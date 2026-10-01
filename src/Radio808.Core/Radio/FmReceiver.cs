using System;
using Radio808.Core.Dsp;

namespace Radio808.Core.Radio;

/// <summary>Interleaved stereo audio (L, R, L, R ...).</summary>
public delegate void AudioHandler(Span<float> stereo);

/// <summary>Complex baseband as separate I and Q arrays.</summary>
public delegate void BasebandHandler(ReadOnlySpan<float> i, ReadOnlySpan<float> q);

/// <summary>
/// Analog FM broadcast receiver. Input: interleaved I/Q at <see cref="DeviceRate"/>, tuned to the station.
/// <code>
/// 1,488,375  -halfband/2->  744,187.5 (HD baseband)  -channel/2->  372,093.75  -discriminator->  MPX
/// MPX  -/2->  186,046.875  -stereo decoder/4->  46,511.72 audio
/// </code>
/// Every stage is a fixed integer ratio of the device clock, so audio and the HD baseband share one timeline.
/// </summary>
public sealed class FmReceiver
{
    public const double DeviceRate = 1_488_375;
    public const double HdRate = DeviceRate / 2;           // nrsc5's native FM rate
    public const double ChannelRate = DeviceRate / 4;
    public const double MpxRate = DeviceRate / 8;
    public const double AudioRate = MpxRate / StereoDecoder.Decimation;
    private const double Deviation = 75_000;

    private readonly FirDecimator _hbI, _hbQ, _chI, _chQ, _mpxDec;
    private readonly StereoDecoder _stereo;
    private readonly CmaEqualizer _eq;
    private readonly RdsDecoder _rds = new(MpxRate);
    private float[] _i = new float[0], _q = new float[0], _hi = new float[0], _hq = new float[0];
    private float[] _ci = new float[0], _cq = new float[0], _mpx = new float[0], _mpx2 = new float[0], _audio = new float[0];
    private float _prevI = 1, _prevQ;
    private readonly float _discGain;
    private double _powerAvg = 1e-9;

    /// <summary>The 744,187.5 S/s complex baseband (for the HD decoder).</summary>
    public event BasebandHandler? Baseband;
    /// <summary>Stereo audio at <see cref="AudioRate"/>.</summary>
    public event AudioHandler? Audio;

    public FmReceiver(double deemphasisUs = 75, double channelPassHz = 100_000, int eqTaps = 16, float eqMu = 2e-4f)
    {
        _eq = new CmaEqualizer(eqTaps, eqMu);
        // /2 to the HD rate: keep +-230 kHz flat; everything that would alias into it is > 70 dB down
        var hb = FirDesign.LowPass(230_000, 514_000, DeviceRate, 70);
        _hbI = new FirDecimator(hb, 2); _hbQ = new FirDecimator(hb, 2);
        // FM channel: +-100 kHz, rejects the HD sidebands (from ~129 kHz) and neighbours
        var ch = FirDesign.LowPass(channelPassHz, 128_000, HdRate, 70);
        _chI = new FirDecimator(ch, 2); _chQ = new FirDecimator(ch, 2);
        // MPX: keep 0-60 kHz (mono, pilot, stereo, RDS)
        _mpxDec = new FirDecimator(FirDesign.LowPass(60_000, 120_000, ChannelRate, 70), 2);
        _stereo = new StereoDecoder(MpxRate, deemphasisUs);
        _discGain = (float)(ChannelRate / (2 * Math.PI * Deviation));
    }

    public StereoDecoder Stereo => _stereo;
    /// <summary>Multipath equalizer on the FM channel.</summary>
    public CmaEqualizer Equalizer => _eq;
    /// <summary>RDS station data (analog).</summary>
    public RdsStatus Rds => _rds.Status;
    /// <summary>Power in the FM channel, dBFS (smoothed).</summary>
    public double ChannelPowerDb => 10 * Math.Log10(_powerAvg);

    public void Process(ReadOnlySpan<float> iq)
    {
        int n = iq.Length / 2;
        Ensure(ref _i, n); Ensure(ref _q, n);
        for (int k = 0; k < n; k++) { _i[k] = iq[2 * k]; _q[k] = iq[2 * k + 1]; }

        // halfband -> HD baseband
        int m1 = _hbI.MaxOutput(n);
        Ensure(ref _hi, m1); Ensure(ref _hq, m1);
        int nh = _hbI.Process(_i.AsSpan(0, n), _hi);
        _hbQ.Process(_q.AsSpan(0, n), _hq);
        Baseband?.Invoke(_hi.AsSpan(0, nh), _hq.AsSpan(0, nh));

        // channel filter
        int m2 = _chI.MaxOutput(nh);
        Ensure(ref _ci, m2); Ensure(ref _cq, m2);
        int nc = _chI.Process(_hi.AsSpan(0, nh), _ci);
        _chQ.Process(_hq.AsSpan(0, nh), _cq);
        double power = 0;
        for (int k = 0; k < nc; k++) power += _ci[k] * _ci[k] + _cq[k] * _cq[k];
        if (nc > 0) _powerAvg += Math.Min(1, nc / (ChannelRate * 0.3)) * (power / nc - _powerAvg);
        _eq.Process(_ci.AsSpan(0, nc), _cq.AsSpan(0, nc));

        // discriminator: angle between successive samples
        Ensure(ref _mpx, nc);
        float pi = _prevI, pq = _prevQ, g = _discGain;
        for (int k = 0; k < nc; k++)
        {
            float x = _ci[k], y = _cq[k];
            _mpx[k] = g * MathF.Atan2(y * pi - x * pq, x * pi + y * pq);
            pi = x; pq = y;
        }
        _prevI = pi; _prevQ = pq;

        // MPX -> stereo audio
        Ensure(ref _mpx2, _mpxDec.MaxOutput(nc));
        int nm = _mpxDec.Process(_mpx.AsSpan(0, nc), _mpx2);
        _rds.Process(_mpx2.AsSpan(0, nm));
        Ensure(ref _audio, 2 * _stereo.MaxOutputFrames(nm));
        int frames = _stereo.Process(_mpx2.AsSpan(0, nm), _audio);
        Audio?.Invoke(_audio.AsSpan(0, 2 * frames));
    }

    /// <summary>Clears filter state, e.g. after retuning.</summary>
    public void Reset()
    {
        _hbI.Reset(); _hbQ.Reset(); _chI.Reset(); _chQ.Reset(); _mpxDec.Reset(); _stereo.Reset(); _eq.Reset(); _rds.Reset();
        _prevI = 1; _prevQ = 0; _powerAvg = 1e-9;
    }

    private static void Ensure(ref float[] a, int n)
    {
        if (a.Length < n) a = new float[Math.Max(n, a.Length * 2)];
    }
}
