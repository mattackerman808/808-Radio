using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Radio808.Avalonia.Drawing;
using Radio808.Core.Hd;
using Radio808.Shared;

namespace Radio808.Avalonia.Map;

/// <summary>
/// A static street map of a bounding box, drawn from Swiftcamp's basemap archive (Protomaps schema, OpenStreetMap
/// data, on the Swiftcamp CDN), in Swiftcamp's palette, as the ground under the HD Radio weather image. Web
/// Mercator, like the tiles. Rendered once per box and size and cached as a PNG.
/// </summary>
internal static class BaseMap
{
    public const string Archive = "street-z15-20260910.pmtiles";
    public const string Attribution = "© OpenStreetMap contributors";
    private static readonly string CacheDir = Path.Combine(AppSettings.Dir, "maps");
    private static readonly PmTiles Tiles = new("https://cdn.swiftcamp.app/" + Archive, Path.Combine(CacheDir, Path.GetFileNameWithoutExtension(Archive)));

    // ---- Web Mercator, as fractions of the world (0..1)
    public static double X(double lon) => (lon + 180) / 360;
    public static double Y(double lat)
    {
        double r = Math.Clamp(lat, -85, 85) * Math.PI / 180;
        return (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2;
    }

    /// <summary>Height / width of the box on a Mercator map.</summary>
    public static double Aspect(MapBounds b) => (Y(b.South) - Y(b.North)) / (X(b.East) - X(b.West));

    private static readonly Color Earth = Hex("#f5f3ee"), Water = Hex("#b9d9e8"), Boundary = Hex("#9a9a9a");
    private static readonly Color RoadMinor = Hex("#ffffff"), RoadMajor = Hex("#fdf3d8"), RoadCasing = Hex("#d8d2c4"), RoadMajorCasing = Hex("#e8c77a");
    private static readonly Color Building = Hex("#dcd5c8"), Label = Hex("#40464e"), LabelHalo = Hex("#f7f5f0");
    private static readonly (Color color, string[] kinds)[] LandKinds =
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
    private static readonly Dictionary<string, Color> LandColor = LandKinds.SelectMany(k => k.kinds.Select(n => (n, k.color))).ToDictionary(t => t.n, t => t.color);

    private static Color Hex(string s) => Color.Parse(s);

    /// <summary>
    /// Renders the box at <paramref name="width"/> x <paramref name="height"/> pixels (the map's own cache serves it
    /// next time). Null if the tiles couldn't be fetched (offline) and nothing is cached.
    /// </summary>
    public static async Task<Bitmap?> RenderAsync(MapBounds b, int width, int height)
    {
        Directory.CreateDirectory(CacheDir);
        string key = string.Create(CultureInfo.InvariantCulture, $"base-{b.North:0.0000}-{b.West:0.0000}-{b.South:0.0000}-{b.East:0.0000}-{width}x{height}.png");
        string file = Path.Combine(CacheDir, key);
        if (File.Exists(file))
        {
            try { return new Bitmap(file); } catch { }
        }
        double x0 = X(b.West), x1 = X(b.East), y0 = Y(b.North), y1 = Y(b.South);
        double spanX = x1 - x0, spanY = y1 - y0;
        // a zoom where the box is a few tiles across, at most ~40 tiles
        int z = (int)Math.Clamp(Math.Ceiling(Math.Log2(width / (spanX * 256))), 3, 15);
        while (z > 3 && (spanX * (1 << z) + 2) * (spanY * (1 << z) + 2) > 42) z--;
        int n = 1 << z;
        int tx0 = (int)Math.Floor(x0 * n), tx1 = (int)Math.Floor(x1 * n), ty0 = (int)Math.Floor(y0 * n), ty1 = (int)Math.Floor(y1 * n);
        var coords = new List<(int x, int y)>();
        for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) coords.Add((tx, ty));
        byte[]?[] raw;
        try { raw = await Task.WhenAll(coords.Select(c => Tiles.GetTileAsync(z, c.x, c.y))).ConfigureAwait(true); }
        catch (Exception ex) { AppLog.Write("map tiles: " + ex.Message); return null; }
        var tiles = new List<(int x, int y, MvtTile tile)>();
        for (int i = 0; i < coords.Count; i++)
            if (raw[i] != null) { try { tiles.Add((coords[i].x, coords[i].y, MvtTile.Decode(raw[i]!))); } catch (Exception ex) { AppLog.Write("map tile decode: " + ex.Message); } }
        if (tiles.Count == 0) return null;

        var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var g = rtb.CreateDrawingContext())
        {
            g.FillRectangle(G.Brush(Earth), new Rect(0, 0, width, height));
            // the transform from a tile's units to pixels
            Matrix M(int tx, int ty, int extent)
            {
                double s = width / (spanX * n * extent);   // pixels per tile unit (the box isn't square, but Mercator is conformal: same in y)
                double ox = (tx / (double)n - x0) / spanX * width, oy = (ty / (double)n - y0) / spanY * height;
                double sy = height / (spanY * n * extent);
                return Matrix.CreateScale(s, sy) * Matrix.CreateTranslation(ox, oy);
            }
            double px = width / (spanX * n * 4096);   // pixels per tile unit, for widths
            double lineScale = Math.Max(0.5, width / 900.0);   // widths are tuned for ~900 px wide
            // each pass over every tile: layer order is global
            foreach (var (tx, ty, tile) in tiles)
                using (g.PushTransform(M(tx, ty, Extent(tile, "landuse"))))
                    foreach (var f in Features(tile, "landuse", MvtGeom.Polygon))
                        if (f.Str("kind") is { } kind && LandColor.TryGetValue(kind, out var c)) Fill(g, f, c);
            foreach (var (tx, ty, tile) in tiles)
                using (g.PushTransform(M(tx, ty, Extent(tile, "water"))))
                {
                    foreach (var f in Features(tile, "water", MvtGeom.Polygon)) Fill(g, f, Water);
                    if (z >= 10) foreach (var f in Features(tile, "water", MvtGeom.Line)) if (f.Num("min_zoom", 0) <= z) Stroke(g, f, Water, Lerp(z, (10, 0.5), (13, 1.2), (16, 3.5)) * lineScale / px);
                }
            if (z >= 13)
                foreach (var (tx, ty, tile) in tiles)
                    using (g.PushTransform(M(tx, ty, Extent(tile, "buildings"))))
                        foreach (var f in Features(tile, "buildings", MvtGeom.Polygon)) Fill(g, f, Building);
            // roads: casing then fill, minor then major
            var minorW = Lerp(z, (11, 0.6), (13, 1.6), (15, 4.0), (17, 10.0)) * lineScale;
            var majorW = Lerp(z, (6, 0.8), (10, 2.0), (13, 4.5), (16, 12.0), (18, 24.0)) * lineScale;
            bool IsMajor(MvtFeature f) => f.Str("kind") is "highway" or "major_road";
            bool IsMinor(MvtFeature f) => f.Str("kind") == "minor_road" && f.Str("kind_detail") is "residential" or "unclassified" or "living_street";
            foreach (var pass in new[] { 0, 1, 2, 3 })
                foreach (var (tx, ty, tile) in tiles)
                    using (g.PushTransform(M(tx, ty, Extent(tile, "roads"))))
                        foreach (var f in Features(tile, "roads", MvtGeom.Line))
                        {
                            if (pass == 0 && z >= 12 && IsMinor(f)) Stroke(g, f, RoadCasing, (minorW + 1.4) / px);
                            else if (pass == 1 && z >= 12 && IsMinor(f)) Stroke(g, f, RoadMinor, minorW / px);
                            else if (pass == 2 && IsMajor(f)) Stroke(g, f, RoadMajorCasing, (majorW + 1.6) / px);
                            else if (pass == 3 && IsMajor(f)) Stroke(g, f, RoadMajor, majorW / px);
                        }
            foreach (var (tx, ty, tile) in tiles)
                using (g.PushTransform(M(tx, ty, Extent(tile, "boundaries"))))
                    foreach (var f in Features(tile, "boundaries", MvtGeom.Line))
                        if (f.Str("kind") is "country" or "region" or "county")   // admin levels 2-6 (kind_detail is the level, as a string)
                            Stroke(g, f, Boundary, Lerp(z, (2, 0.4), (6, 0.8), (10, 1.4)) * lineScale / px * (f.Str("kind") == "county" ? 0.7 : 1), dashed: true);
            // place names, biggest first, skipping any that would overlap one already placed
            var placed = new List<Rect>();
            var places = new List<(double rank, string name, Point p)>();
            foreach (var (tx, ty, tile) in tiles)
            {
                var m = M(tx, ty, Extent(tile, "places"));
                foreach (var f in Features(tile, "places", MvtGeom.Point))
                {
                    if (f.Str("kind") is not ("locality" or "region")) continue;
                    if (f.Num("min_zoom", 0) > z + 1) continue;   // the tileset's own hint for when a place is worth a label
                    if (f.Str("name") is not { Length: > 0 } name || f.Parts.Count == 0) continue;
                    places.Add((f.Num("population_rank"), name, f.Parts[0][0].Transform(m)));
                }
            }
            foreach (var (rank, name, p) in places.OrderByDescending(t => t.rank))
            {
                if (p.X < 0 || p.Y < 0 || p.X > width || p.Y > height) continue;
                double size = Lerp(z, (4, 9 + rank * 0.4), (12, 11 + rank * 0.73)) * lineScale;
                var ft = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(G.Ui, FontStyle.Normal, FontWeight.Bold), size, G.Brush(Label));
                var r = new Rect(p.X - ft.Width / 2, p.Y - ft.Height / 2, ft.Width, ft.Height).Inflate(4);
                if (placed.Any(q => q.Intersects(r))) continue;
                placed.Add(r);
                var halo = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(G.Ui, FontStyle.Normal, FontWeight.Bold), size, G.Brush(LabelHalo));
                foreach (var (dx, dy) in new[] { (-1.2, 0.0), (1.2, 0.0), (0.0, -1.2), (0.0, 1.2), (-0.9, -0.9), (0.9, 0.9), (-0.9, 0.9), (0.9, -0.9) })
                    g.DrawText(halo, new Point(r.X + 4 + dx, r.Y + 4 + dy));
                g.DrawText(ft, new Point(r.X + 4, r.Y + 4));
            }
        }
        try { rtb.Save(file); } catch { }
        return rtb;
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

    private static void Fill(DrawingContext g, MvtFeature f, Color c)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.SetFillRule(FillRule.EvenOdd);   // inner rings are holes
            foreach (var ring in f.Parts)
            {
                if (ring.Length < 3) continue;
                ctx.BeginFigure(ring[0], true);
                for (int i = 1; i < ring.Length; i++) ctx.LineTo(ring[i]);
                ctx.EndFigure(true);
            }
        }
        g.DrawGeometry(G.Brush(c), null, geo);
    }

    private static void Stroke(DrawingContext g, MvtFeature f, Color c, double widthUnits, bool dashed = false)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var line in f.Parts)
            {
                if (line.Length < 2) continue;
                ctx.BeginFigure(line[0], false);
                for (int i = 1; i < line.Length; i++) ctx.LineTo(line[i]);
                ctx.EndFigure(false);
            }
        }
        IPen pen = dashed
            ? new global::Avalonia.Media.Pen(G.Brush(c), widthUnits, new DashStyle(new double[] { 2, 2 }, 0), PenLineCap.Round, PenLineJoin.Round)
            : new global::Avalonia.Media.Pen(G.Brush(c), widthUnits, null, PenLineCap.Round, PenLineJoin.Round);
        g.DrawGeometry(null, pen, geo);
    }
}
