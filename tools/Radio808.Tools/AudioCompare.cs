using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace Radio808.Tools;

/// <summary>Compares the tonal balance and stereo width of WAV files (third-octave bands, relative to 1 kHz).</summary>
internal static class AudioCompare
{
    private static readonly double[] Bands = { 31.5, 63, 125, 250, 500, 1000, 2000, 3150, 5000, 8000, 10000, 12500, 14000, 15000, 16000, 18000 };

    public static int Run(string[] files)
    {
        var results = new List<(string name, double[] mid, double[] side, double width)>();
        foreach (var f in files) results.Add(Analyze(f));
        Console.Write("band Hz  ");
        foreach (var r in results) Console.Write($"{System.IO.Path.GetFileNameWithoutExtension(r.name),-22}");
        Console.WriteLine();
        for (int b = 0; b < Bands.Length; b++)
        {
            Console.Write($"{Bands[b],7}  ");
            foreach (var r in results) Console.Write($"mid {r.mid[b],6:F1} side {r.side[b],6:F1}  ");
            Console.WriteLine();
        }
        Console.Write("side/mid ");
        foreach (var r in results) Console.Write($"{r.width,6:F1} dB              ");
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Time-aligns two recordings of the same program (e.g. HD vs analog, same sample rate) and prints the per-band
    /// SNR of the second against the first, from magnitude-squared coherence: SNR = g^2 / (1 - g^2).
    /// </summary>
    public static int Snr(string refPath, string testPath)
    {
        var (a, fsA) = Load(refPath);
        var (b, fsB) = Load(testPath);
        if (fsA != fsB) { Console.WriteLine($"sample rates differ ({fsA} vs {fsB})"); return 1; }
        int fs = fsA;
        // coarse alignment: cross-correlate the mid signals over +-10 s
        int len = Math.Max(a.Length, b.Length) / 2;
        int nfft = 1; while (nfft < 2 * len) nfft <<= 1;
        var ar = new double[nfft]; var ai = new double[nfft]; var br = new double[nfft]; var bi = new double[nfft];
        for (int i = 0; i < a.Length / 2; i++) ar[i] = a[2 * i] + a[2 * i + 1];
        for (int i = 0; i < b.Length / 2; i++) br[i] = b[2 * i] + b[2 * i + 1];
        Fft(ar, ai); Fft(br, bi);
        for (int i = 0; i < nfft; i++)
        {
            double r = ar[i] * br[i] + ai[i] * bi[i], im = ai[i] * br[i] - ar[i] * bi[i];   // A * conj(B)
            ar[i] = r; ai[i] = -im;   // conjugate for inverse via forward FFT
        }
        Fft(ar, ai);
        int best = 0; double bestV = double.MinValue;
        for (int lag = -10 * fs; lag <= 10 * fs; lag++)
        {
            double v = ar[(lag + nfft) % nfft];
            if (v > bestV) { bestV = v; best = lag; }
        }
        // ref[n] ~ test[n - lag]
        Console.WriteLine($"alignment: test is {(best < 0 ? "ahead of" : "behind")} ref by {Math.Abs(best) / (double)fs * 1000:F1} ms");

        // fine alignment: sub-sample lag every 2 s, then a straight-line fit (the two clocks drift by a few ppm)
        var fitT = new List<double>(); var fitL = new List<double>();
        int segLen = 1 << 15;   // ~0.7 s
        for (int c = fs + Math.Max(0, best); c + segLen + fs < a.Length / 2 && c - best + segLen < b.Length / 2; c += 2 * fs)
        {
            double fine = FineLag(a, b, c, c - best, segLen, 64);
            if (!double.IsNaN(fine)) { fitT.Add(c); fitL.Add(best + fine); }
        }
        double slope = 0, icpt = best;
        if (fitT.Count >= 2)
        {
            double mt = 0, ml = 0; for (int i = 0; i < fitT.Count; i++) { mt += fitT[i]; ml += fitL[i]; }
            mt /= fitT.Count; ml /= fitT.Count;
            double num = 0, den = 0; for (int i = 0; i < fitT.Count; i++) { num += (fitT[i] - mt) * (fitL[i] - ml); den += (fitT[i] - mt) * (fitT[i] - mt); }
            slope = den > 0 ? num / den : 0; icpt = ml - slope * mt;
            Console.WriteLine($"fine lag: {icpt / fs * 1000:F3} ms at t=0, drift {slope * 1e6:F1} ppm ({fitT.Count} points)");
        }

        const int N = 4096;
        int frames = Math.Min(a.Length, b.Length) / 2;
        var sxx = new double[2, N / 2]; var syy = new double[2, N / 2]; var sxyr = new double[2, N / 2]; var sxyi = new double[2, N / 2];
        var xr = new double[N]; var xi = new double[N]; var yr = new double[N]; var yi = new double[N];
        for (int start = fs + Math.Max(0, best); start + N + fs < a.Length / 2; start += N / 2)
        {
            double lagHere = icpt + slope * (start + N / 2);
            int il = (int)Math.Floor(lagHere);
            double frac = lagHere - il;   // test must be delayed by a further frac samples
            int s2 = start - il;
            if (s2 < 0 || s2 + N >= b.Length / 2) continue;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < N; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
                    double l1 = a[2 * (start + i)], r1 = a[2 * (start + i) + 1], l2 = b[2 * (s2 + i)], r2 = b[2 * (s2 + i) + 1];
                    xr[i] = (pass == 0 ? l1 + r1 : l1 - r1) * w; xi[i] = 0;
                    yr[i] = (pass == 0 ? l2 + r2 : l2 - r2) * w; yi[i] = 0;
                }
                Fft(xr, xi); Fft(yr, yi);
                for (int k = 0; k < N / 2; k++)
                {
                    // fractional delay: test[n] taken at n - il; ref[n] ~ test[n - lag] = test[(n - il) - frac]
                    double ph = -2 * Math.PI * k * frac / N, c = Math.Cos(ph), sn = Math.Sin(ph);
                    double tr = yr[k] * c - yi[k] * sn; yi[k] = yr[k] * sn + yi[k] * c; yr[k] = tr;
                    sxx[pass, k] += xr[k] * xr[k] + xi[k] * xi[k];
                    syy[pass, k] += yr[k] * yr[k] + yi[k] * yi[k];
                    sxyr[pass, k] += xr[k] * yr[k] + xi[k] * yi[k];
                    sxyi[pass, k] += xi[k] * yr[k] - xr[k] * yi[k];
                }
            }
        }
        double binHz = fs / (double)N;
        Console.WriteLine("band Hz   mid SNR   side SNR   (test vs ref, from coherence)");
        foreach (double fc in Bands)
        {
            double lo = fc / Math.Pow(2, 1 / 6.0), hi = fc * Math.Pow(2, 1 / 6.0);
            Console.Write($"{fc,7}  ");
            for (int pass = 0; pass < 2; pass++)
            {
                // coherence per bin, then averaged: the two chains' phase differs by frequency (separate processors)
                double g2 = 0; int cnt = 0;
                for (int k = 1; k < N / 2; k++)
                {
                    double f = k * binHz;
                    if (f < lo || f >= hi) continue;
                    double cr = sxyr[pass, k], ci = sxyi[pass, k];
                    g2 += (cr * cr + ci * ci) / (sxx[pass, k] * syy[pass, k] + 1e-30);
                    cnt++;
                }
                g2 /= Math.Max(1, cnt);
                Console.Write($"{10 * Math.Log10(g2 / (1 - g2 + 1e-12)),8:F1} dB");
            }
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>Sub-sample lag (ref index minus test index, beyond the given offsets) by cross-correlation of mid signals.</summary>
    private static double FineLag(float[] a, float[] b, int ca, int cb, int len, int maxLag)
    {
        double best = double.MinValue; int bl = 0;
        var v = new double[2 * maxLag + 1];
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double s = 0;
            for (int i = 0; i < len; i += 2)
            {
                int j = cb + i - lag;
                if (j < 0 || j >= b.Length / 2) continue;
                s += (a[2 * (ca + i)] + a[2 * (ca + i) + 1]) * (double)(b[2 * j] + b[2 * j + 1]);
            }
            v[lag + maxLag] = s;
            if (s > best) { best = s; bl = lag; }
        }
        if (bl == -maxLag || bl == maxLag) return double.NaN;
        double y0 = v[bl + maxLag - 1], y1 = v[bl + maxLag], y2 = v[bl + maxLag + 1];
        double den = y0 - 2 * y1 + y2;
        return bl + (den != 0 ? 0.5 * (y0 - y2) / den : 0);
    }

    private static (float[] data, int fs) Load(string path)
    {
        using var file = new WaveFileReader(path);
        var reader = file.ToSampleProvider();
        if (reader.WaveFormat.Channels != 2) throw new InvalidOperationException($"{path}: expected stereo");
        var all = new List<float>();
        var buf = new float[65536];
        int n;
        while ((n = reader.Read(buf.AsSpan())) > 0) for (int i = 0; i < n; i++) all.Add(buf[i]);
        return (all.ToArray(), reader.WaveFormat.SampleRate);
    }

    private static (string, double[], double[], double) Analyze(string path)
    {
        using var file = new WaveFileReader(path);
        var reader = file.ToSampleProvider();
        int fs = reader.WaveFormat.SampleRate, ch = reader.WaveFormat.Channels;
        var all = new List<float>();
        var buf = new float[fs * ch];
        int n;
        while ((n = reader.Read(buf.AsSpan())) > 0) for (int i = 0; i < n; i++) all.Add(buf[i]);
        const int N = 8192;
        var midP = new double[N / 2]; var sideP = new double[N / 2];
        var re = new double[N]; var im = new double[N];
        int frames = all.Count / ch, count = 0;
        double midE = 0, sideE = 0;
        for (int start = fs; start + N <= frames - fs; start += N / 2, count++)   // skip 1 s at each end
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < N; i++)
                {
                    float l = all[(start + i) * ch], r = all[(start + i) * ch + (ch > 1 ? 1 : 0)];
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
                    re[i] = (pass == 0 ? (l + r) / 2 : (l - r) / 2) * w; im[i] = 0;
                }
                Fft(re, im);
                var p = pass == 0 ? midP : sideP;
                for (int i = 0; i < N / 2; i++) p[i] += re[i] * re[i] + im[i] * im[i];
            }
        }
        for (int i = 1; i < N / 2; i++) { midE += midP[i]; sideE += sideP[i]; }
        double binHz = fs / (double)N;
        double Band(double[] p, double fc)
        {
            double lo = fc / Math.Pow(2, 1 / 6.0), hi = fc * Math.Pow(2, 1 / 6.0), s = 0; int c = 0;
            for (int i = 1; i < N / 2; i++) { double f = i * binHz; if (f >= lo && f < hi) { s += p[i]; c++; } }
            return c == 0 ? double.NaN : 10 * Math.Log10(s / c + 1e-30);
        }
        double refDb = Band(midP, 1000);
        var mid = new double[Bands.Length]; var side = new double[Bands.Length];
        for (int b = 0; b < Bands.Length; b++) { mid[b] = Band(midP, Bands[b]) - refDb; side[b] = Band(sideP, Bands[b]) - refDb; }
        return (path, mid, side, 10 * Math.Log10(sideE / midE));
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wlr = Math.Cos(ang), wli = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double wr = 1, wi = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                    double t = wr * wlr - wi * wli; wi = wr * wli + wi * wlr; wr = t;
                }
            }
        }
    }
}
