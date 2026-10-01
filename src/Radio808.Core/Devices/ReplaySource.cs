using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Radio808.Core.Devices;

/// <summary>
/// Plays .cu8 recordings (8-bit I/Q at the radio's device rate) in real time as if they were a dongle. Each file's
/// frequency comes from its name ("97.3.cu8", "st98.5.cu8", "kqed-88.5.cu8"); tuning picks the matching recording
/// and loops it, and channels without one play low-level noise. For development and demos without hardware.
/// </summary>
public sealed class ReplaySource : IIqSource
{
    private const int BlockBytes = 64 * 1024;
    private readonly Dictionary<long, string> _files = new();
    private Thread? _thread;
    private volatile bool _running;
    private long _frequency;
    private volatile bool _retuned;

    public event IqHandler? Samples;
    public event Action<string>? Stopped;

    public ReplaySource(string directory)
    {
        foreach (var f in Directory.GetFiles(directory, "*.cu8"))
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(f), @"(\d{2,3}\.\d)");
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
                _files[(long)Math.Round(mhz * 10) * 100_000] = f;
        }
        if (_files.Count == 0) throw new InvalidOperationException($"No recordings named like 97.3.cu8 in {directory}");
        Name = $"Replay ({_files.Count} recordings)";
    }

    public string Name { get; }
    public IReadOnlyCollection<long> Frequencies => _files.Keys;
    public uint SampleRate { get; set; } = 1_488_375;
    public double? Gain { get; set; }
    public IReadOnlyList<double> Gains { get; } = Array.Empty<double>();
    public int Ppm { get; set; }
    public bool BiasTee { get; set; }

    public long Frequency
    {
        get => Interlocked.Read(ref _frequency);
        set { Interlocked.Exchange(ref _frequency, value); _retuned = true; }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "replay" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
        _thread = null;
    }

    private void Run()
    {
        var iq = new float[BlockBytes];
        var raw = new byte[BlockBytes];
        var rnd = new Random(1);
        FileStream? fs = null;
        double blockSec = BlockBytes / 2.0 / SampleRate;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long blocks = 0;
        try
        {
            _retuned = true;
            while (_running)
            {
                if (_retuned)
                {
                    _retuned = false;
                    fs?.Dispose();
                    fs = _files.TryGetValue((Frequency + 50_000) / 100_000 * 100_000, out var path) ? File.OpenRead(path) : null;
                }
                if (fs != null)
                {
                    int n = fs.Read(raw, 0, BlockBytes);
                    if (n < BlockBytes) { fs.Position = 0; n = fs.Read(raw, 0, BlockBytes); }
                    for (int i = 0; i < BlockBytes; i++) iq[i] = (raw[i] - 127.4f) / 128f;
                }
                else
                {
                    for (int i = 0; i < BlockBytes; i++) iq[i] = (float)(rnd.NextDouble() - 0.5) * 0.06f;
                }
                Samples?.Invoke(iq);
                // real-time pacing
                blocks++;
                double ahead = blocks * blockSec - clock.Elapsed.TotalSeconds;
                if (ahead > 0) Thread.Sleep(TimeSpan.FromSeconds(ahead));
            }
        }
        catch (Exception ex)
        {
            _running = false;
            Stopped?.Invoke("Replay stopped: " + ex.Message);
        }
        finally
        {
            fs?.Dispose();
        }
    }

    public void Dispose() => Stop();
}
