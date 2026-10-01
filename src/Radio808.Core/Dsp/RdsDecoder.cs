using System;
using System.Numerics;
using System.Threading;

namespace Radio808.Core.Dsp;

/// <summary>What RDS has told us about the station. Replaced wholesale; readers never lock.</summary>
public sealed record RdsStatus
{
    public bool Synced { get; init; }
    public int Pi { get; init; } = -1;
    /// <summary>US call sign derived from the PI code (RBDS), or null.</summary>
    public string? CallSign { get; init; }
    /// <summary>Program Service name (8 chars; often scrolls on US stations).</summary>
    public string? ProgramService { get; init; }
    public string? RadioText { get; init; }
    public int Pty { get; init; } = -1;
    public string? PtyName => Pty is >= 0 and < 32 ? PtyNames[Pty] : null;
    public bool TrafficProgram { get; init; }
    public bool TrafficAnnouncement { get; init; }
    public long Groups { get; init; }
    /// <summary>Fraction of blocks with uncorrectable errors, recent average.</summary>
    public double BlockErrorRate { get; init; }

    // RBDS (North America) program types
    private static readonly string[] PtyNames =
    {
        "None", "News", "Information", "Sports", "Talk", "Rock", "Classic Rock", "Adult Hits", "Soft Rock", "Top 40",
        "Country", "Oldies", "Soft", "Nostalgia", "Jazz", "Classical", "Rhythm and Blues", "Soft R&B", "Language",
        "Religious Music", "Religious Talk", "Personality", "Public", "College", "Spanish Talk", "Spanish Music",
        "Hip Hop", "", "", "Weather", "Emergency Test", "Emergency",
    };
}

/// <summary>
/// RDS/RBDS decoder. Input: FM MPX (any rate well above 120 kHz). The 57 kHz subcarrier is mixed to baseband,
/// filtered, and resampled to exactly 16 samples per bit (19 kS/s). A biphase matched filter is sampled at the phase
/// with the most energy; bits come from differential detection (no carrier PLL needed, and it works on mono
/// stations). Then block sync on the 10-bit checkwords, and group decoding.
/// </summary>
public sealed class RdsDecoder
{
    private const double BitRate = 1187.5;
    private const int Sps = 16;
    private const double SymRate = BitRate * Sps;   // 19000
    private const int Dec = 8;

    private readonly FirDecimator _li, _lq;
    private readonly double _ncoStep, _rsStep;
    private double _ncoPhase, _rsPos = 1;
    private float[] _bi = Array.Empty<float>(), _bq = Array.Empty<float>(), _di = Array.Empty<float>(), _dq = Array.Empty<float>();
    private readonly Complex[] _hist = new Complex[3];   // last decimated samples, for cubic interpolation
    private int _histCount;

    // matched filter + timing
    private readonly Complex[] _ring = new Complex[Sps];
    private int _ringPos;
    private long _n;
    private readonly double[] _energy = new double[Sps];
    private int _best;
    private Complex _prevSym = Complex.One;

    // block sync
    private uint _reg;
    private long _bitCount;
    private bool _synced;
    private int _expect;            // 0..3 = A, B, C/C', D
    private int _bitsInBlock;
    private long _lastFoundBit = -1000;
    private int _lastFoundIdx = -1;
    private readonly ushort[] _blocks = new ushort[4];
    private readonly bool[] _valid = new bool[4];
    private double _errAvg;
    private int _badRun;

    // group state
    private readonly char[] _ps = "        ".ToCharArray();
    private int _psSeen;
    private readonly char[] _rt = new string(' ', 64).ToCharArray();
    private int _rtSeen;            // bit per segment
    private int _rtAb = -1;
    private RdsStatus _status = new();

    private static readonly ushort[] Offsets = { 0x0FC, 0x198, 0x168, 0x350, 0x1B4 };   // A, B, C, C', D
    private static readonly int[] OffsetIndex = { 0, 1, 2, 2, 3 };

    public RdsDecoder(double mpxRate)
    {
        _ncoStep = 2 * Math.PI * 57_000 / mpxRate;
        var taps = FirDesign.LowPass(2_400, 6_000, mpxRate, 60);
        _li = new FirDecimator(taps, Dec);
        _lq = new FirDecimator(taps, Dec);
        _rsStep = mpxRate / Dec / SymRate;
    }

    public RdsStatus Status => Volatile.Read(ref _status);

    public void Process(ReadOnlySpan<float> mpx)
    {
        int n = mpx.Length;
        if (_bi.Length < n) { _bi = new float[n]; _bq = new float[n]; _di = new float[n / Dec + 2]; _dq = new float[n / Dec + 2]; }
        double ph = _ncoPhase, step = _ncoStep;
        for (int k = 0; k < n; k++)
        {
            float x = mpx[k];
            _bi[k] = x * MathF.Cos((float)ph);
            _bq[k] = -x * MathF.Sin((float)ph);
            ph += step;
            if (ph > Math.PI) ph -= 2 * Math.PI;
        }
        _ncoPhase = ph;
        int m = _li.Process(_bi.AsSpan(0, n), _di);
        _lq.Process(_bq.AsSpan(0, n), _dq);

        // cubic resample to 19 kS/s: points p0..p3 = hist[0..2], current sample
        for (int k = 0; k < m; k++)
        {
            var cur = new Complex(_di[k], _dq[k]);
            if (_histCount < 3) { _hist[_histCount++] = cur; continue; }
            // positions: hist[0] = -2, hist[1] = -1, hist[2] = 0, cur = +1; interpolate in [hist[1], hist[2]] + t
            while (_rsPos < 1)
            {
                double t = _rsPos;
                Complex p0 = _hist[0], p1 = _hist[1], p2 = _hist[2], p3 = cur;
                var v = p1 + 0.5 * t * (p2 - p0 + t * (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3 + t * (3.0 * (p1 - p2) + p3 - p0)));
                Symbol(v);
                _rsPos += _rsStep;
            }
            _rsPos -= 1;
            _hist[0] = _hist[1]; _hist[1] = _hist[2]; _hist[2] = cur;
        }
    }

    /// <summary>One sample at 16 per bit: biphase matched filter, timing, differential bit decision.</summary>
    private void Symbol(Complex v)
    {
        _ring[_ringPos] = v;
        _ringPos = (_ringPos + 1) % Sps;
        // matched filter over the last bit: first half minus second half
        Complex mf = Complex.Zero;
        for (int k = 0; k < Sps; k++)
        {
            var s = _ring[(_ringPos + k) % Sps];
            mf += k < Sps / 2 ? s : -s;
        }
        int bin = (int)(_n % Sps);
        _energy[bin] += 0.002 * (mf.Real * mf.Real + mf.Imaginary * mf.Imaginary - _energy[bin]);
        if (bin == Sps - 1)
        {
            int b = _best;
            for (int k = 0; k < Sps; k++) if (_energy[k] > _energy[b] * 1.15) b = k;
            _best = b;
        }
        if (bin == _best)
        {
            double d = (mf * Complex.Conjugate(_prevSym)).Real;
            _prevSym = mf;
            Bit(d < 0 ? 1u : 0u);
        }
        _n++;
    }

    // ------------------------------------------------------------------ blocks

    private static uint Check(uint info)
    {
        uint r = info << 10;
        for (int i = 25; i >= 10; i--)
            if (((r >> i) & 1) != 0) r ^= 0x5B9u << (i - 10);
        return r & 0x3FF;
    }

    /// <summary>Offset index (0..4) the 26-bit block matches, or -1.</summary>
    private static int BlockType(uint block)
    {
        uint check = Check(block >> 10) ^ (block & 0x3FF);
        for (int t = 0; t < 5; t++) if (check == Offsets[t]) return t;
        return -1;
    }

    private void Bit(uint b)
    {
        _reg = ((_reg << 1) | b) & 0x3FFFFFF;
        _bitCount++;
        if (!_synced)
        {
            int t = BlockType(_reg);
            if (t < 0) return;
            int idx = OffsetIndex[t];
            long dist = _bitCount - _lastFoundBit;
            if (_lastFoundIdx >= 0 && dist % 26 == 0 && dist <= 26 * 4 && (_lastFoundIdx + dist / 26) % 4 == idx)
            {
                _synced = true;
                _badRun = 0;
                Array.Clear(_valid);   // blocks before this one belong to no group yet
                _expect = idx;
                _bitsInBlock = 26;   // _reg holds this block now
                StoreBlock(_reg, t, true);
                return;
            }
            _lastFoundBit = _bitCount;
            _lastFoundIdx = idx;
            return;
        }
        if (--_bitsInBlock > 0) return;
        _bitsInBlock = 26;
        _expect = (_expect + 1) % 4;
        uint block = _reg;
        int type = BlockType(block);
        bool ok = type >= 0 && OffsetIndex[type] == _expect;
        if (!ok && TryCorrect(ref block, out type)) ok = OffsetIndex[type] == _expect;
        StoreBlock(block, type, ok);
    }

    /// <summary>Corrects a single-bit error or a 2-bit burst when that yields the expected block type.</summary>
    private bool TryCorrect(ref uint block, out int type)
    {
        for (int i = 0; i < 26; i++)
        {
            foreach (uint mask in new[] { 1u << i, i < 25 ? 3u << i : 0 })
            {
                if (mask == 0) continue;
                uint c = block ^ mask;
                int t = BlockType(c);
                if (t >= 0 && OffsetIndex[t] == _expect) { block = c; type = t; return true; }
            }
        }
        type = -1;
        return false;
    }

    private void StoreBlock(uint block, int type, bool ok)
    {
        int idx = _expect;
        _blocks[idx] = (ushort)(block >> 10);
        _valid[idx] = ok;
        if (idx == 2) _cPrime = ok && type == 3;
        _errAvg += 0.02 * ((ok ? 0 : 1) - _errAvg);
        _badRun = ok ? 0 : _badRun + 1;
        if (_badRun >= 12)   // ~0.25 s of garbage: resync
        {
            _synced = false;
            _lastFoundIdx = -1;
            Publish(s => s with { Synced = false, BlockErrorRate = _errAvg });
            return;
        }
        if (idx == 3) Group();
    }

    private bool _cPrime;

    // ------------------------------------------------------------------ groups

    private void Group()
    {
        int pi = -1;
        if (_valid[0]) pi = _blocks[0];
        else if (_valid[2] && _cPrime) pi = _blocks[2];
        if (!_valid[1])
        {
            if (pi >= 0) SetPi(pi);
            return;
        }
        ushort b = _blocks[1];
        int groupType = b >> 12;
        bool versionB = ((b >> 11) & 1) != 0;
        bool tp = ((b >> 10) & 1) != 0;
        int pty = (b >> 5) & 0x1F;

        var s0 = Status;
        if (pi >= 0 && pi != s0.Pi) ResetText();   // a different station: old text no longer applies
        Publish(s => s with
        {
            Synced = true,
            Pi = pi >= 0 ? pi : s.Pi,
            CallSign = pi >= 0 ? CallSignFromPi(pi) : s.CallSign,
            Pty = pty,
            TrafficProgram = tp,
            Groups = s.Groups + 1,
            BlockErrorRate = _errAvg,
        });

        if (groupType == 0 && _valid[3])
        {
            bool ta = ((b >> 4) & 1) != 0;
            int seg = b & 3;
            ushort d = _blocks[3];
            _ps[2 * seg] = Char((byte)(d >> 8));
            _ps[2 * seg + 1] = Char((byte)d);
            _psSeen |= 1 << seg;
            if (_psSeen == 0xF)
            {
                string ps = new string(_ps).Trim();
                _psSeen = 0;
                Publish(s => s with { ProgramService = ps.Length > 0 ? ps : s.ProgramService, TrafficAnnouncement = ta });
            }
        }
        else if (groupType == 2)
        {
            int ab = (b >> 4) & 1;
            if (ab != _rtAb)
            {
                if (_rtAb >= 0) { Array.Fill(_rt, ' '); _rtSeen = 0; }
                _rtAb = ab;
            }
            int seg = b & 0xF;
            if (!versionB)
            {
                if (_valid[2]) { _rt[4 * seg] = Char((byte)(_blocks[2] >> 8)); _rt[4 * seg + 1] = Char((byte)_blocks[2]); }
                if (_valid[3]) { _rt[4 * seg + 2] = Char((byte)(_blocks[3] >> 8)); _rt[4 * seg + 3] = Char((byte)_blocks[3]); }
                if (_valid[2] && _valid[3]) _rtSeen |= 1 << seg;
            }
            else if (_valid[3])
            {
                _rt[2 * seg] = Char((byte)(_blocks[3] >> 8));
                _rt[2 * seg + 1] = Char((byte)_blocks[3]);
                _rtSeen |= 1 << seg;
            }
            // complete when every segment up to the end-of-text marker (or the end) has arrived
            int len = Array.IndexOf(_rt, '\r');
            int max = versionB ? 32 : 64;
            if (len < 0 || len > max) len = max;
            int perSeg = versionB ? 2 : 4;
            int needed = (len + perSeg - 1) / perSeg;
            int mask = needed >= 16 ? 0xFFFF : (1 << needed) - 1;
            if (needed > 0 && (_rtSeen & mask) == mask)
            {
                string rt = new string(_rt, 0, len).Replace('\r', ' ').Trim();
                if (rt.Length > 0) Publish(s => s with { RadioText = rt });
            }
        }
    }

    private void SetPi(int pi) =>
        Publish(s => s.Pi == pi ? s : s with { Pi = pi, CallSign = CallSignFromPi(pi) });

    private void ResetText()
    {
        Array.Fill(_ps, ' '); _psSeen = 0;
        Array.Fill(_rt, ' '); _rtSeen = 0; _rtAb = -1;
        Publish(s => s with { ProgramService = null, RadioText = null });
    }

    /// <summary>RDS uses its own character table; ASCII-compatible in 0x20..0x7D.</summary>
    private static char Char(byte c) => c == 0x0D ? '\r' : c is >= 0x20 and <= 0x7D ? (char)c : ' ';

    /// <summary>US RBDS call sign from the PI code (NRSC-4 Annex D), or null when the PI doesn't encode one.</summary>
    public static string? CallSignFromPi(int pi)
    {
        if ((pi & 0xFF00) == 0xAF00) pi = (pi & 0xFF) << 8;                       // AFxx -> xx00
        if ((pi & 0xF000) == 0xA000) pi = ((pi & 0x0F00) << 4) | (pi & 0xFF);     // Axyz -> x0yz
        if (pi < 0x1000 || pi > 0x994F) return null;
        char first = pi >= 0x54A8 ? 'W' : 'K';
        int code = pi - (pi >= 0x54A8 ? 0x54A8 : 0x1000);
        return $"{first}{(char)('A' + code / 676)}{(char)('A' + code % 676 / 26)}{(char)('A' + code % 26)}";
    }

    private void Publish(Func<RdsStatus, RdsStatus> change) => Volatile.Write(ref _status, change(_status));

    public void Reset()
    {
        _li.Reset(); _lq.Reset();
        _histCount = 0; _rsPos = 1;
        Array.Clear(_ring); Array.Clear(_energy);
        _synced = false; _lastFoundIdx = -1; _errAvg = 0; _badRun = 0;
        Array.Fill(_ps, ' '); _psSeen = 0; Array.Fill(_rt, ' '); _rtSeen = 0; _rtAb = -1;
        Volatile.Write(ref _status, new RdsStatus());
    }
}
