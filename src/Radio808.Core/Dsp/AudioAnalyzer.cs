using System;

namespace Radio808.Core.Dsp;

/// <summary>
/// A car-stereo style audio spectrum analyzer: band levels on a log frequency scale (bass on the left), from a block of
/// mono audio. Like those analyzers it's tilted +3 dB per octave (around 1 kHz), so typical music, whose energy falls
/// off with frequency, reads roughly level across the bands instead of all bass.
/// </summary>
public sealed class AudioAnalyzer
{
    public const int BlockSize = 2048;   // ~44 ms at 46.5 kHz: ~23 Hz resolution, enough to split the bass bands
    public const double LowHz = 40, HighHz = 16_000;

    private readonly Fft _fft = new(BlockSize);
    private readonly float[] _iq = new float[2 * BlockSize], _re = new float[BlockSize], _im = new float[BlockSize], _db = new float[BlockSize];
    private readonly int[] _bin0, _bin1;
    private readonly float[] _tilt;

    public int Bands { get; }
    /// <summary>Band edges in Hz (Bands + 1 values).</summary>
    public double[] Edges { get; }

    public AudioAnalyzer(int bands, double sampleRate)
    {
        Bands = bands;
        Edges = new double[bands + 1];
        _bin0 = new int[bands];
        _bin1 = new int[bands];
        _tilt = new float[bands];
        double binHz = sampleRate / BlockSize;
        for (int b = 0; b <= bands; b++) Edges[b] = LowHz * Math.Pow(HighHz / LowHz, (double)b / bands);
        int prev = 0;
        for (int b = 0; b < bands; b++)
        {
            // positive-frequency bins (index BlockSize/2 is DC after PowerDb's reordering) whose centers fall in the band;
            // the narrow bass bands get at least one bin each, and no bin is used twice
            int k0 = Math.Max(prev + 1, (int)Math.Ceiling(Edges[b] / binHz));
            int k1 = Math.Max(k0, (int)Math.Ceiling(Edges[b + 1] / binHz) - 1);
            _bin0[b] = k0; _bin1[b] = k1;
            prev = k1;
            double center = Math.Sqrt(Edges[b] * Edges[b + 1]);
            _tilt[b] = (float)(3 * Math.Log2(center / 1000));
        }
    }

    /// <summary>
    /// Band levels in dB (tilted; about 0 for a full-scale tone, so music sits roughly between -60 and -10) from
    /// <see cref="BlockSize"/> mono samples.
    /// </summary>
    public void Analyze(ReadOnlySpan<float> audio, float[] bandDb)
    {
        for (int i = 0; i < BlockSize; i++) { _iq[2 * i] = audio[i]; _iq[2 * i + 1] = 0; }
        _fft.PowerDb(_iq, _re, _im, _db);
        // a full-scale sine through a Hann window peaks at amplitude n/4 in its (two-sided) bin
        float full = 20 * MathF.Log10(BlockSize / 4f);
        for (int b = 0; b < Bands; b++)
        {
            double sum = 0;
            for (int k = _bin0[b]; k <= _bin1[b]; k++) sum += Math.Pow(10, _db[BlockSize / 2 + k] / 10);
            bandDb[b] = (float)(10 * Math.Log10(sum + 1e-20)) - full + _tilt[b];
        }
    }
}
