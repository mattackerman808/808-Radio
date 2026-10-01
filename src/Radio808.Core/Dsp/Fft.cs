using System;

namespace Radio808.Core.Dsp;

/// <summary>In-place radix-2 complex FFT with cached twiddles (for displays; not on the audio path).</summary>
public sealed class Fft
{
    private readonly int _n;
    private readonly float[] _cos, _sin;
    private readonly float[] _window;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("size must be a power of two", nameof(n));
        _n = n;
        _cos = new float[n / 2]; _sin = new float[n / 2];
        for (int i = 0; i < n / 2; i++) { _cos[i] = (float)Math.Cos(-2 * Math.PI * i / n); _sin[i] = (float)Math.Sin(-2 * Math.PI * i / n); }
        _window = new float[n];
        for (int i = 0; i < n; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
    }

    public int Size => _n;

    /// <summary>
    /// Power spectrum (dB, Hann window) of interleaved I/Q, reordered so index 0 is -fs/2 and n/2 is DC.
    /// </summary>
    public void PowerDb(ReadOnlySpan<float> iq, float[] re, float[] im, float[] outDb)
    {
        int n = _n;
        for (int i = 0; i < n; i++) { re[i] = iq[2 * i] * _window[i]; im[i] = iq[2 * i + 1] * _window[i]; }
        Transform(re, im);
        for (int i = 0; i < n; i++)
        {
            int k = (i + n / 2) % n;
            outDb[i] = 10 * MathF.Log10(re[k] * re[k] + im[k] * im[k] + 1e-20f);
        }
    }

    public void Transform(float[] re, float[] im)
    {
        int n = _n;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len / 2, stride = n / len;
            for (int i = 0; i < n; i += len)
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * stride], wi = _sin[k * stride];
                    int a = i + k, b = a + half;
                    float xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                }
        }
    }
}
