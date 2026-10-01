using System;
using System.Threading;
using System.Threading.Tasks;
using Radio808.Core.Audio;
using Radio808.Core.Devices;
using Radio808.Core.Hd;

namespace Radio808.Core.Radio;

/// <summary>
/// The whole radio: dongle -> FM receiver -> (HD decoder) -> HD/analog blender -> audio output.
/// All DSP runs on the dongle's thread except nrsc5, which has its own worker. Controls are thread-safe.
/// </summary>
public sealed class RadioEngine : IDisposable
{
    public const long MinFrequency = 87_500_000, MaxFrequency = 108_000_000;
    private const int RetuneSkipBlocks = 8;   // ~180 ms of samples still queued in USB buffers from the old station

    private readonly RtlSdrDevice _dev;
    private readonly FmReceiver _rx;
    private readonly HdDecoder _hd;
    private readonly HdBlender _blender;
    private readonly AudioPlayer _player;
    private long _frequency;
    private volatile int _retuneGeneration, _appliedGeneration;
    private int _skip;

    /// <summary>Raised (on the device thread) if the dongle stops streaming, e.g. unplugged.</summary>
    public event Action<string>? DeviceStopped;

    private RadioEngine(RtlSdrDevice dev, AudioPlayer player)
    {
        _dev = dev;
        _player = player;
        _rx = new FmReceiver();
        _blender = new HdBlender(FmReceiver.AudioRate);
        _hd = new HdDecoder(_blender);
        _rx.Baseband += (i, q) => _hd.Enqueue(i, q);
        _rx.Audio += a =>
        {
            _blender.Process(a);
            _player.Write(a);
        };
        _dev.Samples += OnSamples;
        _dev.Stopped += m => DeviceStopped?.Invoke(m);
    }

    /// <summary>Opens the dongle (the first one, unless given) and the default audio device, and starts playing.</summary>
    public static async Task<RadioEngine> StartAsync(long frequencyHz, RtlSdrInfo? device = null, double? gainDb = null)
    {
        var list = RtlSdrDevice.Enumerate();
        device ??= list.Count > 0 ? list[0] : throw new InvalidOperationException("No RTL-SDR found. Is it plugged in?");
        var dev = new RtlSdrDevice(device);
        AudioPlayer? player = null;
        try
        {
            dev.SampleRate = (uint)FmReceiver.DeviceRate;
            dev.Gain = gainDb;
            player = await AudioPlayer.CreateAsync(FmReceiver.AudioRate).ConfigureAwait(false);
            var engine = new RadioEngine(dev, player);
            engine.Frequency = frequencyHz;
            engine._hd.Start();
            dev.Start();
            return engine;
        }
        catch
        {
            player?.Dispose();
            dev.Dispose();
            throw;
        }
    }

    public RtlSdrDevice Device => _dev;
    public FmReceiver Receiver => _rx;
    public HdBlender Blender => _blender;
    public AudioPlayer Player => _player;
    public HdStatus Hd => _hd.Status;
    public HdDecoder HdDecoder => _hd;

    /// <summary>Station frequency in Hz. Setting it retunes immediately.</summary>
    public long Frequency
    {
        get => Interlocked.Read(ref _frequency);
        set
        {
            value = Math.Clamp(value, MinFrequency, MaxFrequency);
            Interlocked.Exchange(ref _frequency, value);
            _dev.Frequency = value;
            Interlocked.Increment(ref _retuneGeneration);   // the device thread resets the DSP on its next block
        }
    }

    /// <summary>HD program (0 = HD1).</summary>
    public uint Program { get => _hd.Program; set => _hd.Program = value; }
    public bool ForceAnalog { get => _blender.ForceAnalog; set => _blender.ForceAnalog = value; }
    public bool Equalizer { get => _rx.Equalizer.Enabled; set => _rx.Equalizer.Enabled = value; }
    public bool ForceMono { get => _rx.Stereo.ForceMono; set => _rx.Stereo.ForceMono = value; }
    public float Volume { get => _player.Volume; set => _player.Volume = Math.Clamp(value, 0, 1); }
    public bool Muted { get => _player.Muted; set => _player.Muted = value; }
    /// <summary>Tuner gain in dB, or null for automatic.</summary>
    public double? Gain { set => _dev.Gain = value; }

    private void OnSamples(ReadOnlySpan<float> iq)
    {
        int gen = _retuneGeneration;
        if (gen != _appliedGeneration)
        {
            _appliedGeneration = gen;
            _skip = RetuneSkipBlocks;
            _rx.Reset();
            _hd.Retune();
        }
        if (_skip > 0)
        {
            _skip--;
            return;
        }
        _rx.Process(iq);
    }

    public void Dispose()
    {
        _dev.Stop();
        _hd.Dispose();
        _player.Dispose();
        _dev.Dispose();
    }
}
