using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Radio808.Core.Devices;
using Radio808.Core.Radio;

namespace Radio808.Tools;

/// <summary>
/// <c>overload [gain dB]</c>: tunes every US FM channel at a fixed tuner gain (default: the lowest the tuner has) and
/// measures the ADC level and clipping on each, to say how much attenuation the antenna chain needs before the
/// automatic gain has room to work on every station. Uses the network dongle when R808_RTLTCP is set.
/// </summary>
internal static class OverloadScan
{
    public static int Run(double? gainDb)
    {
        var net = Environment.GetEnvironmentVariable("R808_RTLTCP");
        using IIqSource dev = net is { Length: > 0 } ? new RtlTcpSource(net) : Program.Open(98.5);
        dev.SampleRate = Program.SampleRate;
        double gain = gainDb ?? (dev.Gains.Count > 0 ? dev.Gains[0] : 0);
        dev.Gain = gain;
        Console.WriteLine($"{dev.Name}: tuner gain {gain:0.0} dB (lowest {(dev.Gains.Count > 0 ? dev.Gains[0] : 0):0.0}), measuring 0.3 s per channel after settling");
        Console.WriteLine();
        Console.WriteLine("  MHz    RMS dBFS   clipped   over by   note");

        // per channel: sum of squares, clipped count, sample count, collected after the retune has settled
        double sumSq = 0; long clipped = 0, count = 0;
        int skipSamples = 0;
        bool collecting = false;
        var done = new ManualResetEventSlim(false);
        const long Want = 450_000;   // ~0.3 s of complex samples
        dev.Samples += iq =>
        {
            if (!collecting) return;
            int start = 0;
            if (skipSamples > 0) { int s = Math.Min(skipSamples, iq.Length); skipSamples -= s; start = s; }
            for (int i = start; i < iq.Length; i++)
            {
                float v = iq[i];
                sumSq += v * v;
                if (v > 0.98f || v < -0.98f) clipped++;
            }
            count += (iq.Length - start) / 2;
            if (count >= Want) { collecting = false; done.Set(); }
        };
        string? stopped = null;
        long callbacks = 0;
        dev.Stopped += m => { stopped = m; done.Set(); };
        dev.Samples += _ => Interlocked.Increment(ref callbacks);
        dev.Frequency = 98_500_000;
        dev.Start();
        // wait for the stream to flow before the first measurement (rtl_tcp needs ~20 s after a client left it)
        collecting = true; skipSamples = 0;
        if (!done.Wait(30000) || stopped != null)
        {
            Console.WriteLine(stopped ?? $"no samples from the dongle in 30 s ({Interlocked.Read(ref callbacks)} blocks arrived; {dev.LinkStatus})");
            return 1;
        }

        var results = new List<(double mhz, double rmsDb, double clipFrac, double overDb)>();
        for (long hz = RadioEngine.FirstChannel; hz <= RadioEngine.LastChannel; hz += RadioEngine.ChannelStep)
        {
            dev.Frequency = hz;
            // samples still on their way from the old frequency: the retune skip plus the link's latency
            skipSamples = 2 * (int)((0.176 + dev.ControlLatency.TotalSeconds + 0.1) * Program.SampleRate);
            sumSq = 0; clipped = 0; count = 0;
            done.Reset();
            collecting = true;
            if (!done.Wait(6000) || stopped != null) { Console.WriteLine(stopped ?? $"  {hz / 1e6,5:0.0}  no samples"); if (stopped != null) return 1; continue; }
            double rms = Math.Sqrt(sumSq / Math.Max(1, 2 * count));   // per component
            double clipFrac = clipped / (double)Math.Max(1, 2 * count);
            double over = OverBy(rms, clipFrac);
            results.Add((hz / 1e6, 20 * Math.Log10(Math.Max(rms, 1e-6)), clipFrac, over));
            string note = clipFrac > 0.05 ? "OVERLOAD" : clipFrac > 0.0002 ? "clipping" : over > 0 ? "near the edge" : "";
            Console.WriteLine($"  {hz / 1e6,5:0.0}   {20 * Math.Log10(Math.Max(rms, 1e-6)),7:0.0}   {clipFrac * 100,6:0.000}%   {(over > 0 ? over.ToString("0.0") + " dB" : "   -  "),7}   {note}");
        }
        dev.Stop();

        var bad = results.Where(r => r.clipFrac > 0.0002).ToList();
        double worst = results.Count > 0 ? results.Max(r => r.overDb) : 0;
        Console.WriteLine();
        Console.WriteLine($"{bad.Count} of {results.Count} channels clip at {gain:0.0} dB tuner gain" +
            (bad.Count > 0 ? $": {string.Join(" ", bad.OrderByDescending(r => r.overDb).Take(12).Select(r => r.mhz.ToString("0.0")))}" : "."));
        if (worst > 0)
        {
            Console.WriteLine($"Worst is about {worst:0} dB over the level where clipping stops (a Gaussian estimate from the clip rate: rough above ~10%).");
            Console.WriteLine($"Minimum extra attenuation so nothing clips at the floor: {Math.Ceiling(worst):0} dB.");
            Console.WriteLine($"Recommended, with a couple of gain steps of headroom for the optimizer: {Math.Ceiling(worst) + 6:0} dB.");
        }
        else Console.WriteLine("Nothing clips at this gain: the chain has enough attenuation already.");
        return 0;
    }

    /// <summary>
    /// How many dB the signal would have to drop for clipping to fall below 0.02%: from the clip fraction when it clips
    /// (a Gaussian component clips at 0.98 with probability 2Q(0.98/sigma)), else from the RMS directly. The target is
    /// sigma = 0.25 (-12 dBFS), where that model gives ~0.01%.
    /// </summary>
    private static double OverBy(double rms, double clipFrac)
    {
        const double target = 0.25;
        double sigma = clipFrac > 1e-5 ? 0.98 / QInv(clipFrac / 2) : rms;
        return Math.Max(0, 20 * Math.Log10(sigma / target));
    }

    /// <summary>Inverse of the Gaussian tail Q(x) = P(X > x), for p in (0, 0.5).</summary>
    private static double QInv(double p)
    {
        p = Math.Clamp(p, 1e-12, 0.4999);
        // Acklam's rational approximation of the normal quantile, for the upper tail
        double q = 1 - p;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        if (q > 0.97575)
        {
            double r = Math.Sqrt(-2 * Math.Log(1 - q));
            return -(((((c[0] * r + c[1]) * r + c[2]) * r + c[3]) * r + c[4]) * r + c[5]) / ((((d[0] * r + d[1]) * r + d[2]) * r + d[3]) * r + 1);
        }
        double u = q - 0.5, t = u * u;
        return (((((a[0] * t + a[1]) * t + a[2]) * t + a[3]) * t + a[4]) * t + a[5]) * u / (((((b[0] * t + b[1]) * t + b[2]) * t + b[3]) * t + b[4]) * t + 1);
    }
}
