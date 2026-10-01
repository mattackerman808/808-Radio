using System;

namespace Radio808.Core.Dsp;

/// <summary>
/// Blind multipath equalizer for FM (constant modulus algorithm). An FM signal has a constant envelope; reflections
/// arriving microseconds late make it ripple and distort the audio. This adaptive complex FIR learns the inverse of
/// the channel by driving |y| toward a constant: w -= mu * y (|y|^2 - 1) * conj(x).
/// </summary>
public sealed class CmaEqualizer
{
    private readonly int _taps, _center;
    private readonly float[] _wr, _wi;     // weights
    private readonly float[] _xr, _xi;     // delay line, doubled so a window is always contiguous
    private int _pos;
    private float _agc = -1;               // running mean power for input normalization (-1 = not yet seeded)
    private readonly float _mu;
    private double _envVarAcc, _envCount;

    /// <param name="taps">Equalizer length (16 at 372 kS/s spans 43 us, enough for typical reflections).</param>
    /// <param name="mu">Adaptation step for unit-power input.</param>
    public CmaEqualizer(int taps = 16, float mu = 2e-4f)
    {
        _taps = taps;
        _center = taps / 4;
        _wr = new float[taps]; _wi = new float[taps];
        _wr[_center] = 1;
        _xr = new float[2 * taps]; _xi = new float[2 * taps];
        _mu = mu;
    }

    public bool Enabled { get; set; } = true;
    public int TapCount => _taps;

    /// <summary>Envelope ripple, smoothed over ~0.5 s (for displays; doesn't disturb <see cref="TakeEnvelopeRipple"/>).</summary>
    public double Ripple { get; private set; }

    /// <summary>Copies the tap magnitudes (the multipath profile) into <paramref name="mag"/>.</summary>
    public void CopyTapMagnitudes(float[] mag)
    {
        for (int k = 0; k < Math.Min(_taps, mag.Length); k++)
            mag[k] = MathF.Sqrt(_wr[k] * _wr[k] + _wi[k] * _wi[k]);
    }

    /// <summary>Envelope ripple (std dev of |y|^2 around 1) since the last call; ~0 for a clean FM signal.</summary>
    public double TakeEnvelopeRipple()
    {
        double v = _envCount > 0 ? Math.Sqrt(_envVarAcc / _envCount) : 0;
        _envVarAcc = _envCount = 0;
        return v;
    }

    /// <summary>Equalizes in place.</summary>
    public void Process(Span<float> re, Span<float> im)
    {
        int t = _taps;
        float mu = _mu;
        double blockAcc = 0;
        if (_agc < 0 && re.Length > 0)
        {
            // seed the level from this block, so a strong signal doesn't enter the filter 30x too large
            double p = 0;
            for (int n = 0; n < re.Length; n++) p += re[n] * re[n] + im[n] * im[n];
            _agc = (float)Math.Max(p / re.Length, 1e-12);
        }
        for (int n = 0; n < re.Length; n++)
        {
            // normalize to unit power (slow AGC) so mu doesn't depend on signal level
            float pr = re[n], pim = im[n];
            _agc += 1e-4f * (pr * pr + pim * pim - _agc);
            float g = 1f / MathF.Sqrt(_agc + 1e-12f);
            pr *= g; pim *= g;

            _pos = (_pos == 0 ? t : _pos) - 1;
            _xr[_pos] = _xr[_pos + t] = pr;
            _xi[_pos] = _xi[_pos + t] = pim;

            // y = sum w_k x[n-k]   (window x[_pos .. _pos+t) is newest first)
            float yr = 0, yi = 0;
            for (int k = 0; k < t; k++)
            {
                float xr = _xr[_pos + k], xi = _xi[_pos + k];
                yr += _wr[k] * xr - _wi[k] * xi;
                yi += _wr[k] * xi + _wi[k] * xr;
            }
            float mag2 = yr * yr + yi * yi;
            float d = Math.Clamp(mag2 - 1, -2f, 2f);   // clipped so impulsive noise can't kick the weights
            _envVarAcc += d * d; _envCount++;
            blockAcc += d * d;
            if (Enabled)
            {
                // CMA update: w -= mu * e * conj(x), e = y (|y|^2 - 1)
                float er = mu * yr * d, ei = mu * yi * d;
                for (int k = 0; k < t; k++)
                {
                    float xr = _xr[_pos + k], xi = _xi[_pos + k];
                    _wr[k] -= er * xr + ei * xi;
                    _wi[k] -= ei * xr - er * xi;
                }
                re[n] = yr; im[n] = yi;
            }
            else
            {
                re[n] = pr; im[n] = pim;
            }
        }
        if (re.Length > 0)
        {
            double r = Math.Sqrt(blockAcc / re.Length);
            Ripple = Ripple == 0 ? r : Ripple + Math.Min(1, re.Length / 186_000.0) * (r - Ripple);
        }
        if (!float.IsFinite(_wr[_center]) || !float.IsFinite(_agc)) Reset();   // never let a blow-up stick
    }

    public void Reset()
    {
        Array.Clear(_wr); Array.Clear(_wi); Array.Clear(_xr); Array.Clear(_xi);
        _wr[_center] = 1;
        _agc = -1;
    }
}
