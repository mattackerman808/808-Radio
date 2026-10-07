using System;
using Radio808.Core.Dsp;

namespace Radio808.Core.Radio;

/// <summary>
/// The station's signal from the spectrum of the dongle's whole window (±744 kHz), for the gain optimizer: the analog
/// carrier and the two HD sidebands, each over the noise floor. Unlike ADC clipping this also sees the tuner itself
/// overloading: next to a strong station the floor rises faster than the gain while nothing clips (104.9 beside the
/// 105.3-105.7 cluster, 2026-10-07: HD sidebands 11.7 dB over the floor at 7.7 dB gain, 5.8 at 16.6, 1.8 at 22.9,
/// where the ADC still wasn't clipping). And unlike MER it's there before HD syncs.
/// </summary>
internal sealed class SignalQuality
{
    private const int N = 4096;                                   // 363 Hz bins: one per HD subcarrier
    private const double Bin = FmReceiver.DeviceRate / N;
    private const int Interval = (int)(FmReceiver.DeviceRate * 0.025);   // one block every 25 ms
    private const double FloorSpan = 650_000;                    // inside the RTL2832's anti-alias roll-off
    private const double FloorPercentile = 0.10;

    /// <summary>One averaged measurement. Levels are dB over the noise floor; Blocks = 0 means no data.</summary>
    public readonly record struct Reading(int Blocks, double FloorDb, double CarrierDb, double LowerDb, double UpperDb, double GapDb)
    {
        /// <summary>The two HD sidebands together (the decoder uses both).</summary>
        public double SidebandDb => (LowerDb + UpperDb) / 2;

        /// <summary>
        /// HD sidebands are on the air: both stand clear of the floor and of the empty gap between them and the
        /// analog signal (broad energy from a neighbour fills the gap too).
        /// </summary>
        public bool HdVisible => Blocks > 0 && Math.Min(LowerDb, UpperDb) > 4 && Math.Min(LowerDb, UpperDb) > GapDb + 3;
    }

    private readonly Fft _fft = new(N);
    private readonly float[] _re = new float[N], _im = new float[N], _win = new float[N];
    private readonly double[] _acc = new double[N];
    private readonly object _lock = new();
    private int _fill, _wait, _blocks;

    public SignalQuality()
    {
        for (int i = 0; i < N; i++) _win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
    }

    /// <summary>Feeds device-rate I/Q, centered on the station (device thread).</summary>
    public void Add(ReadOnlySpan<float> iq)
    {
        int n = iq.Length / 2, k = 0;
        while (k < n)
        {
            if (_wait > 0)
            {
                int s = Math.Min(_wait, n - k);
                _wait -= s; k += s;
                continue;
            }
            int m = Math.Min(N - _fill, n - k);
            for (int t = 0; t < m; t++)
            {
                int p = _fill + t, q = 2 * (k + t);
                _re[p] = iq[q] * _win[p];
                _im[p] = iq[q + 1] * _win[p];
            }
            _fill += m; k += m;
            if (_fill < N) continue;
            _fft.Transform(_re, _im);
            lock (_lock)
            {
                for (int i = 0; i < N; i++) _acc[i] += _re[i] * _re[i] + _im[i] * _im[i];
                _blocks++;
            }
            _fill = 0;
            _wait = Interval - N;
        }
    }

    /// <summary>Drops everything measured so far (device thread, on a retune).</summary>
    public void Reset()
    {
        lock (_lock) { Array.Clear(_acc); _blocks = 0; }
        _fill = 0; _wait = 0;
    }

    /// <summary>The average since the last call, and starts a new one.</summary>
    public Reading Take()
    {
        var p = new double[N];
        int blocks;
        lock (_lock)
        {
            if (_blocks == 0) return default;
            Array.Copy(_acc, p, N);
            Array.Clear(_acc);
            blocks = _blocks;
            _blocks = 0;
        }

        int span = (int)(FloorSpan / Bin);
        var floorBins = new double[2 * span + 1];
        for (int k = -span; k <= span; k++) floorBins[k + span] = p[(k + N) % N];
        Array.Sort(floorBins);
        double floor = 10 * Math.Log10(floorBins[(int)(FloorPercentile * floorBins.Length)] + 1e-30);

        double Band(double lo, double hi)
        {
            double sum = 0; int count = 0;
            for (int k = (int)Math.Ceiling(lo / Bin); k <= (int)Math.Floor(hi / Bin); k++) { sum += p[(k + N) % N]; count++; }
            return 10 * Math.Log10(sum / Math.Max(1, count) + 1e-30) - floor;
        }

        // hybrid HD: OFDM sidebands 129-198 kHz either side of the carrier, an empty gap inside them
        return new Reading(blocks, floor,
            CarrierDb: Band(-50_000, 50_000),
            LowerDb: Band(-198_000, -129_000),
            UpperDb: Band(129_000, 198_000),
            GapDb: (Band(-125_000, -105_000) + Band(105_000, 125_000)) / 2);
    }
}
