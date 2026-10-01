using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Radio808.Core.Devices;

namespace Radio808.Tools;

internal static class Program
{
    public const uint SampleRate = 1_488_375;   // 2 x nrsc5's 744187.5 S/s

    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            switch (args.Length > 0 ? args[0] : "")
            {
                case "devices": return Devices();
                case "probe": return Probe(Mhz(args, 1, 97.3), args.Length > 2 ? double.Parse(args[2]) : 3);
                case "capture": return Capture(Mhz(args, 1, 97.3), double.Parse(args[2]), args[3], args.Length > 4 ? double.Parse(args[4]) : null);
                case "spectrum": return Spectrum(args[1]);
                case "scan": return Scan(args.Length > 1 ? double.Parse(args[1]) : double.NaN);
                case "fm": return FmTools.Offline(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 48_000);
                case "snr": return AudioCompare.Snr(args[1], args[2]);
                case "gaintest": return FmTools.GainTest(Mhz(args, 1, 97.3));
                case "selftest": return SelfTest.Run(args.Length > 1 ? double.Parse(args[1]) : 999,
                    args.Length > 2 ? double.Parse(args[2]) : 0, args.Length > 3 ? double.Parse(args[3]) : 0);
                case "compare": return AudioCompare.Run(args[1..]);
                case "fmplay": return FmTools.Play(Mhz(args, 1, 97.3), args.Length > 2 ? double.Parse(args[2]) : 16.6, args.Length > 3 ? double.Parse(args[3]) : 0);
                case "play": return HdTools.Play(Mhz(args, 1, 97.3), args.Length > 2 ? double.Parse(args[2]) : 16.6, args.Length > 3 ? double.Parse(args[3]) : 0);
                case "hd": return HdTools.Offline(args[1], args[2]);
                default:
                    Console.WriteLine("usage: tools devices | probe <MHz> [s] | capture <MHz> <s> <out.cu8> [gain] | spectrum <in.cu8> | scan [gain]");
                    Console.WriteLine("             fm <in.cu8> <out.wav> | play <MHz> [gain] [seconds, 0 = until Enter]");
                    return 2;
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    private static double Mhz(string[] a, int i, double def) => a.Length > i ? double.Parse(a[i]) : def;

    private static int Devices()
    {
        var list = RtlSdrDevice.Enumerate();
        if (list.Count == 0) { Console.WriteLine("No RTL-SDR found."); return 1; }
        foreach (var d in list) Console.WriteLine($"#{d.Index}: {d.Name} | {d.Manufacturer} | {d.Product} | SN {d.Serial}");
        return 0;
    }

    internal static RtlSdrDevice Open(double mhz)
    {
        var list = RtlSdrDevice.Enumerate();
        if (list.Count == 0) throw new InvalidOperationException("No RTL-SDR found.");
        var dev = new RtlSdrDevice(list[0]);
        dev.SampleRate = SampleRate;
        dev.Frequency = (long)Math.Round(mhz * 1e6);
        dev.Gain = null;
        Console.WriteLine($"{list[0]}  tuner {dev.TunerType}  rate {dev.SampleRate}  freq {dev.Frequency}  gains {dev.Gains.Count} steps (max {(dev.Gains.Count > 0 ? dev.Gains[^1] : 0)} dB)");
        return dev;
    }

    private static int Probe(double mhz, double seconds)
    {
        using var dev = Open(mhz);
        double sumSq = 0; long n = 0; int clipped = 0;
        dev.Samples += iq =>
        {
            for (int i = 0; i < iq.Length; i++)
            {
                float v = iq[i];
                sumSq += v * v;
                if (Math.Abs(v) > 0.99f) clipped++;
            }
            n += iq.Length / 2;
        };
        string? stopped = null;
        dev.Stopped += m => stopped = m;
        var sw = Stopwatch.StartNew();
        dev.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        dev.Stop();
        double rate = n / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"received {n} samples in {sw.Elapsed.TotalSeconds:F2} s = {rate / 1e6:F4} MS/s (expected {SampleRate / 1e6:F4})");
        Console.WriteLine($"RMS {Math.Sqrt(sumSq / Math.Max(1, 2 * n)):F3}  clipped {100.0 * clipped / Math.Max(1, 2 * n):F3}%");
        if (stopped != null) Console.WriteLine(stopped);
        return 0;
    }

    /// <summary>Averaged power spectrum of a cu8 file, printed as dB per 25 kHz bin across +-375 kHz.</summary>
    private static int Spectrum(string path)
    {
        const int N = 4096;
        var data = File.ReadAllBytes(path);
        var acc = new double[N];
        var re = new double[N]; var im = new double[N];
        int frames = 0;
        for (long off = 0; off + 2 * N <= data.Length && frames < 2000; off += 2 * N * 3, frames++)
        {
            for (int i = 0; i < N; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
                re[i] = (data[off + 2 * i] - 127.4) * w; im[i] = (data[off + 2 * i + 1] - 127.4) * w;
            }
            Fft(re, im);
            for (int i = 0; i < N; i++) acc[i] += re[i] * re[i] + im[i] * im[i];
        }
        double binHz = SampleRate / (double)N;
        double floor = double.MaxValue;
        var rows = new System.Collections.Generic.List<(int khz, double db)>();
        for (int khz = -375; khz < 375; khz += 25)
        {
            double sum = 0; int cnt = 0;
            for (int i = 0; i < N; i++)
            {
                double f = (i < N / 2 ? i : i - N) * binHz / 1000;
                if (f >= khz && f < khz + 25) { sum += acc[i]; cnt++; }
            }
            double db = 10 * Math.Log10(sum / cnt / frames + 1e-12);
            rows.Add((khz, db));
            floor = Math.Min(floor, db);
        }
        foreach (var (khz, db) in rows)
            Console.WriteLine($"{khz,5}..{khz + 25,-5} kHz {db - floor,6:F1} dB  {new string('#', (int)Math.Max(0, (db - floor) * 1.5))}");
        return 0;
    }

    /// <summary>Sweeps 88-108 MHz and lists the strongest 200 kHz FM channels (dB above the band's noise floor).</summary>
    private static int Scan(double gainDb)
    {
        const int N = 4096;
        using var dev = Open(88.6);
        if (!double.IsNaN(gainDb)) dev.Gain = gainDb;
        Console.WriteLine($"gain: {(double.IsNaN(gainDb) ? "auto" : gainDb + " dB")}");
        var power = new System.Collections.Generic.Dictionary<int, double>();   // channel (kHz) -> power
        double binHz = SampleRate / (double)N;
        var acc = new double[N];
        var re = new double[N]; var im = new double[N];
        int frames = 0, skip = 0;
        var done = new ManualResetEventSlim(true);
        // Stream once and retune in place; buffers in flight during a retune are discarded.
        dev.Samples += iq =>
        {
            if (done.IsSet) return;
            if (skip > 0) { skip--; return; }
            for (int off = 0; off + 2 * N <= iq.Length; off += 2 * N)
            {
                for (int i = 0; i < N; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
                    re[i] = iq[off + 2 * i] * w; im[i] = iq[off + 2 * i + 1] * w;
                }
                Fft(re, im);
                for (int i = 0; i < N; i++) acc[i] += re[i] * re[i] + im[i] * im[i];
                if (++frames >= 60) { done.Set(); return; }
            }
        };
        dev.Start();
        for (int centerKhz = 88600; centerKhz <= 107600; centerKhz += 1000)
        {
            dev.Frequency = centerKhz * 1000L;
            Array.Clear(acc);
            frames = 0;
            skip = 20;   // ~0.4 s of buffers queued before the retune
            done.Reset();
            if (!done.Wait(5000)) Console.WriteLine($"  {centerKhz / 1000.0:F1} MHz: no samples");
            // channels within +-500 kHz of center, skipping the DC-affected center channel
            for (int ch = centerKhz - 500; ch < centerKhz + 500; ch += 100)
            {
                if (ch % 200 == 0 || ch == centerKhz) continue;   // US FM channels are odd tenths
                double sum = 0; int cnt = 0;
                for (int i = 0; i < N; i++)
                {
                    double f = (i < N / 2 ? i : i - N) * binHz / 1000 + centerKhz;
                    if (Math.Abs(f - ch) < 90) { sum += acc[i]; cnt++; }
                }
                if (cnt > 0 && frames > 0) power[ch] = 10 * Math.Log10(sum / cnt / frames + 1e-15);
            }
        }
        var sorted = new System.Collections.Generic.List<double>(power.Values);
        sorted.Sort();
        double floor = sorted[sorted.Count / 4];
        var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, double>>(power);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        foreach (var kv in list.GetRange(0, Math.Min(20, list.Count)))
            Console.WriteLine($"{kv.Key / 1000.0,6:F1} MHz  {kv.Value - floor,5:F1} dB  {new string('#', (int)Math.Max(0, kv.Value - floor))}");
        return 0;
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
            for (int i = 0; i < n; i += len)
                for (int k = 0; k < len / 2; k++)
                {
                    double wr = Math.Cos(ang * k), wi = Math.Sin(ang * k);
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                }
        }
    }

    private static int Capture(double mhz, double seconds, string path, double? gainDb)
    {
        using var dev = Open(mhz);
        dev.Gain = gainDb;
        Console.WriteLine($"gain: {(gainDb is null ? "auto" : gainDb + " dB")} -> tuner reports {dev.Gain} dB");
        long want = (long)(seconds * SampleRate) * 2;
        long have = 0;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        var done = new ManualResetEventSlim();
        var buf = new byte[256 * 1024];
        dev.Samples += iq =>
        {
            int n = (int)Math.Min(iq.Length, want - have);
            if (n <= 0) { done.Set(); return; }
            if (buf.Length < n) buf = new byte[n];
            for (int i = 0; i < n; i++) buf[i] = (byte)Math.Clamp((int)MathF.Round(iq[i] * 128f + 127.4f), 0, 255);
            fs.Write(buf, 0, n);
            have += n;
            if (have >= want) done.Set();
        };
        dev.Stopped += _ => done.Set();
        dev.Start();
        done.Wait(TimeSpan.FromSeconds(seconds + 10));
        dev.Stop();
        Console.WriteLine($"wrote {have / 2} samples ({have / 2.0 / SampleRate:F1} s) to {path}");
        return 0;
    }
}
