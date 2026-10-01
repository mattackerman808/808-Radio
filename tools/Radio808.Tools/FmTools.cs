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
    public static int Offline(string inPath, string outPath)
    {
        var data = File.ReadAllBytes(inPath);
        var rx = new FmReceiver();
        var rs = new StereoResampler(FmReceiver.AudioRate, 48_000);
        using var wav = new WaveFileWriter(outPath, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));
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
                Console.WriteLine($"{t,5:F1}s  ch {rx.ChannelPowerDb,6:F1} dBFS  pilot {(st.PilotLocked ? "LOCK" : "----")} {st.PilotLevel:F3} snr {st.PilotSnrDb,5:F1} dB  blend {st.Blend:F2}");
                nextReport += 2;
            }
        }
        double secs = data.Length / 2.0 / FmReceiver.DeviceRate;
        double mid = (sumL + sumR + 2 * sumLR) / 4, side = Math.Max(0, (sumL + sumR - 2 * sumLR) / 4);
        Console.WriteLine($"audio {audioFrames / FmReceiver.AudioRate:F2} s of {secs:F2} s, peak {peak:F2}, rms L {Math.Sqrt(sumL / audioFrames):F3} R {Math.Sqrt(sumR / audioFrames):F3}, L/R correlation {sumLR / Math.Sqrt(sumL * sumR):F2}, side/mid {10 * Math.Log10(side / mid + 1e-12):F1} dB");
        Console.WriteLine($"cpu: {sw.Elapsed.TotalSeconds / secs * 100:F1}% of realtime (one core)");
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
        Console.WriteLine(seconds > 0 ? $"playing {mhz} MHz for {seconds} s" : $"playing {mhz} MHz - press Enter to stop");
        var sw = Stopwatch.StartNew();
        var quit = new ManualResetEventSlim();
        if (seconds <= 0) new Thread(() => { Console.ReadLine(); quit.Set(); }) { IsBackground = true }.Start();
        while (!quit.IsSet && stopped == null && (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds))
        {
            quit.Wait(2000);
            var st = rx.Stereo;
            double cpu = dspTicks / (double)Stopwatch.Frequency / sw.Elapsed.TotalSeconds * 100;
            Console.WriteLine($"{sw.Elapsed.TotalSeconds,5:F0}s  ch {rx.ChannelPowerDb,6:F1} dBFS  pilot {(st.PilotLocked ? "LOCK" : "----")} snr {st.PilotSnrDb,5:F1}  blend {st.Blend:F2}  buf {player.BufferedMs,4:F0} ms  drift {player.DriftPpm,6:F0} ppm  underruns {player.Underruns}  dsp {cpu:F1}%");
        }
        dev.Stop();
        if (stopped != null) Console.WriteLine(stopped);
        return 0;
    }
}
