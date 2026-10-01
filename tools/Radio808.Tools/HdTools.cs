using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;
using Radio808.Core.Dsp;
using Radio808.Core.Hd;
using Radio808.Core.Radio;

namespace Radio808.Tools;

internal static class HdTools
{
    /// <summary>
    /// Runs a cu8 recording through receiver + HD decoder + blender, pacing the decoder as if live. Prints the blend
    /// state, and checks L/R orientation of the analog stereo decoder against HD (true L/R) once HD is playing.
    /// </summary>
    public static int Offline(string inPath, string outPath)
    {
        Console.WriteLine($"libnrsc5 {HdDecoder.LibraryVersion}");
        var data = File.ReadAllBytes(inPath);
        var rx = new FmReceiver();
        var blender = new HdBlender(FmReceiver.AudioRate);
        using var hd = new HdDecoder(blender);
        rx.Baseband += (i, q) => hd.Enqueue(i, q);

        var rs = new StereoResampler(FmReceiver.AudioRate, 48_000);
        using var wav = new WaveFileWriter(outPath, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));
        var tmp = new float[1 << 16];
        var analog = new float[0];
        // L/R orientation: envelope correlations (5 ms buckets) between analog in and blended out, while fully HD
        var env = new double[4]; int envN = 0; double accAL = 0, accAR = 0, accHL = 0, accHR = 0; int bucket = 0;
        var sums = new double[4, 3];   // pairs (aL,hL) (aL,hR) (aR,hR) (aR,hL): sum xy, xx, yy (mean-removed later)
        var sx = new double[4]; var sy = new double[4]; int pairs = 0;
        int bucketLen = (int)(FmReceiver.AudioRate / 200);
        rx.Audio += a =>
        {
            if (analog.Length < a.Length) analog = new float[a.Length];
            a.CopyTo(analog);
            blender.Process(a);
            bool fullHd = blender.PlayingHd;
            for (int i = 0; i < a.Length; i += 2)
            {
                accAL += Math.Abs(analog[i]); accAR += Math.Abs(analog[i + 1]);
                accHL += Math.Abs(a[i]); accHR += Math.Abs(a[i + 1]);
                if (++bucket < bucketLen) continue;
                if (fullHd)
                {
                    double[] x = { accAL, accAL, accAR, accAR }, y = { accHL, accHR, accHR, accHL };
                    for (int p = 0; p < 4; p++)
                    {
                        sums[p, 0] += x[p] * y[p]; sums[p, 1] += x[p] * x[p]; sums[p, 2] += y[p] * y[p];
                        sx[p] += x[p]; sy[p] += y[p];
                    }
                    pairs++;
                }
                accAL = accAR = accHL = accHR = 0; bucket = 0;
            }
            int n = rs.Process(a, tmp);
            wav.WriteSamples(tmp, 0, 2 * n);
        };

        hd.Start();
        const int block = 65536;
        var iq = new float[block];
        var sw = Stopwatch.StartNew();
        double nextReport = 1;
        bool conj = Environment.GetEnvironmentVariable("R808_CONJ") == "1";   // mirror the spectrum (negate Q)
        for (int off = 0; off + block <= data.Length; off += block)
        {
            for (int i = 0; i < block; i++) iq[i] = (data[off + i] - 127.4f) / 128f;
            if (conj) for (int i = 1; i < block; i += 2) iq[i] = -iq[i];
            rx.Process(iq);
            hd.WaitIdle();   // as if live: HD is decoded as soon as its samples arrive
            double t = (off + block) / 2.0 / FmReceiver.DeviceRate;
            if (t >= nextReport)
            {
                var s = hd.Status;
                Console.WriteLine($"{t,5:F1}s  {(s.Synced ? "SYNC" : "----")} MER {s.MerLower,4:F1}/{s.MerUpper,4:F1}  " +
                    $"{(blender.PlayingHd ? "HD    " : "analog")}  lead {blender.HdLeadSeconds,6:F3} s  score {blender.AlignScore:F2}  gain {blender.HdGain:F2}  {s.StationName} {s.Title} - {s.Artist}");
                nextReport += 1;
            }
        }
        double secs = data.Length / 2.0 / FmReceiver.DeviceRate;
        Console.WriteLine($"cpu: {sw.Elapsed.TotalSeconds / secs * 100:F1}% of realtime");
        if (pairs > 50)
        {
            string[] names = { "analog L ~ HD L", "analog L ~ HD R", "analog R ~ HD R", "analog R ~ HD L" };
            var c = new double[4];
            for (int p = 0; p < 4; p++)
            {
                double cov = sums[p, 0] - sx[p] * sy[p] / pairs, vx = sums[p, 1] - sx[p] * sx[p] / pairs, vy = sums[p, 2] - sy[p] * sy[p] / pairs;
                c[p] = cov / Math.Sqrt(vx * vy);
                Console.WriteLine($"  {names[p]}: {c[p]:F3}");
            }
            Console.WriteLine(c[0] + c[2] > c[1] + c[3] ? "L/R orientation: correct" : "L/R orientation: SWAPPED");
        }
        else Console.WriteLine("HD never played long enough to check L/R orientation");
        return 0;
    }

    /// <summary>
    /// For each tuner gain: pilot SNR, envelope ripple, ADC clipping, and HD MER (full chain, muted), to see which fast
    /// metric peaks where HD quality does.
    /// </summary>
    public static int GainSweep(double mhz, double settle, double measure)
    {
        using var radio = RadioEngine.StartAsync((long)Math.Round(mhz * 1e6)).GetAwaiter().GetResult();
        radio.Muted = true;
        long clipped = 0, samples = 0;
        bool count = false;
        radio.Device.Samples += iq =>
        {
            if (!count) return;
            for (int i = 0; i < iq.Length; i++) if (Math.Abs(iq[i]) > 0.98f) clipped++;
            samples += iq.Length;
        };
        Console.WriteLine($"{mhz:F1} MHz: waiting for HD...");
        for (int i = 0; i < 30 && !radio.Hd.Synced; i++) Thread.Sleep(500);
        var gains = ((Radio808.Core.Devices.RtlSdrDevice)radio.Device).Gains;
        Console.WriteLine("  gain  pilotSNR  ripple   clip%    MER   (HD sync)");
        foreach (var g in gains)
        {
            if (g < 5 || g > 45) continue;
            radio.Gain = g;
            Thread.Sleep(TimeSpan.FromSeconds(settle));
            clipped = samples = 0; count = true;
            double snr = 0, rip = 0, mer = 0; int n = 0, merN = 0, syncN = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < measure)
            {
                Thread.Sleep(250);
                var st = radio.Receiver.Stereo; var hd = radio.Hd;
                snr += st.PilotLocked ? st.PilotSnrDb : 0; rip += radio.Receiver.Equalizer.Ripple; n++;
                if (hd.Synced) { syncN++; if (hd.MerLower > 0) { mer += (hd.MerLower + hd.MerUpper) / 2; merN++; } }
            }
            count = false;
            Console.WriteLine($"{g,6:F1} {snr / n,9:F1} {rip / n,7:F3} {100.0 * clipped / Math.Max(1, samples),7:F3} {(merN > 0 ? (mer / merN).ToString("F1") : "  -"),6}   {syncN}/{n}");
        }
        return 0;
    }

    /// <summary>Hammers gain changes and retunes from two threads at once; counts failed control calls.</summary>
    public static int ControlStress(double seconds)
    {
        using var radio = RadioEngine.StartAsync(98_500_000, null, 16.6).GetAwaiter().GetResult();
        radio.Muted = true;
        var dev = radio.Device;
        int gainOps = 0, tuneOps = 0, gainFail = 0, tuneFail = 0;
        var stop = DateTime.UtcNow.AddSeconds(seconds);
        var rnd = new Random(1);
        var gains = dev.Gains;
        var t = new Thread(() =>
        {
            while (DateTime.UtcNow < stop)
            {
                try { dev.Gain = gains[rnd.Next(4, gains.Count - 8)]; gainOps++; } catch { gainFail++; }
                Thread.Sleep(5);
            }
        });
        t.Start();
        long[] freqs = { 98_500_000, 97_300_000, 92_300_000, 105_700_000 };
        int i = 0;
        while (DateTime.UtcNow < stop)
        {
            try { radio.Frequency = freqs[i++ % freqs.Length]; tuneOps++; } catch (Exception e) { tuneFail++; Console.WriteLine("  tune failed: " + e.Message); }
            Thread.Sleep(50);
        }
        t.Join();
        radio.Gain = 16.6;
        radio.Frequency = 98_500_000;
        Thread.Sleep(4000);
        Console.WriteLine($"{gainOps} gain changes ({gainFail} failed), {tuneOps} retunes ({tuneFail} failed)");
        Console.WriteLine($"afterwards on 98.5: pilot {(radio.Receiver.Stereo.PilotLocked ? "locked" : "NOT locked")}, RDS {radio.Receiver.Rds.CallSign}, HD {(radio.Hd.Synced ? "synced" : "not synced")}");
        return tuneFail + gainFail == 0 ? 0 : 1;
    }

    /// <summary>Live radio through the default audio device, with keyboard controls.</summary>
    public static int Play(double mhz, double? gainDb, double seconds = 0)
    {
        using var radio = RadioEngine.StartAsync((long)Math.Round(mhz * 1e6), null, gainDb).GetAwaiter().GetResult();
        string? stopped = null;
        radio.DeviceStopped += m => stopped = m;
        Console.WriteLine($"playing {mhz:F1} MHz.  Keys: Left/Right tune 0.2 MHz, 1-8 HD program, A analog only, E equalizer, M mono, Q quit");
        var quit = new ManualResetEventSlim();
        var wake = new AutoResetEvent(false);
        if (!Console.IsInputRedirected)
            new Thread(() =>
            {
                while (true)
                {
                    var k = Console.ReadKey(true);
                    switch (k.Key)
                    {
                        case ConsoleKey.Q or ConsoleKey.Escape or ConsoleKey.Enter: quit.Set(); wake.Set(); return;
                        case ConsoleKey.RightArrow: radio.Frequency += 200_000; break;
                        case ConsoleKey.LeftArrow: radio.Frequency -= 200_000; break;
                        case ConsoleKey.A: radio.ForceAnalog = !radio.ForceAnalog; break;
                        case ConsoleKey.E: radio.Equalizer = !radio.Equalizer; break;
                        case ConsoleKey.M: radio.ForceMono = !radio.ForceMono; break;
                        default:
                            if (k.KeyChar is >= '1' and <= '8') radio.Program = (uint)(k.KeyChar - '1');
                            break;
                    }
                    wake.Set();
                }
            }) { IsBackground = true }.Start();
        var sw = Stopwatch.StartNew();
        // R808_TOUR="98.5,92.3": unattended test, retunes through the list every 10 s
        var tour = Environment.GetEnvironmentVariable("R808_TOUR")?.Split(',');
        int tourIdx = 0;
        while (!quit.IsSet && stopped == null && (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds))
        {
            if (tour != null && tourIdx < tour.Length && sw.Elapsed.TotalSeconds >= 10 * (tourIdx + 1))
                radio.Frequency = (long)Math.Round(double.Parse(tour[tourIdx++]) * 1e6);
            wake.WaitOne(2000);
            if (quit.IsSet) break;
            var s = radio.Hd; var b = radio.Blender; var st = radio.Receiver.Stereo;
            string programs = string.Join(",", System.Linq.Enumerable.Select(s.Programs.Keys, p => p == radio.Program ? $"[HD{p + 1}]" : $"HD{p + 1}"));
            var opt = radio.GainOptimizer;
            Console.Write($"[gain {radio.CurrentGainDb,4:F1}{(radio.AutoGain ? $" {opt.State,-9} {opt.Metric,-9} {opt.LastScore,5:F1}" : " fixed")} clip {opt.Clipping * 100,5:F2}%] ");
            Console.WriteLine($"{sw.Elapsed.TotalSeconds,4:F0}s {radio.Frequency / 1e6,5:F1} " +
                $"{(b.PlayingHd ? "HD    " : radio.ForceAnalog ? "ANALOG" : "analog")} " +
                $"{(s.Synced ? $"MER {(s.MerLower + s.MerUpper) / 2,4:F1}" : "no HD   ")} {programs,-24} " +
                $"lead {b.HdLeadSeconds,5:F2} " + (b.RetryIn > 0 ? $"retry {b.RetryIn:F0}s " : "") +
                $"| pilot {st.PilotSnrDb,4:F1} blend {st.Blend:F2}{(radio.Equalizer ? "" : " EQoff")}{(radio.ForceMono ? " MONO" : "")} " +
                (s.StationName != null ? $"| {s.StationName} {s.Title}{(s.Artist != null ? " - " + s.Artist : "")}"
                    : $"| RDS {radio.Receiver.Rds.CallSign} [{radio.Receiver.Rds.ProgramService}] {radio.Receiver.Rds.RadioText}") +
                (radio.HdDecoder.DroppedBlocks > 0 ? $" | dropped {radio.HdDecoder.DroppedBlocks}" : ""));
        }
        if (stopped != null) Console.WriteLine(stopped);
        return 0;
    }
}
