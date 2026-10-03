using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Radio808.Core.Hd;

namespace Radio808.Shared.Map;

/// <summary>
/// A static street map of a bounding box, drawn from Swiftcamp's basemap archive (Protomaps schema, OpenStreetMap
/// data, on the Swiftcamp CDN), in Swiftcamp's palette, as the ground under the HD Radio weather image. Web
/// Mercator, like the tiles. The style is here; each app supplies an <see cref="IMapCanvas"/> over its own drawing
/// and caches the rendered bitmap as a PNG (<see cref="CacheFile"/>) so a box renders once per size.
/// </summary>
public static class BaseMap
{
    public const string Archive = "street-z15-20260910.pmtiles";
    public const string ArchiveUrl = "https://cdn.swiftcamp.app/" + Archive;
    public const string Attribution = "© OpenStreetMap contributors";
    public static readonly string CacheDir = Path.Combine(AppSettings.Dir, "maps");
    public static readonly PmTiles Tiles = new(ArchiveUrl, Path.Combine(CacheDir, Path.GetFileNameWithoutExtension(Archive)));

    // ---- Web Mercator, as fractions of the world (0..1)
    public static double X(double lon) => (lon + 180) / 360;
    public static double Y(double lat)
    {
        double r = Math.Clamp(lat, -85, 85) * Math.PI / 180;
        return (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2;
    }

    /// <summary>Height / width of the box on a Mercator map.</summary>
    public static double Aspect(MapBounds b) => (Y(b.South) - Y(b.North)) / (X(b.East) - X(b.West));

    /// <summary>The rendered map's cache file for a box at a size (each app saves and loads it as a PNG).</summary>
    public static string CacheFile(MapBounds b, int width, int height)
    {
        Directory.CreateDirectory(CacheDir);
        return Path.Combine(CacheDir, string.Create(CultureInfo.InvariantCulture, $"base-{b.North:0.0000}-{b.West:0.0000}-{b.South:0.0000}-{b.East:0.0000}-{width}x{height}.png"));
    }

    private static readonly MapColor Earth = Hex("#f5f3ee"), Water = Hex("#b9d9e8"), Boundary = Hex("#9a9a9a");
    private static readonly MapColor RoadMinor = Hex("#ffffff"), RoadMajor = Hex("#fdf3d8"), RoadCasing = Hex("#d8d2c4"), RoadMajorCasing = Hex("#e8c77a");
    private static readonly MapColor Building = Hex("#dcd5c8"), Label = Hex("#40464e"), LabelHalo = Hex("#f7f5f0");
    private static readonly (MapColor color, string[] kinds)[] LandKinds =
    {
        (Hex("#d4e2c2"), new[] { "forest", "wood" }),
        (Hex("#dcecd2"), new[] { "park", "nature_reserve", "protected_area", "recreation_ground", "garden", "village_green", "allotments" }),
        (Hex("#e7edd6"), new[] { "grassland", "meadow", "grass", "pitch", "golf_course" }),
        (Hex("#e0e4c9"), new[] { "scrub", "heath" }),
        (Hex("#f0ebd9"), new[] { "farmland", "orchard", "vineyard" }),
        (Hex("#d9e7e2"), new[] { "wetland", "marsh", "swamp", "mud" }),
        (Hex("#f3ecd6"), new[] { "sand", "beach", "dune" }),
        (Hex("#dfdcd6"), new[] { "bare_rock", "barren", "scree", "quarry" }),
        (Hex("#eef4f7"), new[] { "glacier", "snow", "ice" }),
        (Hex("#edeae4"), new[] { "residential", "commercial", "industrial", "retail", "urban_area" }),
        (Hex("#efece5"), new[] { "school", "university", "college", "hospital", "cemetery", "military", "aerodrome", "airfield", "dam", "pier" }),
    };
    private static readonly Dictionary<string, MapColor> LandColor = LandKinds.SelectMany(k => k.kinds.Select(n => (n, k.color))).ToDictionary(t => t.n, t => t.color);

    private static MapColor Hex(string s) => MapColor.Hex(s);

    /// <summary>The tiles a box needs at <paramref name="width"/> pixels: a zoom where it is a few tiles across, at most ~40 tiles.</summary>
    public static (int Zoom, List<(int X, int Y)> Tiles) TilesFor(MapBounds b, int width)
    {
        double x0 = X(b.West), x1 = X(b.East), y0 = Y(b.North), y1 = Y(b.South);
        double spanX = x1 - x0, spanY = y1 - y0;
        int z = (int)Math.Clamp(Math.Ceiling(Math.Log2(width / (spanX * 256))), 3, 15);
        while (z > 3 && (spanX * (1 << z) + 2) * (spanY * (1 << z) + 2) > 42) z--;
        int n = 1 << z;
        int tx0 = (int)Math.Floor(x0 * n), tx1 = (int)Math.Floor(x1 * n), ty0 = (int)Math.Floor(y0 * n), ty1 = (int)Math.Floor(y1 * n);
        var coords = new List<(int X, int Y)>();
        for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) coords.Add((tx, ty));
        return (z, coords);
    }

    /// <summary>The widths a map window is likely to be rendered at (a 300 px minimum up to a 4K Retina display).</summary>
    public static readonly int[] LikelyWidths = { 300, 450, 600, 820, 1000, 1200, 1640, 2000, 2400, 3000, 4096 };

    /// <summary>Every tile the box could need at a likely size.</summary>
    public static IEnumerable<(int Z, int X, int Y)> AllTilesFor(MapBounds b) =>
        LikelyWidths.Select(w => TilesFor(b, w)).SelectMany(t => t.Tiles.Select(c => (t.Zoom, c.X, c.Y))).Distinct();

    /// <summary>The decoded tiles for a box at a width, ready to draw (a box's tiles are the same at any height).</summary>
    public sealed record MapTiles(int Zoom, List<(int X, int Y, MvtTile Tile)> Tiles);

    /// <summary>
    /// Fetches (or reads from the cache) and decodes the tiles for the box at <paramref name="width"/> pixels. Null
    /// if no tile could be had: offline with nothing cached.
    /// </summary>
    public static async Task<MapTiles?> FetchAsync(MapBounds b, int width)
    {
        var (z, coords) = TilesFor(b, width);
        var raw = await Task.WhenAll(coords.Select(c => Tiles.GetTileAsync(z, c.X, c.Y))).ConfigureAwait(false);
        var tiles = new List<(int X, int Y, MvtTile Tile)>();
        for (int i = 0; i < coords.Count; i++)
            if (raw[i] != null) { try { tiles.Add((coords[i].X, coords[i].Y, MvtTile.Decode(raw[i]!))); } catch (Exception ex) { AppLog.Write("map tile decode: " + ex.Message); } }
        return tiles.Count == 0 ? null : new MapTiles(z, tiles);
    }

    /// <summary>Draws the box at <paramref name="width"/> x <paramref name="height"/> pixels from fetched tiles.</summary>
    public static void Draw(IMapCanvas g, MapBounds b, int width, int height, MapTiles t) => Draw(g, b, width, height, t.Zoom, t.Tiles);

    private static void Draw(IMapCanvas g, MapBounds b, int width, int height, int z, List<(int X, int Y, MvtTile Tile)> tiles)
    {
        double x0 = X(b.West), x1 = X(b.East), y0 = Y(b.North), y1 = Y(b.South);
        double spanX = x1 - x0, spanY = y1 - y0;
        int n = 1 << z;
        g.FillRect(0, 0, width, height, Earth);
        // from a tile's units to pixels (the box isn't square, but Mercator is conformal, so x and y scale alike)
        (double sx, double sy, double ox, double oy) M(int tx, int ty, int extent) =>
            (width / (spanX * n * extent), height / (spanY * n * extent), (tx / (double)n - x0) / spanX * width, (ty / (double)n - y0) / spanY * height);
        MapPt[][] Px(MvtFeature f, (double sx, double sy, double ox, double oy) m) =>
            f.Parts.Select(part => part.Select(p => new MapPt(p.X * m.sx + m.ox, p.Y * m.sy + m.oy)).ToArray()).ToArray();
        double lineScale = Math.Max(0.5, width / 900.0);   // widths are tuned for ~900 px wide
        // each pass over every tile: layer order is global
        foreach (var (tx, ty, tile) in tiles)
        {
            var m = M(tx, ty, Extent(tile, "landuse"));
            foreach (var f in Features(tile, "landuse", MvtGeom.Polygon))
                if (f.Str("kind") is { } kind && LandColor.TryGetValue(kind, out var c)) g.FillPolygon(Px(f, m), c);
        }
        foreach (var (tx, ty, tile) in tiles)
        {
            var m = M(tx, ty, Extent(tile, "water"));
            foreach (var f in Features(tile, "water", MvtGeom.Polygon)) g.FillPolygon(Px(f, m), Water);
            if (z >= 10) foreach (var f in Features(tile, "water", MvtGeom.Line)) if (f.Num("min_zoom", 0) <= z) g.StrokeLines(Px(f, m), Water, Lerp(z, (10, 0.5), (13, 1.2), (16, 3.5)) * lineScale, false);
        }
        if (z >= 13)
            foreach (var (tx, ty, tile) in tiles)
            {
                var m = M(tx, ty, Extent(tile, "buildings"));
                foreach (var f in Features(tile, "buildings", MvtGeom.Polygon)) g.FillPolygon(Px(f, m), Building);
            }
        // roads: casing then fill, minor then major
        double minorW = Lerp(z, (11, 0.6), (13, 1.6), (15, 4.0), (17, 10.0)) * lineScale;
        double majorW = Lerp(z, (6, 0.8), (10, 2.0), (13, 4.5), (16, 12.0), (18, 24.0)) * lineScale;
        bool IsMajor(MvtFeature f) => f.Str("kind") is "highway" or "major_road";
        bool IsMinor(MvtFeature f) => f.Str("kind") == "minor_road" && f.Str("kind_detail") is "residential" or "unclassified" or "living_street";
        for (int pass = 0; pass < 4; pass++)
            foreach (var (tx, ty, tile) in tiles)
            {
                var m = M(tx, ty, Extent(tile, "roads"));
                foreach (var f in Features(tile, "roads", MvtGeom.Line))
                {
                    if (pass == 0 && z >= 12 && IsMinor(f)) g.StrokeLines(Px(f, m), RoadCasing, minorW + 1.4, false);
                    else if (pass == 1 && z >= 12 && IsMinor(f)) g.StrokeLines(Px(f, m), RoadMinor, minorW, false);
                    else if (pass == 2 && IsMajor(f)) g.StrokeLines(Px(f, m), RoadMajorCasing, majorW + 1.6, false);
                    else if (pass == 3 && IsMajor(f)) g.StrokeLines(Px(f, m), RoadMajor, majorW, false);
                }
            }
        foreach (var (tx, ty, tile) in tiles)
        {
            var m = M(tx, ty, Extent(tile, "boundaries"));
            foreach (var f in Features(tile, "boundaries", MvtGeom.Line))
                if (f.Str("kind") is "country" or "region" or "county")   // admin levels 2-6 (kind_detail is the level, as a string)
                    g.StrokeLines(Px(f, m), Boundary, Lerp(z, (2, 0.4), (6, 0.8), (10, 1.4)) * lineScale * (f.Str("kind") == "county" ? 0.7 : 1), true);
        }
        // place names, biggest first, skipping any that would overlap one already placed
        var placed = new List<(double x, double y, double w, double h)>();
        var places = new List<(double rank, string name, MapPt p)>();
        foreach (var (tx, ty, tile) in tiles)
        {
            var m = M(tx, ty, Extent(tile, "places"));
            foreach (var f in Features(tile, "places", MvtGeom.Point))
            {
                if (f.Str("kind") is not ("locality" or "region")) continue;
                if (f.Num("min_zoom", 0) > z + 1) continue;   // the tileset's own hint for when a place is worth a label
                if (f.Str("name") is not { Length: > 0 } name || f.Parts.Count == 0) continue;
                var p = f.Parts[0][0];
                places.Add((f.Num("population_rank"), name, new MapPt(p.X * m.sx + m.ox, p.Y * m.sy + m.oy)));
            }
        }
        foreach (var (rank, name, p) in places.OrderByDescending(t => t.rank))
        {
            if (p.X < 0 || p.Y < 0 || p.X > width || p.Y > height) continue;
            double size = Lerp(z, (4, 9 + rank * 0.4), (12, 11 + rank * 0.73)) * lineScale;
            var (tw, th) = g.MeasureLabel(name, size);
            double rx = p.X - tw / 2 - 4, ry = p.Y - th / 2 - 4, rw = tw + 8, rh = th + 8;
            if (placed.Any(q => q.x < rx + rw && rx < q.x + q.w && q.y < ry + rh && ry < q.y + q.h)) continue;
            placed.Add((rx, ry, rw, rh));
            g.DrawLabel(name, size, rx + 4, ry + 4, Label, LabelHalo);
        }
    }

    private static int Extent(MvtTile t, string layer) => t.Layer(layer)?.Extent ?? 4096;

    private static IEnumerable<MvtFeature> Features(MvtTile t, string layer, MvtGeom type) =>
        t.Layer(layer)?.Features.Where(f => f.Type == type) ?? Enumerable.Empty<MvtFeature>();

    private static double Lerp(double z, params (double z, double v)[] stops)
    {
        if (z <= stops[0].z) return stops[0].v;
        for (int i = 1; i < stops.Length; i++)
            if (z <= stops[i].z) return stops[i - 1].v + (stops[i].v - stops[i - 1].v) * (z - stops[i - 1].z) / (stops[i].z - stops[i - 1].z);
        return stops[^1].v;
    }
}
