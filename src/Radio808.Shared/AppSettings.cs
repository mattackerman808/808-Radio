using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radio808.Shared;

public sealed class Preset
{
    public double Mhz { get; set; }
    public string? Name { get; set; }
    /// <summary>The HD program (0 = HD1, the main program, which is also what an analog-only station plays).</summary>
    public uint Program { get; set; }
}

/// <summary>User settings, saved as JSON in <see cref="Dir"/>/settings.json.</summary>
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
    /// <summary>Seek stops only at stations that sync HD.</summary>
    public bool SeekHd { get; set; }
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
    /// <summary>Display: 1 = station info on the big line and the song on the small one; otherwise the reverse.</summary>
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
    /// <summary>Apple TV: minutes without a remote press before the screen saver (0 = never).</summary>
    public int SaverMinutes { get; set; } = 3;

    /// <summary>%APPDATA%\808Radio on Windows, ~/Library/Application Support/808Radio on macOS, ~/.config/808Radio elsewhere.</summary>
#if __TVOS__
    // tvOS apps may only write to Library/Caches (small, and the system may purge it) and tmp
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "808Radio");
#else
    public static string Dir => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "808Radio")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "808Radio");
#endif
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new() : new(); }
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        }
        catch { /* settings are a convenience; never crash over them */ }
    }
}

/// <summary>
/// The settings' JSON code, generated at compile time: the trimmed tvOS build strips what reflection would need
/// (setters nothing but the deserializer calls, like the preset list), and this works the same everywhere.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJson : JsonSerializerContext
{
}
