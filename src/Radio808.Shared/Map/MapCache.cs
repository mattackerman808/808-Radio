using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Radio808.Core.Hd;

namespace Radio808.Shared.Map;

/// <summary>
/// Keeps the base map usable without the internet: when a station's weather box becomes known, every tile the box
/// could need (at any likely window size) is fetched into the tile cache, so the map draws from disk from then on.
/// Once a day the archive on the CDN is checked (a HEAD request: its ETag); when it has changed, the cache is
/// cleared and fetched again. A fetch that fails (offline) is retried every few minutes. Driven by the apps'
/// once-a-second tick; everything runs in the background and never blocks the caller.
/// </summary>
public static class MapCache
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24), RetryEvery = TimeSpan.FromMinutes(5), FirstCheckAfter = TimeSpan.FromSeconds(45);
    private static MapBounds? _box;
    private static bool _boxComplete, _busy;
    private static DateTime _started = DateTime.UtcNow, _lastCheck = DateTime.MinValue, _lastAttempt = DateTime.MinValue;
    private static string VersionFile => Path.Combine(BaseMap.Tiles.CacheDir, "version.txt");

    /// <summary>What the cache has been up to, for a status line.</summary>
    public static string Status { get; private set; } = "";

    /// <summary>Called about once a second with the current station's weather box (null when none is known).</summary>
    public static void Tick(MapBounds? box)
    {
        if (_busy) return;
        var now = DateTime.UtcNow;
        if (box is { } b && (_box == null || !Same(_box.Value, b))) { _box = b; _boxComplete = false; _lastAttempt = DateTime.MinValue; }
        if (_box == null) return;
        bool checkDue = now - _started > FirstCheckAfter && now - _lastCheck > CheckEvery;
        bool fetchDue = !_boxComplete && now - _lastAttempt > RetryEvery;
        if (!checkDue && !fetchDue) return;
        _busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (checkDue) await CheckArchiveAsync().ConfigureAwait(false);
                if (!_boxComplete) await PrefetchAsync(_box.Value).ConfigureAwait(false);
            }
            catch (Exception ex) { AppLog.Write("map cache: " + ex.Message); }
            finally { _busy = false; }
        });
    }

    private static bool Same(MapBounds a, MapBounds b) =>
        Math.Abs(a.North - b.North) < 1e-4 && Math.Abs(a.West - b.West) < 1e-4 && Math.Abs(a.South - b.South) < 1e-4 && Math.Abs(a.East - b.East) < 1e-4;

    /// <summary>Asks the CDN for the archive's version; a changed archive empties the cache (tiles and rendered maps).</summary>
    private static async Task CheckArchiveAsync()
    {
        var version = await BaseMap.Tiles.VersionAsync().ConfigureAwait(false);
        if (version == null) { _lastCheck = DateTime.UtcNow - CheckEvery + TimeSpan.FromHours(1); return; }   // unreachable: again in an hour
        _lastCheck = DateTime.UtcNow;
        string? had = null;
        try { if (File.Exists(VersionFile)) had = File.ReadAllText(VersionFile); } catch { }
        if (had == version) return;
        if (had != null)
        {
            AppLog.Write($"map archive changed ({had} -> {version}): refetching the cache");
            try
            {
                foreach (var f in Directory.EnumerateFiles(BaseMap.Tiles.CacheDir, "*.mvt")) File.Delete(f);
                if (Directory.Exists(BaseMap.CacheDir)) foreach (var f in Directory.EnumerateFiles(BaseMap.CacheDir, "base-*.png")) File.Delete(f);
            }
            catch (Exception ex) { AppLog.Write("map cache clear: " + ex.Message); }
            BaseMap.Tiles.Reset();
            _boxComplete = false;
        }
        try { Directory.CreateDirectory(BaseMap.Tiles.CacheDir); File.WriteAllText(VersionFile, version); } catch { }
    }

    /// <summary>Fetches every tile the box could need; complete when none is missing afterwards.</summary>
    private static async Task PrefetchAsync(MapBounds box)
    {
        _lastAttempt = DateTime.UtcNow;
        var wanted = BaseMap.AllTilesFor(box).Where(t => !BaseMap.Tiles.IsCached(t.Z, t.X, t.Y)).ToList();
        if (wanted.Count == 0) { _boxComplete = true; Status = "map cached for offline use"; return; }
        Status = $"caching the map: {wanted.Count} tiles";
        int got = 0;
        foreach (var batch in wanted.Chunk(8))
        {
            var results = await Task.WhenAll(batch.Select(t => BaseMap.Tiles.GetTileAsync(t.Z, t.X, t.Y))).ConfigureAwait(false);
            got += results.Count(r => r != null);
            if (BaseMap.Tiles.Offline) break;
        }
        // tiles the archive simply doesn't have (open ocean at high zoom) count as done: they'd come back null again
        _boxComplete = !BaseMap.Tiles.Offline;
        Status = _boxComplete ? $"map cached for offline use ({got} tiles fetched)" : $"map caching paused (offline), {got} of {wanted.Count} fetched";
        AppLog.Write(Status);
    }
}
