using System;
using System.Collections.Generic;
using Radio808.Core.Radio;

namespace Radio808.Tools;

/// <summary>
/// Synthesizes an FM stereo broadcast from known tones (75 us pre-emphasis, pilot, 38 kHz DSB-SC, 75 kHz deviation)
/// at the device rate, runs it through <see cref="FmReceiver"/>, and measures frequency response, stereo separation,
/// and the residual (noise + distortion) of the output.
/// </summary>
internal static class SelfTest
{
    private static readonly double[] LeftTones = { 100, 400, 1000, 3000, 6000, 10000, 13000, 15000 };
    private static readonly double[] RightTones = { 150, 600, 1500, 4000, 8000, 12000, 14000 };
    private const double ToneAmp = 0.03, Tau = 75e-6;

    /// <param name="echoDb">Level of a simulated reflection relative to the direct signal (e.g. -6), or 0 for none.</param>
    /// <param name="echoUs">Reflection delay in microseconds.</param>
    public static int Run(double cnrDb, double echoDb = 0, double echoUs = 0)
    {
        double fs = FmReceiver.DeviceRate;
        const double seconds = 6;
        var rx = new FmReceiver();
        rx.Equalizer.Enabled = Environment.GetEnvironmentVariable("R808_EQ") != "0";
        int echoDelay = (int)Math.Round(echoUs * 1e-6 * fs);
        float echoGain = echoDb < 0 ? (float)Math.Pow(10, echoDb / 20) : 0;
        var hist = new List<(float i, float q)>();   // direct-signal history for the echo
        var outL = new List<float>(); var outR = new List<float>();
        rx.Audio += a => { for (int i = 0; i < a.Length; i += 2) { outL.Add(a[i]); outR.Add(a[i + 1]); } };

        // tone oscillators with pre-emphasis applied as gain and phase: H(f) = 1 + j 2 pi f tau
        var tones = new List<(double f, double amp, double ph, bool left)>();
        var rnd = new Random(1);
        foreach (var f in LeftTones) tones.Add(Pre(f, rnd, true));
        foreach (var f in RightTones) tones.Add(Pre(f, rnd, false));

        int block = 32768;
        var iq = new float[2 * block];
        double phase = 0, noiseAmp = Math.Pow(10, -cnrDb / 20) / Math.Sqrt(2) * Math.Sqrt(fs / 200_000);  // CNR in 200 kHz
        var nr = new Random(2);
        long n = 0, total = (long)(seconds * fs);
        while (n < total)
        {
            for (int k = 0; k < block; k++, n++)
            {
                double t = n / fs, l = 0, r = 0;
                foreach (var tn in tones)
                {
                    double v = tn.amp * Math.Sin(2 * Math.PI * tn.f * t + tn.ph);
                    if (tn.left) l += v; else r += v;
                }
                double pilot = 2 * Math.PI * 19000 * t;
                double mpx = 0.9 * ((l + r) / 2 + (l - r) / 2 * Math.Sin(2 * pilot)) + 0.1 * Math.Sin(pilot);
                phase += 2 * Math.PI * 75000 * mpx / fs;
                double ni = 0, nq = 0;
                if (cnrDb < 200) { ni = Gauss(nr) * noiseAmp; nq = Gauss(nr) * noiseAmp; }
                float di = (float)(0.3 * Math.Cos(phase)), dq = (float)(0.3 * Math.Sin(phase));
                float ei = 0, eq = 0;
                if (echoGain > 0)
                {
                    hist.Add((di, dq));
                    if (hist.Count > echoDelay)
                    {
                        // reflection with a fixed carrier phase rotation (here 120 degrees)
                        var (hi, hq) = hist[0]; hist.RemoveAt(0);
                        float c = -0.5f, s = 0.866f;
                        ei = echoGain * (hi * c - hq * s); eq = echoGain * (hi * s + hq * c);
                    }
                }
                iq[2 * k] = (float)(di + ei + 0.3 * ni);
                iq[2 * k + 1] = (float)(dq + eq + 0.3 * nq);
            }
            rx.Process(iq);
        }

        // analyze the last 3 s
        double afs = FmReceiver.AudioRate;
        int len = (int)(3 * afs), start = outL.Count - len;
        var L = outL.GetRange(start, len).ToArray(); var R = outR.GetRange(start, len).ToArray();
        double t0 = start / afs;
        Console.WriteLine($"EQ {(rx.Equalizer.Enabled ? "on" : "off")}, echo {(echoGain > 0 ? $"{echoDb} dB at {echoUs} us" : "none")}, CNR {(cnrDb >= 200 ? "noise-free" : cnrDb + " dB")}, pilot {(rx.Stereo.PilotLocked ? "locked" : "NOT locked")}, snr {rx.Stereo.PilotSnrDb:F1} dB, blend {rx.Stereo.Blend:F2}");
        Console.WriteLine(" tone Hz  ch   level dB   leak into other ch dB");
        var resL = (float[])L.Clone(); var resR = (float[])R.Clone();
        double sigE = 0;
        foreach (var tn in tones)
        {
            // expected output amplitude: L/R = tone amplitude without pre-emphasis (0.9 * (L+R)/2 +- ... recovers 0.9x)
            var (aOwn, fitOwn) = Fit(tn.left ? L : R, tn.f, afs, t0);
            var (aOther, _) = Fit(tn.left ? R : L, tn.f, afs, t0);
            Subtract(tn.left ? resL : resR, fitOwn);
            Subtract(tn.left ? resR : resL, Fit(tn.left ? R : L, tn.f, afs, t0).fit);
            double expect = ToneAmp * 0.9;
            sigE += aOwn * aOwn / 2;
            Console.WriteLine($"{tn.f,8} {(tn.left ? "L" : "R"),3} {20 * Math.Log10(aOwn / expect),9:F2} {20 * Math.Log10(aOther / aOwn + 1e-12),14:F1}");
        }
        double resE = 0;
        for (int i = 0; i < len; i++) resE += (resL[i] * resL[i] + resR[i] * resR[i]) / 2;
        resE /= len;
        Console.WriteLine($"residual (noise + distortion) relative to the tones: {10 * Math.Log10(resE / sigE):F1} dB");
        return 0;
    }

    private static (double f, double amp, double ph, bool left) Pre(double f, Random rnd, bool left)
    {
        double w = 2 * Math.PI * f * Tau;
        return (f, ToneAmp * Math.Sqrt(1 + w * w), rnd.NextDouble() * 2 * Math.PI + Math.Atan(w), left);
    }

    /// <summary>Least-squares amplitude of a sinusoid at f; also returns the fitted waveform.</summary>
    private static (double amp, double[] fit) Fit(float[] x, double f, double fs, double t0)
    {
        double sc = 0, ss = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double w = 2 * Math.PI * f * (t0 + i / fs);
            sc += x[i] * Math.Cos(w); ss += x[i] * Math.Sin(w);
        }
        double a = 2 * sc / x.Length, b = 2 * ss / x.Length;
        var fit = new double[x.Length];
        for (int i = 0; i < x.Length; i++) { double w = 2 * Math.PI * f * (t0 + i / fs); fit[i] = a * Math.Cos(w) + b * Math.Sin(w); }
        return (Math.Sqrt(a * a + b * b), fit);
    }

    private static void Subtract(float[] x, double[] fit) { for (int i = 0; i < x.Length; i++) x[i] -= (float)fit[i]; }

    private static double Gauss(Random r)
    {
        double u1 = 1 - r.NextDouble(), u2 = r.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
