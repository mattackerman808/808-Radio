using System;

namespace Radio808.Core.Dsp;

/// <summary>
/// FM stereo decoder: MPX in (normalized so 75 kHz deviation = 1.0), stereo audio out at 1/4 of the MPX rate.
/// A PLL locks to the 19 kHz pilot; L-R is demodulated with the doubled pilot phase (38 kHz). Stereo separation is
/// blended toward mono as the pilot's SNR falls, so weak stations hiss less. Output is de-emphasized.
/// </summary>
public sealed class StereoDecoder
{
    public const int Decimation = 4;

    private readonly double _fs;
    private readonly FirDecimator _sum, _diff;
    private float[] _m = new float[0], _s = new float[0], _mOut = new float[0], _sOut = new float[0];

    // pilot PLL
    private double _theta, _omega;
    private readonly double _omega0, _omegaLimit, _kp, _ki;
    private readonly float _lpA;
    private float _pi, _pq, _pi1, _pq1, _pi2, _pq2;   // 3-pole phase detector lowpass (stages 1, 2, output)
    // lock / SNR / blend
    private float _lockAvg, _noise = 1e-6f, _blend;
    private float _deL, _deR;
    private readonly float _deA;

    /// <param name="mpxRate">Sample rate of the MPX input.</param>
    /// <param name="deemphasisUs">75 (Americas, Korea) or 50 (elsewhere).</param>
    public StereoDecoder(double mpxRate, double deemphasisUs = 75)
    {
        _fs = mpxRate;
        // Audio filter: pass 15 kHz, and remove the 19 kHz pilot (> 70 dB) before decimating by 4.
        var taps = FirDesign.LowPass(15_000, 18_600, mpxRate, 72);
        _sum = new FirDecimator(taps, Decimation);
        _diff = new FirDecimator(taps, Decimation);

        _omega0 = 2 * Math.PI * 19_000 / mpxRate;
        _omegaLimit = 2 * Math.PI * 30 / mpxRate;              // +-30 Hz pull range (pilot is crystal accurate)
        double wn = 2 * Math.PI * 12 / mpxRate;                 // ~12 Hz loop bandwidth
        _kp = 2 * 0.707 * wn;
        _ki = wn * wn;
        _omega = _omega0;
        // Phase detector lowpass: 3 poles at 150 Hz, ~85 dB down 4 kHz away, where 15 kHz audio and the
        // 23 kHz L-R band would otherwise leak in.
        _lpA = (float)(1 - Math.Exp(-2 * Math.PI * 150 / mpxRate));
        // De-emphasis runs at the MPX rate on L+R and L-R (before decimation): a one-pole filter there tracks the
        // analog 1/(1 + j w tau) within 0.1 dB to 15 kHz; at the 46.5 kHz audio rate it read +1.4 dB high at 15 kHz.
        _deA = (float)(1 - Math.Exp(-1 / (mpxRate * deemphasisUs * 1e-6)));
    }

    public double OutputRate => _fs / Decimation;
    /// <summary>Pilot detected and the PLL is locked.</summary>
    public bool PilotLocked => _lockAvg > 0.7f;
    /// <summary>Pilot level relative to full deviation (nominally ~0.09).</summary>
    public float PilotLevel => 2 * MathF.Sqrt(_pi * _pi + _pq * _pq);
    /// <summary>Pilot SNR in a 250 Hz band, dB. Drives the stereo blend.</summary>
    public float PilotSnrDb => 10 * MathF.Log10(_pi * _pi / _noise + 1e-9f);
    /// <summary>0 = mono, 1 = full stereo.</summary>
    public float Blend => _blend;
    /// <summary>Force mono output.</summary>
    public bool ForceMono { get; set; }

    public int MaxOutputFrames(int mpxLength) => mpxLength / Decimation + 1;

    /// <summary>Decodes MPX samples; writes interleaved L/R to <paramref name="stereo"/> and returns the frame count.</summary>
    public int Process(ReadOnlySpan<float> mpx, Span<float> stereo)
    {
        int n = mpx.Length;
        if (_m.Length < n) { _m = new float[n]; _s = new float[n]; }
        double theta = _theta, omega = _omega;
        float pi = _pi, pq = _pq, pi1 = _pi1, pq1 = _pq1, pi2 = _pi2, pq2 = _pq2, a = _lpA;
        float dm = _deL, ds = _deR, da = _deA;   // de-emphasis state for L+R and L-R
        for (int k = 0; k < n; k++)
        {
            float x = mpx[k];
            float c = MathF.Cos((float)theta), s = MathF.Sin((float)theta);
            // phase detector: mix the pilot down to DC
            pi1 += a * (x * c - pi1); pi2 += a * (pi1 - pi2); pi += a * (pi2 - pi);
            pq1 += a * (-x * s - pq1); pq2 += a * (pq1 - pq2); pq += a * (pq2 - pq);
            double err = MathF.Atan2(pq, pi);
            omega += _ki * err;
            if (omega > _omega0 + _omegaLimit) omega = _omega0 + _omegaLimit;
            else if (omega < _omega0 - _omegaLimit) omega = _omega0 - _omegaLimit;
            theta += omega + _kp * err;
            if (theta > Math.PI) theta -= 2 * Math.PI;
            // The pilot is sin(wt) and the subcarrier sin(2wt). The PLL locks theta to the pilot's cosine phase,
            // theta = wt - pi/2, so sin(2wt) = -sin(2 theta) = -2 sin(theta) cos(theta).
            // (De-emphasizing L-R here, before its lowpass, also de-emphasizes the 38 kHz products; the audio
            // filter removes those.)
            dm += da * (x - dm);
            ds += da * (x * -4 * s * c - ds);
            _m[k] = dm;
            _s[k] = ds;
        }
        _theta = theta; _omega = omega; _pi = pi; _pq = pq; _pi1 = pi1; _pq1 = pq1; _pi2 = pi2; _pq2 = pq2;
        _deL = dm; _deR = ds;

        // lock and noise tracking, once per block
        float amp = PilotLevel;
        bool inLock = amp > 0.02f && pi > 0.9f * MathF.Sqrt(pi * pi + pq * pq);
        float blockSec = (float)(n / _fs);
        _lockAvg += Math.Min(1f, blockSec / 0.3f) * ((inLock ? 1f : 0f) - _lockAvg);
        _noise += Math.Min(1f, blockSec / 0.5f) * (pq * pq + 1e-9f - _noise);

        int max = n / Decimation + 1;
        if (_mOut.Length < max) { _mOut = new float[max]; _sOut = new float[max]; }
        int frames = _sum.Process(_m.AsSpan(0, n), _mOut);
        _diff.Process(_s.AsSpan(0, n), _sOut);

        // blend: full stereo above 32 dB pilot SNR, mono below 18 dB; moves slowly to avoid pumping
        float target = 0;
        if (!ForceMono && PilotLocked) target = Math.Clamp((PilotSnrDb - 18f) / 14f, 0f, 1f);
        float blend = _blend, step = 1f / (float)(OutputRate * 0.4);
        for (int k = 0; k < frames; k++)
        {
            blend += Math.Clamp(target - blend, -step, step);
            float m = _mOut[k], d = _sOut[k] * blend;
            stereo[2 * k] = m + d;
            stereo[2 * k + 1] = m - d;
        }
        _blend = blend;
        return frames;
    }

    public void Reset()
    {
        _sum.Reset(); _diff.Reset();
        _theta = 0; _omega = _omega0; _pi = _pq = _pi1 = _pq1 = _pi2 = _pq2 = 0; _lockAvg = 0; _noise = 1e-6f; _blend = 0; _deL = _deR = 0;
    }
}
