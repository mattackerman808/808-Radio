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
    /// <summary>US FM channels: 87.9 .. 107.9 MHz in 200 kHz steps.</summary>
    public const long FirstChannel = 87_900_000, LastChannel = 107_900_000, ChannelStep = 200_000;
    public const double DefaultGainDb = 16.6;
    /// <summary>
    /// Seek stops where the FM envelope ripple (after the multipath equalizer) is below this. Measured across the
    /// Bay Area band: stations 0.03-0.35, empty channels 0.68-0.73.
    /// </summary>
    public const double SeekRippleThreshold = 0.42;
    private const int SpectrumSize = 4096;
    private const int RetuneSkipBlocks = 8;   // ~180 ms of samples still queued in USB buffers from the old station

    private readonly IIqSource _dev;
    private readonly FmReceiver _rx;
    private readonly HdDecoder _hd;
    private readonly HdBlender _blender;
    private readonly AudioPlayer _player;
    private long _frequency;
    private volatile int _retuneGeneration, _appliedGeneration;
    private int _skip;

    /// <summary>Raised (on the device thread) if the dongle stops streaming, e.g. unplugged.</summary>
    public event Action<string>? DeviceStopped;

    private RadioEngine(IIqSource dev, AudioPlayer player)
    {
        _dev = dev;
        _player = player;
        _rx = new FmReceiver();
        _blender = new HdBlender(FmReceiver.AudioRate);
        _hd = new HdDecoder(_blender);
        _rx.Baseband += (i, q) =>
        {
            _hd.Enqueue(i, q);
            CaptureSpectrum(i, q);
        };
        _rx.Audio += a =>
        {
            _blender.Process(a);
            _player.Write(a);
        };
        _dev.Samples += OnSamples;
        _dev.Stopped += m => DeviceStopped?.Invoke(m);
    }

    /// <summary>
    /// Opens the dongle (the first one, unless given) and the default audio device, and starts playing.
    /// Gain null = the tuner's AGC, which overloads on strong local stations; a fixed gain works much better.
    /// </summary>
    public static async Task<RadioEngine> StartAsync(long frequencyHz, RtlSdrInfo? device = null, double? gainDb = DefaultGainDb)
    {
        var list = RtlSdrDevice.Enumerate();
        device ??= list.Count > 0 ? list[0] : throw new InvalidOperationException("No RTL-SDR found. Is it plugged in?");
        return await StartAsync(new RtlSdrDevice(device), frequencyHz, gainDb).ConfigureAwait(false);
    }

    /// <summary>Starts the radio on any IQ source (e.g. a <see cref="ReplaySource"/>). Takes ownership of it.</summary>
    public static async Task<RadioEngine> StartAsync(IIqSource dev, long frequencyHz, double? gainDb = DefaultGainDb)
    {
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

    public IIqSource Device => _dev;
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

    /// <summary>
    /// Seeks to the next station up (+1) or down (-1), wrapping around the band. Audio is muted while seeking.
    /// Returns false (and goes back to the start frequency) if the whole band was empty.
    /// </summary>
    public async Task<bool> SeekAsync(int direction, CancellationToken ct = default)
    {
        long start = Frequency;
        long f = (start - FirstChannel) / ChannelStep * ChannelStep + FirstChannel;   // snap to the channel grid
        int channels = (int)((LastChannel - FirstChannel) / ChannelStep) + 1;
        bool wasMuted = Muted;
        Muted = true;
        try
        {
            for (int i = 0; i < channels; i++)
            {
                f += Math.Sign(direction) * ChannelStep;
                if (f > LastChannel) f = FirstChannel;
                if (f < FirstChannel) f = LastChannel;
                Frequency = f;
                await Task.Delay(330, ct).ConfigureAwait(false);   // retune skip + equalizer settling
                _rx.Equalizer.TakeEnvelopeRipple();
                await Task.Delay(180, ct).ConfigureAwait(false);
                double ripple = _rx.Equalizer.TakeEnvelopeRipple();
                if (ripple > 0 && ripple < SeekRippleThreshold) return true;
            }
            Frequency = start;
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;   // stay wherever the user stopped it
        }
        finally
        {
            Muted = wasMuted;
        }
    }

    // ---- spectrum tap: the UI pulls a block of baseband now and then ----

    private readonly float[] _spec = new float[2 * SpectrumSize];
    private int _specFill;
    private readonly object _specLock = new();

    private void CaptureSpectrum(ReadOnlySpan<float> i, ReadOnlySpan<float> q)
    {
        if (_specFill >= SpectrumSize) return;   // waiting for the UI to take it
        lock (_specLock)
        {
            int n = Math.Min(i.Length, SpectrumSize - _specFill);
            for (int k = 0; k < n; k++) { _spec[2 * (_specFill + k)] = i[k]; _spec[2 * (_specFill + k) + 1] = q[k]; }
            _specFill += n;
        }
    }

    /// <summary>
    /// Copies the latest 4096 baseband samples (interleaved I/Q at <see cref="FmReceiver.HdRate"/>, centered on the
    /// station) into <paramref name="dest"/> if a fresh block is ready.
    /// </summary>
    public bool TryGetSpectrumBlock(float[] dest)
    {
        if (Volatile.Read(ref _specFill) < SpectrumSize) return false;
        lock (_specLock)
        {
            Array.Copy(_spec, dest, Math.Min(dest.Length, _spec.Length));
            _specFill = 0;
        }
        return true;
    }

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
