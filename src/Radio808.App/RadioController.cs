using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Radio808.Core.Devices;
using Radio808.Core.Radio;

namespace Radio808.App;

/// <summary>
/// Owns the radio engine and the settings; everything the UI does goes through here. UI thread only.
/// </summary>
public sealed class RadioController : IDisposable
{
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _seekCts;

    public AppSettings Settings { get; }
    /// <summary>Play recordings from this folder instead of a dongle (808Radio.exe --replay &lt;folder&gt;).</summary>
    public string? ReplayDirectory { get; init; }
    public RadioEngine? Engine { get; private set; }
    /// <summary>Why the radio isn't playing, if it isn't.</summary>
    public string? Error { get; private set; }
    public bool Starting { get; private set; }
    public bool Seeking => _seekCts != null;

    /// <summary>Something changed that the UI should show right away.</summary>
    public event Action? Changed;

    public RadioController(AppSettings settings)
    {
        Settings = settings;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public long Frequency => Engine?.Frequency ?? (long)Math.Round(Settings.FrequencyMhz * 1e6);

    public async Task StartAsync()
    {
        if (Engine != null || Starting) return;
        bool noUsbDongle = false;
        Starting = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            double? gain = Settings.AutoGain ? null : Settings.GainDb ?? RadioEngine.DefaultGainDb;
            string? net = Networked ? Settings.RtlTcpAddress : null;
            var engine = ReplayDirectory != null
                ? await Task.Run(() => RadioEngine.StartAsync(new ReplaySource(ReplayDirectory), Frequency, gain))
                : net != null
                ? await Task.Run(() => RadioEngine.StartAsync(new RtlTcpSource(net), Frequency, gain))
                : await Task.Run(() => RadioEngine.StartAsync(Frequency, null, gain));
            engine.Volume = Settings.Volume * Settings.Volume;
            engine.Muted = Settings.Muted;
            engine.ForceAnalog = Settings.ForceAnalog;
            engine.Equalizer = Settings.Equalizer;
            engine.ForceMono = Settings.ForceMono;
            engine.DeviceStopped += msg => _ui.Post(_ => OnDeviceStopped(msg), null);
            try
            {
                if (Settings.Ppm != 0) engine.Ppm = Settings.Ppm;
                if (Settings.BiasTee) engine.BiasTee = true;
            }
            catch (Exception ex) { AppLog.Write(ex); }
            Engine = engine;
        }
        catch (Exception ex)
        {
            Error = Friendly(ex);
            if (Networked) { Error += " Retrying…"; RetryLater(); }
            else if (ReplayDirectory == null && ex.Message.Contains("No RTL-SDR")) noUsbDongle = true;
        }
        finally
        {
            Starting = false;
            Changed?.Invoke();
        }
        // no dongle on this PC: if there's one on the network, use that
        if (noUsbDongle && !_disposed)
        {
            Error = "No RTL-SDR on this PC. Looking on the network…";
            Changed?.Invoke();
            var found = await DiscoverAsync();
            if (found.Count > 0 && Engine == null && !Starting && !Networked)
            {
                Message?.Invoke("FOUND " + found[0].Name.ToUpperInvariant());
                SetSource(found[0].ConnectAddress);
            }
            else if (Engine == null && !Starting)
            {
                Error = Friendly(new InvalidOperationException("No RTL-SDR"));
                Changed?.Invoke();
            }
        }
    }

    // ---- network dongles (rtl_tcp) found by mDNS ----

    private readonly Dictionary<string, (RtlTcpServer server, DateTime seen)> _found = new();
    private Task<IReadOnlyList<RtlTcpServer>>? _browse;

    /// <summary>rtl_tcp servers seen on the network in the last few minutes, by name.</summary>
    public IReadOnlyList<RtlTcpServer> Discovered => _found.Values
        .Where(f => DateTime.UtcNow - f.seen < TimeSpan.FromMinutes(5)).Select(f => f.server)
        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public bool Discovering => _browse != null;
    /// <summary>A network search started or finished.</summary>
    public event Action? DiscoveryChanged;

    /// <summary>Searches the local network for rtl_tcp servers (about 1.5 s). Concurrent calls share one search.</summary>
    public async Task<IReadOnlyList<RtlTcpServer>> DiscoverAsync()
    {
        if (_browse == null)
        {
            _browse = RtlTcpDiscovery.BrowseAsync(TimeSpan.FromSeconds(1.5));
            DiscoveryChanged?.Invoke();
            try
            {
                foreach (var s in await _browse) _found[s.ConnectAddress.ToLowerInvariant()] = (s, DateTime.UtcNow);
            }
            catch (Exception ex) { AppLog.Write(ex); }
            finally
            {
                _browse = null;
                DiscoveryChanged?.Invoke();
            }
        }
        else
        {
            try { await _browse; } catch { }
        }
        return Discovered;
    }

    /// <summary>Using a network dongle (rtl_tcp) rather than a USB one.</summary>
    public bool Networked => ReplayDirectory == null && Settings.UseRtlTcp && !string.IsNullOrWhiteSpace(Settings.RtlTcpAddress);

    private bool _retryPending, _disposed;

    /// <summary>A network dongle comes back by itself (Pi rebooted, Wi-Fi blip): keep trying every few seconds.</summary>
    private async void RetryLater()
    {
        if (_retryPending) return;
        _retryPending = true;
        await Task.Delay(2000);   // (a connection attempt to a host that's down adds up to 5 s more)
        _retryPending = false;
        if (!_disposed && Engine == null && !Starting && Networked) await StartAsync();
    }

    /// <summary>Switches between the USB dongle (address null) and an rtl_tcp server, and restarts the radio.</summary>
    public async void SetSource(string? rtlTcpAddress)
    {
        Settings.UseRtlTcp = rtlTcpAddress != null;
        if (rtlTcpAddress != null) Settings.RtlTcpAddress = rtlTcpAddress.Trim();
        Settings.Save();
        StopEngine();
        _ppmDone = false; _ppmRounds = 0; _ppmReadings.Clear();   // a different dongle has its own crystal error
        await StartAsync();
    }

    private static string Friendly(Exception ex)
    {
        string m = ex.Message;
        if (ex is DllNotFoundException) return "A native library is missing (rtlsdr.dll / libnrsc5.dll). Reinstall 808 Radio.";
        if (m.Contains("No RTL-SDR")) return "No RTL-SDR found. Plug in the dongle, then click to retry.";
        if (m.Contains("Could not open")) return "The RTL-SDR is busy. Close SDR# or any other program using it, then click to retry.";
        return m;
    }

    private void OnDeviceStopped(string msg)
    {
        StopEngine();
        if (Networked) { Error = msg + " Reconnecting…"; RetryLater(); }
        else Error = msg + " Click to reconnect.";
        Changed?.Invoke();
    }

    private void StopEngine()
    {
        _seekCts?.Cancel();
        var e = Engine;
        Engine = null;
        e?.Dispose();
    }

    // ---- tuning ----

    public void Tune(long hz)
    {
        CancelSeek();
        hz = Math.Clamp(hz, RadioEngine.MinFrequency, RadioEngine.MaxFrequency);
        Settings.FrequencyMhz = hz / 1e6;
        try
        {
            if (Engine != null) Engine.Frequency = hz;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            Message?.Invoke("TUNE FAILED - TRY AGAIN");
        }
        Changed?.Invoke();
    }

    /// <summary>A short message for the display (errors from controls, etc.).</summary>
    public event Action<string>? Message;

    /// <summary>One channel (200 kHz) up or down, wrapping at the band edges.</summary>
    public void Step(int direction)
    {
        long f = Frequency + direction * RadioEngine.ChannelStep;
        if (f > RadioEngine.LastChannel) f = RadioEngine.FirstChannel;
        if (f < RadioEngine.FirstChannel) f = RadioEngine.LastChannel;
        Tune(f);
    }

    public async void Seek(int direction)
    {
        if (Engine == null) return;
        if (Seeking) { CancelSeek(); return; }   // pressing seek again stops it
        var cts = _seekCts = new CancellationTokenSource();
        Changed?.Invoke();
        try { await Engine.SeekAsync(direction, cts.Token); }
        finally
        {
            if (_seekCts == cts) _seekCts = null;
            Settings.FrequencyMhz = Frequency / 1e6;
            Changed?.Invoke();
        }
    }

    public void CancelSeek()
    {
        _seekCts?.Cancel();
        _seekCts = null;
    }

    // ---- presets ----

    public void RecallPreset(int i)
    {
        var p = Settings.Presets[i];
        if (p == null) return;
        Tune((long)Math.Round(p.Mhz * 1e6));
        _lastPreset = i;
    }

    private int _lastPreset = -1;

    /// <summary>
    /// The preset for the station you're on, or -1: the one last recalled or saved if it's this frequency (several
    /// presets can hold the same one), else the first that matches.
    /// </summary>
    public int CurrentPreset
    {
        get
        {
            bool Matches(int i) => Settings.Presets[i] is { } p && Math.Abs(p.Mhz * 1e6 - Frequency) < 50_000;
            if (_lastPreset >= 0 && _lastPreset < Settings.Presets.Count && Matches(_lastPreset)) return _lastPreset;
            for (int i = 0; i < Settings.Presets.Count; i++) if (Matches(i)) return i;
            return -1;
        }
    }

    public void StorePreset(int i, string? name)
    {
        _lastPreset = i;
        Settings.Presets[i] = new Preset { Mhz = Math.Round(Frequency / 1e5) / 10, Name = name };
        Settings.Save();
        Changed?.Invoke();
    }

    // ---- audio and HD ----

    public void SetProgram(uint p)
    {
        if (Engine == null) return;
        Engine.Program = p;
        Settings.Program = p;
        Changed?.Invoke();
    }

    public void SetForceAnalog(bool on)
    {
        Settings.ForceAnalog = on;
        if (Engine != null) Engine.ForceAnalog = on;
        Changed?.Invoke();
    }

    public void SetVolume(float v)
    {
        Settings.Volume = Math.Clamp(v, 0, 1);
        if (Engine != null) Engine.Volume = Settings.Volume * Settings.Volume;
        Changed?.Invoke();
    }

    public void ToggleMute()
    {
        Settings.Muted = !Settings.Muted;
        if (Engine != null) Engine.Muted = Settings.Muted;
        Changed?.Invoke();
    }

    public void SetEqualizer(bool on)
    {
        Settings.Equalizer = on;
        if (Engine != null) Engine.Equalizer = on;
    }

    public void SetForceMono(bool on)
    {
        Settings.ForceMono = on;
        if (Engine != null) Engine.ForceMono = on;
    }

    /// <summary>Fixed gain in dB, or null for automatic peaking.</summary>
    public void SetGain(double? db)
    {
        Settings.AutoGain = db is null;
        if (db is not null) Settings.GainDb = db;
        if (Engine != null) Engine.Gain = db;
        Settings.Save();
    }

    // ---- frequency correction and antenna power ----

    private bool _ppmDone;
    private int _ppmRounds;

    private readonly System.Collections.Generic.List<double> _ppmReadings = new();
    private DateTime _nextPpmReading;

    /// <summary>
    /// Called about once a second. With auto-correction on, takes a reading of the dongle's frequency error every 3 s
    /// while a solid stereo station plays, and after 9 readings applies their median if it's 1.5 ppm or more and the
    /// readings agree (interquartile range under 2.5 ppm), at most 10 ppm at a time, up to 3 rounds per session.
    /// A single reading can be off by several ppm (seen through rtl_tcp: +-4 ppm between readings), and trusting single
    /// readings walked the setting to +22 ppm over many restarts; when the readings disagree, it leaves the setting alone.
    /// </summary>
    public void PpmTick()
    {
        var eng = Engine;
        if (eng == null || !Settings.AutoPpm || _ppmDone || Seeking) return;
        if (eng.MeasuredPpmError is not double err) return;
        if (DateTime.UtcNow < _nextPpmReading) return;
        _nextPpmReading = DateTime.UtcNow.AddSeconds(3);
        _ppmReadings.Add(err);
        if (_ppmReadings.Count < 9) return;

        _ppmReadings.Sort();
        double median = _ppmReadings[4], spread = _ppmReadings[6] - _ppmReadings[2];
        string readings = string.Join(" ", _ppmReadings.Select(r => r.ToString("+0.0;-0.0")));
        _ppmReadings.Clear();
        _ppmRounds++;
        if (_ppmRounds >= 3) _ppmDone = true;
        if (spread > 2.5)
        {
            AppLog.Write($"PPM: readings disagree (spread {spread:0.0} ppm: {readings}); leaving {eng.Ppm:+0;-0;0} ppm");
            return;
        }
        if (Math.Abs(median) < 1.5)
        {
            AppLog.Write($"PPM: {eng.Ppm:+0;-0;0} ppm is right (median error {median:+0.0;-0.0}: {readings})");
            _ppmDone = true;
            return;
        }
        int step = Math.Clamp((int)Math.Round(median), -10, 10);
        AppLog.Write($"PPM: median error {median:+0.0;-0.0} ppm ({readings}): {eng.Ppm:+0;-0;0} -> {eng.Ppm + step:+0;-0;0}");
        SetPpm(eng.Ppm + step);
        Message?.Invoke($"PPM {Settings.Ppm:+0;-0;0} CALIBRATED");
    }

    /// <summary>Measure again on the current station (from the menu).</summary>
    public void CalibratePpmNow()
    {
        Settings.AutoPpm = true;
        _ppmDone = false;
        _ppmRounds = 0;
        _ppmReadings.Clear();
        Engine?.Receiver.RestartCarrierOffset();
        Message?.Invoke("CALIBRATING PPM");
    }

    public void SetPpm(int ppm)
    {
        Settings.Ppm = Math.Clamp(ppm, -200, 200);
        try { if (Engine != null) Engine.Ppm = Settings.Ppm; }
        catch (Exception ex) { AppLog.Write(ex); }
        Settings.Save();
        Changed?.Invoke();
    }

    public void SetBiasTee(bool on)
    {
        Settings.BiasTee = on;
        try { if (Engine != null) Engine.BiasTee = on; }
        catch (Exception ex) { AppLog.Write(ex); }
        Settings.Save();
        Message?.Invoke(on ? "ANTENNA POWER ON" : "ANTENNA POWER OFF");
    }

    public void Dispose()
    {
        _disposed = true;
        Settings.FrequencyMhz = Frequency / 1e6;
        Settings.Save();
        StopEngine();
    }
}
