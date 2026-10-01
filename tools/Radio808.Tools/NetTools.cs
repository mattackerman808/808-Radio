using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radio808.Core.Devices;

namespace Radio808.Tools;

internal static class NetTools
{
    /// <summary>
    /// Measures an rtl_tcp link: throughput, drops, and the control latency (time from a command to the first samples
    /// that show it), by toggling the gain between its lowest and a high step and watching the signal power jump.
    /// </summary>
    public static int Latency(string address, double mhz, int rounds = 12)
    {
        using var src = new RtlTcpSource(address);
        Console.WriteLine($"connected: {src.Name}, {src.Gains.Count} gain steps");
        src.SampleRate = 1_488_375;
        src.Frequency = (long)Math.Round(mhz * 1e6);
        double lo = src.Gains[0], hi = src.Gains[Math.Min(src.Gains.Count - 1, 18)];
        src.Gain = lo;

        var events = new List<(long t, double p)>();
        long chunks = 0;
        src.Samples += iq =>
        {
            double sum = 0;
            for (int i = 0; i < iq.Length; i++) sum += iq[i] * iq[i];
            lock (events) events.Add((Stopwatch.GetTimestamp(), 10 * Math.Log10(sum / iq.Length + 1e-12)));
            Interlocked.Increment(ref chunks);
        };
        src.Start();
        Thread.Sleep(1500);

        var lat = new List<double>();
        bool high = false;
        for (int r = 0; r < rounds; r++)
        {
            double before;
            lock (events) { before = events.TakeLast(5).Average(e => e.p); events.Clear(); }
            high = !high;
            long t0 = Stopwatch.GetTimestamp();
            src.Gain = high ? hi : lo;
            Thread.Sleep(900);
            List<(long t, double p)> ev;
            lock (events) ev = events.ToList();
            double after = ev.TakeLast(5).Average(e => e.p);
            double mid = (before + after) / 2;
            var hit = ev.FirstOrDefault(e => high ? e.p > mid : e.p < mid);
            double ms = hit.t == 0 ? double.NaN : (hit.t - t0) * 1000.0 / Stopwatch.Frequency;
            lat.Add(ms);
            Console.WriteLine($"gain {(high ? hi : lo),4:0.0} dB: power {before,6:0.0} -> {after,6:0.0} dBFS, seen after {ms,5:0} ms");
        }
        Thread.Sleep(2000);
        Console.WriteLine($"link: {src.LinkStatus}");
        var ok = lat.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
        if (ok.Count > 0) Console.WriteLine($"control latency: median {ok[ok.Count / 2]:0} ms, max {ok[^1]:0} ms (source assumes {src.ControlLatency.TotalMilliseconds:0} ms)");
        return 0;
    }
}
