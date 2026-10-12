using System;

namespace Radio808.Core.Hd;

public enum LowRateEqMode { Off, Auto, Strong }

/// <summary>
/// Tone correction for starved HD programs. A subchannel at 20-odd kbit/s comes out of the HDC encoder with its bass
/// pulled down, a hump around 500 Hz - 1 kHz, and a fizzy synthesized top: measured on several stations, the shape
/// is the same wherever the bit rate is, so the correction follows the measured rate rather than a per-station
/// preset.
/// Nothing is applied at 48 kbit/s and up (a main channel's usual share); the full curve at 24 and below.
/// Three biquads per channel (low shelf, mid dip, high shelf) plus a trim for headroom, at the HD rate of 44.1 kHz.
/// </summary>
public sealed class LowRateEq
{
    private const double Rate = 44100;
    // The full-strength curve (dB), deliberately mild: a survey of Bay Area subchannels (19-31 kbit/s) found the dead
    // top octave and the collapsed stereo above 8 kHz everywhere, but the gutted bass and the midrange hump only on
    // some stations (99.7 HD2 badly, others not at all), so Auto corrects what is common and Strong doubles it for
    // the stations that need it.
    private const double BassDb = 3, BassHz = 120, MidDb = -2, MidHz = 700, MidQ = 0.9, HighDb = -2, HighHz = 10000, TrimDb = -1.5;
    private const double StrongScale = 2;
    private const double FullKbps = 24, NoneKbps = 48;

    public volatile LowRateEqMode Mode = LowRateEqMode.Auto;
    /// <summary>Bit rate of the program being played, kbit/s; 0 = unknown (no correction).</summary>
    public double Kbps { get; set; }

    private double _strength = -1;   // the strength the coefficients were built for
    private double _smooth;          // follows the target over about a second of packets
    private readonly Biquad[] _f = { new(), new(), new(), new(), new(), new() };   // L: 0..2, R: 3..5
    private float _trim = 1;
    private bool _bypass = true;

    /// <summary>Target strength for the mode and bit rate: 0..1 in Auto, up to 2 in Strong.</summary>
    public double Target
    {
        get
        {
            if (Mode == LowRateEqMode.Off || Kbps <= 0) return 0;
            double s = Math.Clamp((NoneKbps - Kbps) / (NoneKbps - FullKbps), 0, 1);
            return Mode == LowRateEqMode.Strong ? s * StrongScale : s;
        }
    }

    /// <summary>The strength in effect (after smoothing), for the panel.</summary>
    public double Strength => _smooth;

    /// <summary>Forget the filter state and jump to the target (a new program: the old audio's tail means nothing).</summary>
    public void Reset()
    {
        foreach (var f in _f) f.Clear();
        _smooth = Target;
        Build();
    }

    /// <summary>Once per packet, before <see cref="Apply"/>: eases the strength toward the target and rebuilds the filters when it moved.</summary>
    public void Update()
    {
        double t = Target;
        _smooth += (t - _smooth) * 0.08;   // ~1 s of 2048-sample packets to settle
        if (Math.Abs(_smooth - t) < 0.005) _smooth = t;
        if (Math.Abs(_smooth - _strength) >= 0.01) Build();
    }

    public void Apply(ref float l, ref float r)
    {
        if (_bypass) return;
        l = _f[2].Run(_f[1].Run(_f[0].Run(l))) * _trim;
        r = _f[5].Run(_f[4].Run(_f[3].Run(r))) * _trim;
    }

    private void Build()
    {
        double s = _smooth;
        _strength = s;
        _bypass = s < 0.01;
        if (_bypass) return;
        for (int c = 0; c < 6; c += 3)
        {
            _f[c].LowShelf(BassHz, BassDb * s);
            _f[c + 1].Peak(MidHz, MidQ, MidDb * s);
            _f[c + 2].HighShelf(HighHz, HighDb * s);
        }
        _trim = (float)Math.Pow(10, TrimDb * s / 20);
    }

    /// <summary>Direct form I biquad, coefficients from the Audio EQ Cookbook.</summary>
    private sealed class Biquad
    {
        private double _b0 = 1, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public void Clear() { _x1 = _x2 = _y1 = _y2 = 0; }

        public float Run(float x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return (float)y;
        }

        public void LowShelf(double hz, double db) => Shelf(hz, db, true);
        public void HighShelf(double hz, double db) => Shelf(hz, db, false);

        private void Shelf(double hz, double db, bool low)
        {
            double A = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / Rate, cs = Math.Cos(w), sn = Math.Sin(w);
            double alpha = sn / 2 * Math.Sqrt((A + 1 / A) * (1 / 1.0 - 1) + 2);   // S = 1
            double sq = 2 * Math.Sqrt(A) * alpha, sign = low ? 1 : -1;
            double b0 = A * ((A + 1) - sign * (A - 1) * cs + sq);
            double b1 = sign * 2 * A * ((A - 1) - sign * (A + 1) * cs);
            double b2 = A * ((A + 1) - sign * (A - 1) * cs - sq);
            double a0 = (A + 1) + sign * (A - 1) * cs + sq;
            double a1 = sign * -2 * ((A - 1) + sign * (A + 1) * cs);
            double a2 = (A + 1) + sign * (A - 1) * cs - sq;
            Set(b0, b1, b2, a0, a1, a2);
        }

        public void Peak(double hz, double q, double db)
        {
            double A = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / Rate, cs = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            Set(1 + alpha * A, -2 * cs, 1 - alpha * A, 1 + alpha / A, -2 * cs, 1 - alpha / A);
        }

        private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }
    }
}
