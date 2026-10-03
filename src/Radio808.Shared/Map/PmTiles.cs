using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Radio808.Shared.Map;

/// <summary>
/// Reads tiles out of a PMTiles v3 archive over HTTP byte-range requests (the format is one file: a header, a
/// directory tree, then the tiles, all addressed by offset), caching directories in memory and tiles on disk.
/// The archive here is Swiftcamp's basemap on its CDN, cut from the Protomaps planet; gzip inside and out.
/// Offline, cached tiles are served and the rest come back null (after one failure the network is left alone
/// for half a minute, so a map renders from the cache without waiting on every tile).
/// </summary>
public sealed class PmTiles
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _url, _cacheDir;
    private Header? _header;
    private readonly ConcurrentDictionary<long, Entry[]> _dirs = new();
    private DateTime _offlineUntil;

    public PmTiles(string url, string cacheDir)
    {
        _url = url;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public string CacheDir => _cacheDir;

    /// <summary>True after a fetch failed in the last half minute: the archive is treated as unreachable meanwhile.</summary>
    public bool Offline => DateTime.UtcNow < _offlineUntil;

    /// <summary>Forgets the archive's layout (after it changed on the server) so the next tile re-reads it.</summary>
    public void Reset() { _header = null; _dirs.Clear(); _offlineUntil = DateTime.MinValue; }

    /// <summary>
    /// A token for the archive version on the server (its ETag, else Last-Modified, else its length), or null if it
    /// can't be reached.
    /// </summary>
    public async Task<string?> VersionAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, _url);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return resp.Headers.ETag?.Tag ?? resp.Content.Headers.LastModified?.ToString("o") ?? resp.Content.Headers.ContentLength?.ToString();
        }
        catch (Exception ex) { AppLog.Write("map archive check: " + ex.Message); return null; }
    }

    public bool IsCached(int z, int x, int y) => File.Exists(TileFile(z, x, y));
    private string TileFile(int z, int x, int y) => Path.Combine(_cacheDir, $"{z}-{x}-{y}.mvt");

    private sealed record Header(long RootOff, long RootLen, long LeafOff, long TileOff, int InternalCompression, int TileCompression, int MinZoom, int MaxZoom);
    private readonly record struct Entry(long TileId, long Offset, int Length, int RunLength);

    private async Task<byte[]> RangeAsync(long offset, long length)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private static byte[] Decompress(byte[] data, int compression)
    {
        if (compression != 2) return data;   // 1 = none; gzip is what Protomaps builds use
        using var ms = new MemoryStream(data);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gz.CopyTo(outMs);
        return outMs.ToArray();
    }

    private async Task<Header> HeaderAsync()
    {
        if (_header != null) return _header;
        var h = await RangeAsync(0, 127).ConfigureAwait(false);
        if (h.Length < 127 || h[0] != 'P' || h[1] != 'M' || h[7] != 3) throw new InvalidDataException("not a PMTiles v3 archive");
        long U64(int at) => BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(at));
        return _header = new Header(U64(8), U64(16), U64(40), U64(56), h[97], h[98], h[100], h[101]);
    }

    private static Entry[] ParseDirectory(byte[] d)
    {
        int pos = 0;
        long Varint()
        {
            long v = 0; int shift = 0;
            while (true) { byte b = d[pos++]; v |= (long)(b & 0x7F) << shift; if ((b & 0x80) == 0) return v; shift += 7; }
        }
        int n = (int)Varint();
        var e = new Entry[n];
        long id = 0;
        for (int i = 0; i < n; i++) { id += Varint(); e[i] = e[i] with { TileId = id }; }
        for (int i = 0; i < n; i++) e[i] = e[i] with { RunLength = (int)Varint() };
        for (int i = 0; i < n; i++) e[i] = e[i] with { Length = (int)Varint() };
        for (int i = 0; i < n; i++)
        {
            long v = Varint();
            e[i] = e[i] with { Offset = v == 0 && i > 0 ? e[i - 1].Offset + e[i - 1].Length : v - 1 };
        }
        return e;
    }

    private async Task<Entry[]> DirectoryAsync(long offset, long length, int compression)
    {
        if (_dirs.TryGetValue(offset, out var cached)) return cached;
        var raw = await RangeAsync(offset, length).ConfigureAwait(false);
        var dir = ParseDirectory(Decompress(raw, compression));
        _dirs[offset] = dir;
        return dir;
    }

    /// <summary>The archive's tile id for z/x/y: zoom levels packed in order, tiles along a Hilbert curve within each.</summary>
    public static long TileId(int z, int x, int y)
    {
        long acc = 0;
        for (int i = 0; i < z; i++) acc += 1L << (2 * i);
        long n = 1L << z, d = 0;
        long rx, ry, tx = x, ty = y;
        for (long s = n / 2; s > 0; s /= 2)
        {
            rx = (tx & s) > 0 ? 1 : 0;
            ry = (ty & s) > 0 ? 1 : 0;
            d += s * s * ((3 * rx) ^ ry);
            if (ry == 0)
            {
                if (rx == 1) { tx = s - 1 - tx; ty = s - 1 - ty; }
                (tx, ty) = (ty, tx);
            }
        }
        return acc + d;
    }

    private static Entry? Find(Entry[] dir, long id)
    {
        int lo = 0, hi = dir.Length - 1;
        while (lo <= hi)
        {
            int m = (lo + hi) / 2;
            if (dir[m].TileId > id) hi = m - 1;
            else if (dir[m].TileId < id) lo = m + 1;
            else return dir[m];
        }
        if (hi >= 0)
        {
            var e = dir[hi];
            if (e.RunLength == 0 || id - e.TileId < e.RunLength) return e;   // a leaf pointer, or inside a run
        }
        return null;
    }

    /// <summary>
    /// The decoded (decompressed) tile, or null if the archive has none there or can't be reached. Tiles are
    /// cached on disk; a cached tile is served without touching the network.
    /// </summary>
    public async Task<byte[]?> GetTileAsync(int z, int x, int y)
    {
        string file = TileFile(z, x, y);
        if (File.Exists(file)) return await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        if (Offline) return null;
        try { return await FetchTileAsync(z, x, y, file).ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (!Offline) AppLog.Write("map tiles: " + ex.Message);
            _offlineUntil = DateTime.UtcNow.AddSeconds(30);
            return null;
        }
    }

    private async Task<byte[]?> FetchTileAsync(int z, int x, int y, string file)
    {
        var h = await HeaderAsync().ConfigureAwait(false);
        if (z < h.MinZoom || z > h.MaxZoom) return null;
        long id = TileId(z, x, y);
        var dir = await DirectoryAsync(h.RootOff, h.RootLen, h.InternalCompression).ConfigureAwait(false);
        for (int depth = 0; depth < 4; depth++)
        {
            var e = Find(dir, id);
            if (e == null) return null;
            if (e.Value.RunLength > 0)
            {
                var raw = await RangeAsync(h.TileOff + e.Value.Offset, e.Value.Length).ConfigureAwait(false);
                var tile = Decompress(raw, h.TileCompression);
                try { await File.WriteAllBytesAsync(file, tile).ConfigureAwait(false); } catch { }
                return tile;
            }
            dir = await DirectoryAsync(h.LeafOff + e.Value.Offset, e.Value.Length, h.InternalCompression).ConfigureAwait(false);
        }
        return null;
    }
}
