using System;
using System.Threading;
using System.Threading.Tasks;
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
        Starting = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            double? gain = Settings.AutoGain ? null : Settings.GainDb ?? RadioEngine.DefaultGainDb;
            var engine = ReplayDirectory != null
                ? await Task.Run(() => RadioEngine.StartAsync(new Radio808.Core.Devices.ReplaySource(ReplayDirectory), Frequency, gain))
                : await Task.Run(() => RadioEngine.StartAsync(Frequency, null, gain));
            engine.Volume = Settings.Volume * Settings.Volume;
            engine.Muted = Settings.Muted;
            engine.ForceAnalog = Settings.ForceAnalog;
            engine.Equalizer = Settings.Equalizer;
            engine.ForceMono = Settings.ForceMono;
            engine.DeviceStopped += msg => _ui.Post(_ => OnDeviceStopped(msg), null);
            Engine = engine;
        }
        catch (Exception ex)
        {
            Error = Friendly(ex);
        }
        finally
        {
            Starting = false;
            Changed?.Invoke();
        }
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
        Error = msg + " Click to reconnect.";
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
        if (p != null) Tune((long)Math.Round(p.Mhz * 1e6));
    }

    public void StorePreset(int i, string? name)
    {
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

    public void Dispose()
    {
        Settings.FrequencyMhz = Frequency / 1e6;
        Settings.Save();
        StopEngine();
    }
}
