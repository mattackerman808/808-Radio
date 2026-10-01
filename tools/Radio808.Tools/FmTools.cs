using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;
using Radio808.Core.Audio;
using Radio808.Core.Dsp;
using Radio808.Core.Radio;

namespace Radio808.Tools;

internal static class FmTools
{
    /// <summary>Demodulates a cu8 recording (1,488,375 S/s) to a 48 kHz stereo WAV and prints receiver stats.</summary>
    public static int Offline(string inPath, string outPath, int outRate)
    {
        var data = File.ReadAllBytes(inPath);
        double chPass = double.Parse(Environment.GetEnvironmentVariable("R808_CH") ?? "100000");
        int taps = int.Parse(Environment.GetEnvironmentVariable("R808_TAPS") ?? "16");
        float mu = float.Parse(Environment.GetEnvironmentVariable("R808_MU") ?? "2e-4");
        var rx = new FmReceiver(75, chPass, taps, mu);
        rx.Equalizer.Enabled = Environment.GetEnvironmentVariable("R808_EQ") != "0";
        Console.WriteLine($"multipath equalizer: {(rx.Equalizer.Enabled ? $"on, {taps} taps, mu {mu}" : "off")}, channel +-{chPass / 1000} kHz");
        double snrSum = 0, rippleSum = 0; int snrN = 0;
        var rs = new StereoResampler(FmReceiver.AudioRate, outRate);
        using var wav = new WaveFileWriter(outPath, WaveFormat.CreateIeeeFloatWaveFormat(outRate, 2));
        var tmp = new float[1 << 16];
        double peak = 0, sumL = 0, sumR = 0, sumLR = 0;
        long audioFrames = 0;
        rx.Audio += a =>
        {
            for (int i = 0; i < a.Length; i += 2)
            {
                float l = a[i], r = a[i + 1];
                peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                sumL += l * l; sumR += r * r; sumLR += l * r;
            }
            audioFrames += a.Length / 2;
            int n = rs.Process(a, tmp);
            wav.WriteSamples(tmp, 0, 2 * n);
        };

        const int block = 65536;   // bytes, like the live device
        var iq = new float[block];
        var sw = Stopwatch.StartNew();
        double nextReport = 1;
        for (int off = 0; off + block <= data.Length; off += block)
        {
            for (int i = 0; i < block; i++) iq[i] = (data[off + i] - 127.4f) / 128f;
            rx.Process(iq);
            double t = (off + block) / 2.0 / FmReceiver.DeviceRate;
            if (t >= nextReport)
            {
                var st = rx.Stereo;
                double ripple = rx.Equalizer.TakeEnvelopeRipple();
                if (t >= 3) { snrSum += st.PilotSnrDb; rippleSum += ripple; snrN++; }
                if (Environment.GetEnvironmentVariable("R808_QUIET") == null)
                    Console.WriteLine($"{t,5:F1}s  ch {rx.ChannelPowerDb,6:F1} dBFS  pilot {(st.PilotLocked ? "LOCK" : "----")} {st.PilotLevel:F3} snr {st.PilotSnrDb,5:F1} dB  blend {st.Blend:F2}  env ripple {ripple:F3}");
                nextReport += 2;
            }
        }
        double secs = data.Length / 2.0 / FmReceiver.DeviceRate;
        Console.WriteLine($"average from 3 s: pilot SNR {snrSum / Math.Max(1, snrN):F1} dB, envelope ripple {rippleSum / Math.Max(1, snrN):F3}");
        var r = rx.Rds;
        Console.WriteLine($"RDS: {(r.Synced ? "sync" : "no sync")}, groups {r.Groups} ({r.Groups / secs:F1}/s), block errors {r.BlockErrorRate:P0}, " +
            $"PI {(r.Pi >= 0 ? r.Pi.ToString("X4") : "-")} {r.CallSign}, PTY {r.PtyName}, TP {r.TrafficProgram}");
        Console.WriteLine($"     PS \"{r.ProgramService}\"  RT \"{r.RadioText}\"");
        double mid = (sumL + sumR + 2 * sumLR) / 4, side = Math.Max(0, (sumL + sumR - 2 * sumLR) / 4);
        Console.WriteLine($"audio {audioFrames / FmReceiver.AudioRate:F2} s of {secs:F2} s, peak {peak:F2}, rms L {Math.Sqrt(sumL / audioFrames):F3} R {Math.Sqrt(sumR / audioFrames):F3}, L/R correlation {sumLR / Math.Sqrt(sumL * sumR):F2}, side/mid {10 * Math.Log10(side / mid + 1e-12):F1} dB");
        Console.WriteLine($"cpu: {sw.Elapsed.TotalSeconds / secs * 100:F1}% of realtime (one core)");
        return 0;
    }

    /// <summary>Measures pilot SNR and ADC clipping at each tuner gain, to find the gain with the best analog quality.</summary>
    public static int GainTest(double mhz)
    {
        using var dev = Program.Open(mhz);
        var rx = new FmReceiver();
        long clipped = 0, samples = 0;
        bool measure = false;
        dev.Samples += iq =>
        {
            rx.Process(iq);
            if (!measure) return;
            for (int i = 0; i < iq.Length; i++) if (Math.Abs(iq[i]) > 0.98f) clipped++;
            samples += iq.Length;
        };
        dev.Start();
        Console.WriteLine(" gain dB   channel dBFS   pilot SNR   clipped");
        foreach (var g in dev.Gains)
        {
            if (g < 8 || g > 45) continue;
            dev.Gain = g;
            measure = false; Thread.Sleep(1500);   // settle; pilot SNR is smoothed over ~0.5 s
            clipped = samples = 0; measure = true;
            Thread.Sleep(3000);
            var st = rx.Stereo;
            Console.WriteLine($"{g,7:F1} {rx.ChannelPowerDb,12:F1} {st.PilotSnrDb,10:F1} dB {100.0 * clipped / Math.Max(1, samples),8:F3}%");
        }
        dev.Stop();
        return 0;
    }

    /// <summary>Plays a station live through the default audio device.</summary>
    public static int Play(double mhz, double gainDb, double seconds)
    {
        using var dev = Program.Open(mhz);
        dev.Gain = gainDb;
        var rx = new FmReceiver();
        using var player = AudioPlayer.CreateAsync(FmReceiver.AudioRate).GetAwaiter().GetResult();
        if (player.DeviceName != "") Console.WriteLine($"audio out: {player.DeviceName}");
        rx.Audio += a => player.Write(a);
        long dspTicks = 0;
        dev.Samples += iq =>
        {
            long t0 = Stopwatch.GetTimestamp();
            rx.Process(iq);
            dspTicks += Stopwatch.GetTimestamp() - t0;
        };
        string? stopped = null;
        dev.Stopped += m => stopped = m;
        dev.Start();
        Console.WriteLine(seconds > 0 ? $"playing {mhz} MHz for {seconds} s"
            : $"playing {mhz} MHz.  Keys: E = multipath equalizer on/off, M = mono/stereo, Q or Enter = quit");
        var sw = Stopwatch.StartNew();
        var quit = new ManualResetEventSlim();
        var wake = new AutoResetEvent(false);
        if (seconds <= 0 && !Console.IsInputRedirected)
            new Thread(() =>
            {
                while (true)
                {
                    var k = Console.ReadKey(true).Key;
                    if (k is ConsoleKey.Q or ConsoleKey.Enter or ConsoleKey.Escape) { quit.Set(); wake.Set(); return; }
                    if (k == ConsoleKey.E) rx.Equalizer.Enabled = !rx.Equalizer.Enabled;
                    if (k == ConsoleKey.M) rx.Stereo.ForceMono = !rx.Stereo.ForceMono;
                    wake.Set();
                }
            }) { IsBackground = true }.Start();
        while (!quit.IsSet && stopped == null && (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds))
        {
            wake.WaitOne(2000);
            if (quit.IsSet) break;
            var st = rx.Stereo;
            double cpu = dspTicks / (double)Stopwatch.Frequency / sw.Elapsed.TotalSeconds * 100;
            Console.WriteLine($"{sw.Elapsed.TotalSeconds,5:F0}s  [{(rx.Equalizer.Enabled ? "EQ on " : "EQ off")}{(st.ForceMono ? " MONO" : "")}]  ch {rx.ChannelPowerDb,6:F1} dBFS  pilot {(st.PilotLocked ? "LOCK" : "----")} snr {st.PilotSnrDb,5:F1}  blend {st.Blend:F2}  buf {player.BufferedMs,4:F0} ms  drift {player.DriftPpm,4:F0} ppm  underruns {player.Underruns}  dsp {cpu:F1}%");
        }
        dev.Stop();
        if (stopped != null) Console.WriteLine(stopped);
        return 0;
    }
}
