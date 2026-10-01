using System;
using System.Numerics;

namespace Radio808.Core.Dsp;

public static class FirDesign
{
    /// <summary>
    /// Kaiser-window lowpass. The tap count follows from the transition band and attenuation (always odd, so the
    /// filter is symmetric with an integer group delay of (taps - 1) / 2).
    /// </summary>
    public static float[] LowPass(double passHz, double stopHz, double fs, double attenDb = 70, double gain = 1)
    {
        double dw = 2 * Math.PI * (stopHz - passHz) / fs;
        int n = (int)Math.Ceiling((attenDb - 8) / (2.285 * dw)) + 1;
        if (n % 2 == 0) n++;
        double beta = attenDb > 50 ? 0.1102 * (attenDb - 8.7)
                    : attenDb >= 21 ? 0.5842 * Math.Pow(attenDb - 21, 0.4) + 0.07886 * (attenDb - 21) : 0;
        double fc = (passHz + stopHz) / 2 / fs;   // cycles per sample
        var h = new double[n];
        double sum = 0, i0b = BesselI0(beta);
        int mid = n / 2;
        for (int i = 0; i < n; i++)
        {
            int x = i - mid;
            double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double r = 2.0 * i / (n - 1) - 1;
            h[i] = sinc * BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
            sum += h[i];
        }
        var taps = new float[n];
        for (int i = 0; i < n; i++) taps[i] = (float)(h[i] * gain / sum);
        return taps;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 50; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }

    /// <summary>Gain of a filter at a frequency, in dB (for tests and diagnostics).</summary>
    public static double ResponseDb(float[] taps, double hz, double fs)
    {
        double re = 0, im = 0, w = 2 * Math.PI * hz / fs;
        for (int i = 0; i < taps.Length; i++) { re += taps[i] * Math.Cos(w * i); im -= taps[i] * Math.Sin(w * i); }
        return 10 * Math.Log10(re * re + im * im + 1e-30);
    }
}

/// <summary>Streaming real FIR filter with integer decimation (decimation 1 = plain filter).</summary>
public sealed class FirDecimator
{
    private readonly float[] _taps;
    private readonly int _m;
    private float[] _buf;
    private int _count;   // valid samples in _buf (starts with taps-1 zeros of history)

    public FirDecimator(float[] taps, int decimation = 1)
    {
        if (decimation < 1 || decimation > taps.Length) throw new ArgumentOutOfRangeException(nameof(decimation));
        _taps = taps;
        _m = decimation;
        _buf = new float[taps.Length * 2 + 8192];
        _count = taps.Length - 1;
    }

    public int Taps => _taps.Length;
    public int Decimation => _m;

    /// <summary>Upper bound on outputs produced for an input of the given length.</summary>
    public int MaxOutput(int inputLength) => inputLength / _m + 1;

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int need = _count + input.Length;
        if (_buf.Length < need) Array.Resize(ref _buf, need * 2);
        input.CopyTo(_buf.AsSpan(_count));
        _count = need;
        int t = _taps.Length, pos = 0, n = 0;
        ReadOnlySpan<float> taps = _taps;
        while (pos + t <= _count)
        {
            output[n++] = Dot(new ReadOnlySpan<float>(_buf, pos, t), taps);
            pos += _m;
        }
        int keep = _count - pos;
        Array.Copy(_buf, pos, _buf, 0, keep);
        _count = keep;
        return n;
    }

    public void Reset()
    {
        Array.Clear(_buf);
        _count = _taps.Length - 1;
    }

    internal static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0, w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        for (; i <= a.Length - w; i += w)
            acc += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
        float s = Vector.Sum(acc);
        for (; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
