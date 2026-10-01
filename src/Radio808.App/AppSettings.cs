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
    /// <summary>Tuner gain in dB; null = automatic (not recommended: strong stations overload it).</summary>
    public double? GainDb { get; set; } = 16.6;
    public List<Preset?> Presets { get; set; } = new();
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are a convenience; never crash over them */ }
    }
}
