using System;
using System.Collections.Generic;
using System.Threading;
using Radio808.Core.Native;

namespace Radio808.Core.Devices;

public sealed record RtlSdrInfo(uint Index, string Name, string Manufacturer, string Product, string Serial)
{
    public override string ToString() => string.IsNullOrEmpty(Serial) ? Name : $"{Name} (SN {Serial})";
}

/// <summary>Receives interleaved float I/Q (range about -1..1). Called on the device thread.</summary>
public delegate void IqHandler(ReadOnlySpan<float> iq);

/// <summary>
/// An RTL-SDR dongle streaming complex samples. Samples arrive on a dedicated thread via <see cref="Samples"/>.
/// </summary>
public sealed unsafe class RtlSdrDevice : IIqSource
{
    public string Name => Info.ToString();

    private const uint BufferBytes = 64 * 1024;   // ~22 ms at 1.488 MS/s
    private const uint BufferCount = 15;

    private IntPtr _dev;
    private Thread? _thread;
    private RtlSdrNative.ReadAsyncCallback? _callback;   // kept alive while streaming
    private float[] _iq = new float[BufferBytes];
    private readonly float[] _lut = new float[256];
    private float _dcI, _dcQ;
    private volatile bool _running;

    public event IqHandler? Samples;
    /// <summary>Raised on the device thread if streaming stops unexpectedly (e.g. the dongle was unplugged).</summary>
    public event Action<string>? Stopped;

    public RtlSdrInfo Info { get; }
    public string TunerType { get; }
    public IReadOnlyList<double> Gains { get; }   // dB
    public bool IsStreaming => _running;
    public long SamplesDelivered { get; private set; }

    public static IReadOnlyList<RtlSdrInfo> Enumerate()
    {
        var list = new List<RtlSdrInfo>();
        uint n = RtlSdrNative.rtlsdr_get_device_count();
        for (uint i = 0; i < n; i++)
        {
            var (m, p, s) = RtlSdrNative.UsbStrings(i);
            list.Add(new RtlSdrInfo(i, RtlSdrNative.DeviceName(i), m, p, s));
        }
        return list;
    }

    public RtlSdrDevice(RtlSdrInfo info)
    {
        Info = info;
        int r = RtlSdrNative.rtlsdr_open(out _dev, info.Index);
        if (r < 0 || _dev == IntPtr.Zero)
            throw new InvalidOperationException($"Could not open {info} (error {r}). Is another program using it, or is the WinUSB driver (Zadig) missing?");
        TunerType = RtlSdrNative.rtlsdr_get_tuner_type(_dev) switch
        {
            1 => "E4000", 2 => "FC0012", 3 => "FC0013", 4 => "FC2580", 5 => "R820T", 6 => "R828D", _ => "unknown"
        };
        int count = RtlSdrNative.rtlsdr_get_tuner_gains(_dev, null);
        var gains = new int[Math.Max(count, 0)];
        fixed (int* g = gains) RtlSdrNative.rtlsdr_get_tuner_gains(_dev, g);
        Gains = Array.ConvertAll(gains, g => g / 10.0);
        for (int i = 0; i < 256; i++) _lut[i] = (i - 127.4f) / 128f;
        RtlSdrNative.rtlsdr_set_agc_mode(_dev, 0);
        // Setting the sample rate re-applies the current frequency, which is 0 Hz straight after open. Start in the FM
        // band so the tuner is never programmed with a meaningless frequency.
        // (We use upstream osmocom librtlsdr: the RTL-SDR Blog fork left the R820T receiving only noise on about half
        // of all opens with this dongle.)
        RtlSdrNative.rtlsdr_set_center_freq(_dev, 100_000_000);
    }

    // librtlsdr isn't thread-safe for control calls: tuning and gain changes both drive the tuner over I2C through a
    // repeater that's switched on and off around each command, so overlapping calls (e.g. the gain optimizer and a
    // tune from the UI) corrupt each other ("could not tune (error -9)"). Every control call takes this lock.
    private readonly object _ctl = new();

    public uint SampleRate
    {
        get { lock (_ctl) return RtlSdrNative.rtlsdr_get_sample_rate(_dev); }
        set { lock (_ctl) Check(RtlSdrNative.rtlsdr_set_sample_rate(_dev, value), "set sample rate"); }
    }

    public long Frequency
    {
        get { lock (_ctl) return RtlSdrNative.rtlsdr_get_center_freq(_dev); }
        set
        {
            lock (_ctl)
            {
                // a transient USB control failure shouldn't be fatal: retry a couple of times
                int r = 0;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    r = RtlSdrNative.rtlsdr_set_center_freq(_dev, (uint)value);
                    if (r >= 0) return;
                    Thread.Sleep(20);
                }
                Check(r, "tune");
            }
        }
    }

    private int _ppm;
    private bool _biasTee;

    /// <summary>Crystal error correction in ppm (the driver corrects both tuning and sample rate, and retunes).</summary>
    public int Ppm
    {
        get => _ppm;
        set
        {
            lock (_ctl)
            {
                int r = RtlSdrNative.rtlsdr_set_freq_correction(_dev, value);
                if (r == 0 || r == -2) _ppm = value;   // -2: already set
            }
        }
    }

    /// <summary>Tuner gain in dB, or null for the tuner's automatic gain.</summary>
    public double? Gain
    {
        get { lock (_ctl) return RtlSdrNative.rtlsdr_get_tuner_gain(_dev) / 10.0; }
        set
        {
            lock (_ctl)
            {
                if (value is null) { RtlSdrNative.rtlsdr_set_tuner_gain_mode(_dev, 0); return; }
                RtlSdrNative.rtlsdr_set_tuner_gain_mode(_dev, 1);
                RtlSdrNative.rtlsdr_set_tuner_gain(_dev, (int)Math.Round(value.Value * 10));
            }
        }
    }

    public bool BiasTee
    {
        get => _biasTee;
        set { lock (_ctl) if (RtlSdrNative.rtlsdr_set_bias_tee(_dev, value ? 1 : 0) == 0) _biasTee = value; }
    }
    public void Start()
    {
        if (_running) return;
        RtlSdrNative.rtlsdr_reset_buffer(_dev);
        _running = true;
        _callback = OnBuffer;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "rtlsdr", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        RtlSdrNative.rtlsdr_cancel_async(_dev);
        _thread?.Join();
        _thread = null;
    }

    private void ReadLoop()
    {
        int r = RtlSdrNative.rtlsdr_read_async(_dev, _callback!, IntPtr.Zero, BufferCount, BufferBytes);
        if (_running)
        {
            _running = false;
            Stopped?.Invoke(r < 0 ? $"The dongle stopped streaming (error {r}). Was it unplugged?" : "The dongle stopped streaming.");
        }
    }

    private void OnBuffer(byte* buf, uint len, IntPtr ctx)
    {
        if (!_running) return;
        int n = (int)len & ~1;
        if (_iq.Length < n) _iq = new float[n];
        // 8-bit unsigned -> float, with a slow DC blocker (removes the RTL's center spike)
        float dcI = _dcI, dcQ = _dcQ;
        const float a = 1e-5f;
        for (int i = 0; i < n; i += 2)
        {
            float x = _lut[buf[i]], y = _lut[buf[i + 1]];
            dcI += a * (x - dcI); dcQ += a * (y - dcQ);
            _iq[i] = x - dcI; _iq[i + 1] = y - dcQ;
        }
        _dcI = dcI; _dcQ = dcQ;
        SamplesDelivered += n / 2;
        try { Samples?.Invoke(new ReadOnlySpan<float>(_iq, 0, n)); }
        catch (Exception) { /* a consumer bug must not kill the USB thread */ }
    }

    private static void Check(int r, string what)
    {
        if (r < 0) throw new InvalidOperationException($"rtlsdr: could not {what} (error {r})");
    }

    public void Dispose()
    {
        Stop();
        if (_dev != IntPtr.Zero) { RtlSdrNative.rtlsdr_close(_dev); _dev = IntPtr.Zero; }
    }
}
