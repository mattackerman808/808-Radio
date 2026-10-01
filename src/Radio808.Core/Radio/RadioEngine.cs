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
            TrackLevels(a);
            _player.Write(a);
        };
        _rx.Mpx += CaptureMpx;
        _dev.Samples += OnSamples;
        _dev.Stopped += m => DeviceStopped?.Invoke(m);
        _optimizer = new GainOptimizer(this, _dev.Gains, DefaultGainDb);
    }

    private readonly GainOptimizer _optimizer;

    /// <summary>
    /// Opens the dongle (the first one, unless given) and the default audio device, and starts playing.
    /// Gain null = automatic: the <see cref="GainOptimizer"/> peaks it for each station (the tuner's own AGC overloads
    /// on strong local stations, so it isn't used).
    /// </summary>
    public static async Task<RadioEngine> StartAsync(long frequencyHz, RtlSdrInfo? device = null, double? gainDb = null)
    {
        var list = RtlSdrDevice.Enumerate();
        device ??= list.Count > 0 ? list[0] : throw new InvalidOperationException("No RTL-SDR found. Is it plugged in?");
        return await StartAsync(new RtlSdrDevice(device), frequencyHz, gainDb).ConfigureAwait(false);
    }

    /// <summary>Starts the radio on any IQ source (e.g. a <see cref="ReplaySource"/>). Takes ownership of it.</summary>
    public static async Task<RadioEngine> StartAsync(IIqSource dev, long frequencyHz, double? gainDb = null)
    {
        AudioPlayer? player = null;
        try
        {
            dev.SampleRate = (uint)FmReceiver.DeviceRate;
            dev.Gain = gainDb ?? DefaultGainDb;   // automatic starts from the default and peaks from there
            player = await AudioPlayer.CreateAsync(FmReceiver.AudioRate).ConfigureAwait(false);
            var engine = new RadioEngine(dev, player);
            engine.Gain = gainDb;
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
    /// <summary>
    /// Tuner gain: a fixed value in dB, or null for automatic (the <see cref="GainOptimizer"/> peaks it per station).
    /// </summary>
    public double? Gain
    {
        get => AutoGain ? null : _dev.Gain;
        set
        {
            if (value is null) { _optimizer.Enabled = _dev.Gains.Count > 0; if (!_optimizer.Enabled) _dev.Gain = null; }
            else { _optimizer.Enabled = false; _dev.Gain = value; }
        }
    }

    public bool AutoGain => _optimizer.Enabled;
    public GainOptimizer GainOptimizer => _optimizer;
    /// <summary>The tuner gain actually in use, dB.</summary>
    public double CurrentGainDb => _dev.Gain ?? 0;

    internal void ApplyGain(double db) => _dev.Gain = db;

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
        IsSeeking = true;
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
            IsSeeking = false;
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

    // ---- more taps for the advanced panel ----

    private readonly float[] _mpxBuf = new float[SpectrumSize];
    private int _mpxFill;
    private readonly object _mpxLock = new();
    private float _peakL, _peakR;
    private long _dspTicks, _dspStart = System.Diagnostics.Stopwatch.GetTimestamp();
    private double _dspLoad;

    private void CaptureMpx(ReadOnlySpan<float> mpx)
    {
        if (_mpxFill >= SpectrumSize) return;
        lock (_mpxLock)
        {
            int n = Math.Min(mpx.Length, SpectrumSize - _mpxFill);
            mpx.Slice(0, n).CopyTo(_mpxBuf.AsSpan(_mpxFill));
            _mpxFill += n;
        }
    }

    private readonly float[] _devBuf = new float[2 * SpectrumSize];
    private int _devFill;
    private readonly object _devLock = new();

    private void CaptureDevice(ReadOnlySpan<float> iq)
    {
        if (_devFill >= SpectrumSize) return;
        lock (_devLock)
        {
            int n = Math.Min(iq.Length / 2, SpectrumSize - _devFill);
            iq.Slice(0, 2 * n).CopyTo(_devBuf.AsSpan(2 * _devFill));
            _devFill += n;
        }
    }

    /// <summary>
    /// Copies 4096 samples of the dongle's full-bandwidth I/Q (interleaved, at <see cref="FmReceiver.DeviceRate"/>,
    /// centered on the tuned frequency) into <paramref name="dest"/> if a fresh block is ready.
    /// </summary>
    public bool TryGetDeviceSpectrumBlock(float[] dest)
    {
        if (Volatile.Read(ref _devFill) < SpectrumSize) return false;
        lock (_devLock)
        {
            Array.Copy(_devBuf, dest, Math.Min(dest.Length, _devBuf.Length));
            _devFill = 0;
        }
        return true;
    }

    /// <summary>Copies 4096 MPX samples (at <see cref="FmReceiver.MpxRate"/>) into <paramref name="dest"/> if ready.</summary>
    public bool TryGetMpxBlock(float[] dest)
    {
        if (Volatile.Read(ref _mpxFill) < SpectrumSize) return false;
        lock (_mpxLock)
        {
            Array.Copy(_mpxBuf, dest, Math.Min(dest.Length, SpectrumSize));
            _mpxFill = 0;
        }
        return true;
    }

    private float _rmsL, _rmsR;

    private void TrackLevels(Span<float> a)
    {
        float l = 0, r = 0;
        double sl = 0, sr = 0;
        for (int i = 0; i < a.Length; i += 2)
        {
            l = Math.Max(l, Math.Abs(a[i])); r = Math.Max(r, Math.Abs(a[i + 1]));
            sl += a[i] * a[i]; sr += a[i + 1] * a[i + 1];
        }
        int n = Math.Max(1, a.Length / 2);
        // VU-like ballistics: ~300 ms integration for RMS; peaks attack instantly and fall back over ~1.5 s
        float k = Math.Min(1f, (float)(n / FmReceiver.AudioRate / 0.3));
        _rmsL += k * ((float)Math.Sqrt(sl / n) - _rmsL);
        _rmsR += k * ((float)Math.Sqrt(sr / n) - _rmsR);
        float fall = (float)Math.Pow(0.1, n / FmReceiver.AudioRate / 1.5);
        _peakL = Math.Max(l, _peakL * fall);
        _peakR = Math.Max(r, _peakR * fall);
    }

    /// <summary>Audio levels after the HD blend, before volume: RMS (VU-like) and decaying peaks, linear 0..1+.</summary>
    public (float RmsL, float RmsR, float PeakL, float PeakR) Levels => (_rmsL, _rmsR, _peakL, _peakR);

    /// <summary>Fraction of one core spent in DSP on the device thread (excludes nrsc5's own thread).</summary>
    public double DspLoad
    {
        get
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsed = (now - _dspStart) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (elapsed > 1)
            {
                _dspLoad = Interlocked.Exchange(ref _dspTicks, 0) / (double)System.Diagnostics.Stopwatch.Frequency / elapsed;
                _dspStart = now;
            }
            return _dspLoad;
        }
    }

    private void OnSamples(ReadOnlySpan<float> iq)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { ProcessSamples(iq); }
        finally { Interlocked.Add(ref _dspTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
    }

    private void ProcessSamples(ReadOnlySpan<float> iq)
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
        // ADC clipping: the 8-bit converter saturates at +-1.0 (the DC blocker can shift that by a hair)
        int clip = 0;
        for (int i = 0; i < iq.Length; i++) if (iq[i] > 0.98f || iq[i] < -0.98f) clip++;
        Interlocked.Add(ref _clipCount, clip);
        Interlocked.Add(ref _sampleCount, iq.Length);
        CaptureDevice(iq);
        _rx.Process(iq);
    }

    private long _clipCount, _sampleCount;

    /// <summary>Fraction of ADC samples at full scale since the last call.</summary>
    public double TakeClipFraction()
    {
        long c = Interlocked.Exchange(ref _clipCount, 0), n = Interlocked.Exchange(ref _sampleCount, 0);
        return n > 0 ? (double)c / n : 0;
    }

    /// <summary>Changes on every retune (for watchers like the gain optimizer).</summary>
    public int RetuneGeneration => _retuneGeneration;
    /// <summary>True while <see cref="SeekAsync"/> is stepping through the band.</summary>
    public bool IsSeeking { get; private set; }

    public void Dispose()
    {
        _optimizer.Dispose();
        _dev.Stop();
        _hd.Dispose();
        _player.Dispose();
        _dev.Dispose();
    }
}
