using System;
using System.Collections.Generic;
using Radio808.Core.Dsp;
using Radio808.Core.Hd;
using Radio808.Core.Radio;

namespace Radio808.Shared.Display;

public enum InfoItem { Song, Station, Genre, Artist, Title, Frequency }

/// <summary>What the display's two text lines show, by display mode: the same rules on every platform.</summary>
public static class DisplayText
{
    public static readonly (string Name, InfoItem Top, InfoItem Bottom)[] Modes =
    {
        ("SONG / STATION", InfoItem.Song, InfoItem.Station),
        ("STATION / SONG", InfoItem.Station, InfoItem.Song),
        ("STATION / GENRE", InfoItem.Station, InfoItem.Genre),
        ("ARTIST / TITLE", InfoItem.Artist, InfoItem.Title),
        ("FREQUENCY / STATION", InfoItem.Frequency, InfoItem.Station),
    };

    /// <summary>
    /// The text for one item, falling back through what the station offers (a song line falls back to the station,
    /// then the genre, then the frequency), skipping <paramref name="avoid"/> (what the other line already shows).
    /// </summary>
    public static string Item(InfoItem item, RadioEngine? eng, HdStatus? hd, RdsStatus? rds, bool synced, string mhz, string? avoid)
    {
        string freq = $"FM {mhz}";
        string? name = synced ? hd?.StationName : null;
        name ??= rds?.ProgramService?.Trim() is { Length: > 0 } ps ? ps : rds?.CallSign;
        string station = string.IsNullOrWhiteSpace(name) ? freq : name.Contains(mhz) ? name : $"{name} {mhz}";
        string? title = synced && !string.IsNullOrWhiteSpace(hd?.Title) ? hd!.Title : null;
        string? artist = synced && !string.IsNullOrWhiteSpace(hd?.Artist) ? hd!.Artist : null;
        string? song = title != null ? (artist != null ? $"{title} - {artist}" : title) : rds?.RadioText is { Length: > 0 } rt ? rt : null;
        string? genre = eng != null && synced && hd != null && hd.Programs.TryGetValue(eng.Program, out var type) && !string.IsNullOrWhiteSpace(type)
            && !string.Equals(type, "None", StringComparison.OrdinalIgnoreCase)   // some stations label a program "None"
            ? type : rds?.PtyName is { Length: > 0 } pty ? pty : null;

        IEnumerable<string?> choices = item switch
        {
            InfoItem.Song => new[] { song, station, genre, freq },
            InfoItem.Station => new[] { station, genre, freq },
            InfoItem.Genre => new[] { genre, freq, station },
            InfoItem.Artist => new[] { artist, song, station, freq },
            InfoItem.Title => new[] { title, station, genre, freq },
            _ => new[] { freq, station },
        };
        foreach (var c in choices)
            if (!string.IsNullOrWhiteSpace(c) && !string.Equals(c, avoid, StringComparison.OrdinalIgnoreCase)) return c;
        return avoid == null ? freq : "";
    }

    /// <summary>Scrolls a line longer than its window: a pause, then one character every third tick, then around again.</summary>
    public static void Marquee(int len, int cells, ref int chars, ref int ticks)
    {
        if (len <= cells) { chars = ticks = 0; return; }
        ticks++;
        const int start = 20, every = 3;
        if (ticks > start && (ticks - start) % every == 0 && ++chars > len + 3) chars = ticks = 0;
    }
}
