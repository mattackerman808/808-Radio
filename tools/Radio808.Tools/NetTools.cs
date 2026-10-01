using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using Radio808.Core.Devices;

namespace Radio808.Tools;

internal static class NetTools
{
    /// <summary>Band-level statistics of the faceplate's audio analyzer over a WAV file (to set its display range).</summary>
    public static int AnalyzerStats(string wav)
    {
        using var file = new NAudio.Wave.WaveFileReader(wav);
        var reader = file.ToSampleProvider();
        int ch = reader.WaveFormat.Channels;
        var an = new Radio808.Core.Dsp.AudioAnalyzer(16, reader.WaveFormat.SampleRate);
        var samples = new List<float>();
        var buf = new float[65536];
        int got;
        while ((got = reader.Read(buf.AsSpan())) > 0) for (int i = 0; i < got; i++) samples.Add(buf[i]);
        var all = samples.ToArray();
        int n = all.Length;
        var mono = new float[n / ch];
        for (int i = 0; i < mono.Length; i++) { float s = 0; for (int c = 0; c < ch; c++) s += all[i * ch + c]; mono[i] = s / ch; }
        var per = Enumerable.Range(0, 16).Select(_ => new List<float>()).ToArray();
        var db = new float[16];
        for (int pos = 0; pos + Radio808.Core.Dsp.AudioAnalyzer.BlockSize <= mono.Length; pos += 1024)
        {
            an.Analyze(mono.AsSpan(pos, Radio808.Core.Dsp.AudioAnalyzer.BlockSize), db);
            for (int b = 0; b < 16; b++) per[b].Add(db[b]);
        }
        Console.WriteLine($"{wav}: {reader.WaveFormat.SampleRate} Hz, {per[0].Count} blocks");
        Console.WriteLine(" band        Hz     p10    p50    p90    max");
        for (int b = 0; b < 16; b++)
        {
            var s = per[b].OrderBy(x => x).ToList();
            Console.WriteLine($"  {b,2}  {an.Edges[b],6:0}-{an.Edges[b + 1],-6:0} {s[s.Count / 10],6:0.0} {s[s.Count / 2],6:0.0} {s[s.Count * 9 / 10],6:0.0} {s[^1],6:0.0}");
        }
        return 0;
    }

    /// <summary>Lists rtl_tcp servers advertised on the local network (mDNS / DNS-SD).</summary>
    public static int Discover(double seconds)
    {
        var sw = Stopwatch.StartNew();
        var found = RtlTcpDiscovery.BrowseAsync(TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
        Console.WriteLine($"{found.Count} rtl_tcp server(s) in {sw.Elapsed.TotalSeconds:0.0} s");
        foreach (var s in found) Console.WriteLine($"  {s.Name,-28} {s.Host}:{s.Port}  {s.Address}");
        return found.Count > 0 ? 0 : 1;
    }

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
