using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Radio808.App;

public sealed class Preset
{
    public double Mhz { get; set; }
    public string? Name { get; set; }
}

/// <summary>User settings, saved as JSON in %APPDATA%\808Radio\settings.json.</summary>
public sealed class AppSettings
{
    public const int PresetCount = 6;

    public double FrequencyMhz { get; set; } = 97.3;
    public uint Program { get; set; }
    /// <summary>Volume slider position 0..1 (the audio gain is its square, which feels linear).</summary>
    public float Volume { get; set; } = 0.7f;
    public bool Muted { get; set; }
    public bool ForceAnalog { get; set; }
    public bool Equalizer { get; set; } = true;
    public bool ForceMono { get; set; }
    /// <summary>Peak the tuner gain automatically for each station (recommended).</summary>
    public bool AutoGain { get; set; } = true;
    /// <summary>Fixed tuner gain in dB, used when <see cref="AutoGain"/> is off.</summary>
    public double? GainDb { get; set; } = 16.6;
    /// <summary>Dongle frequency correction in ppm.</summary>
    public int Ppm { get; set; }
    /// <summary>Measure the correction from FM carriers and apply it when it's off by 1.5 ppm or more.</summary>
    public bool AutoPpm { get; set; } = true;
    /// <summary>Antenna power through the coax (only for powered antennas / LNAs).</summary>
    public bool BiasTee { get; set; }
    /// <summary>Use a dongle on another machine (rtl_tcp at <see cref="RtlTcpAddress"/>) instead of a USB one.</summary>
    public bool UseRtlTcp { get; set; }
    /// <summary>The rtl_tcp server: "host" or "host:port" (default port 1234).</summary>
    public string? RtlTcpAddress { get; set; }
    public List<Preset?> Presets { get; set; } = new();
    /// <summary>Index into the faceplate's illumination colors.</summary>
    public int Illumination { get; set; }
    /// <summary>Display mode: 0 = now playing, 1 = station name, 2 = frequency.</summary>
    public int DisplayMode { get; set; }
    public bool AlwaysOnTop { get; set; }
    /// <summary>Show HD album art / station logos on the display (off: the spectrum analyzer is always there).</summary>
    public bool ShowAlbumArt { get; set; } = true;
    /// <summary>Instrument panel spectrum span: true = the dongle's full 1.49 MHz, false = the 744 kHz HD baseband.</summary>
    public bool PanelWideSpan { get; set; } = true;
    /// <summary>
    /// Instrument panel frame rate: 0 = every display refresh (the default; the GPU draws it), or 120 / 60 / 30 (the
    /// display's refresh divided evenly, so motion stays even). Without a GPU, GDI+ runs it at up to 60.
    /// </summary>
    public int PanelFps { get; set; }
    public static readonly int[] PanelFpsChoices = { 0, 120, 60, 30 };
    public int[]? WindowBounds { get; set; }

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "808Radio");
    private static string FilePath => Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { s = new(); }
        while (s.Presets.Count < PresetCount) s.Presets.Add(null);
        return s;
    }

    /// <summary>Never write settings (the paint benchmark runs beside the user's own copy).</summary>
    public static bool ReadOnly;

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are a convenience; never crash over them */ }
    }
}
