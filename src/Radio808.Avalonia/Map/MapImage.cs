using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Radio808.Avalonia.Drawing;
using Radio808.Core.Hd;
using Radio808.Shared.Map;

namespace Radio808.Avalonia.Map;

/// <summary>The shared base map (<see cref="BaseMap"/>) as an Avalonia bitmap, rendered once per box and size and cached as a PNG.</summary>
internal static class MapImage
{
    /// <summary>Null if the tiles couldn't be fetched (offline) and nothing is cached.</summary>
    public static async Task<Bitmap?> RenderAsync(MapBounds b, int width, int height)
    {
        string file = BaseMap.CacheFile(b, width, height);
        if (File.Exists(file))
        {
            try { return new Bitmap(file); } catch { }
        }
        var tiles = await BaseMap.FetchAsync(b, width);
        if (tiles == null) return null;
        var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var g = rtb.CreateDrawingContext()) BaseMap.Draw(new Canvas(g), b, width, height, tiles);
        try { rtb.Save(file); } catch { }
        return rtb;
    }

    /// <summary>The map canvas over Avalonia's drawing context.</summary>
    private sealed class Canvas : IMapCanvas
    {
        private readonly DrawingContext _g;
        public Canvas(DrawingContext g) => _g = g;

        private static Color C(MapColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
        private static Point P(MapPt p) => new(p.X, p.Y);

        public void FillRect(double x, double y, double w, double h, MapColor c) => _g.FillRectangle(G.Brush(C(c)), new Rect(x, y, w, h));

        public void FillPolygon(MapPt[][] rings, MapColor c)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.SetFillRule(FillRule.EvenOdd);
                foreach (var ring in rings)
                {
                    if (ring.Length < 3) continue;
                    ctx.BeginFigure(P(ring[0]), true);
                    for (int i = 1; i < ring.Length; i++) ctx.LineTo(P(ring[i]));
                    ctx.EndFigure(true);
                }
            }
            _g.DrawGeometry(G.Brush(C(c)), null, geo);
        }

        public void StrokeLines(MapPt[][] lines, MapColor c, double width, bool dashed)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                foreach (var line in lines)
                {
                    if (line.Length < 2) continue;
                    ctx.BeginFigure(P(line[0]), false);
                    for (int i = 1; i < line.Length; i++) ctx.LineTo(P(line[i]));
                    ctx.EndFigure(false);
                }
            }
            var pen = new global::Avalonia.Media.Pen(G.Brush(C(c)), width, dashed ? new DashStyle(new double[] { 2, 2 }, 0) : null, PenLineCap.Round, PenLineJoin.Round);
            _g.DrawGeometry(null, pen, geo);
        }

        private static FormattedText Text(string s, double px, MapColor c) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(G.Ui, FontStyle.Normal, FontWeight.Bold), px, G.Brush(C(c)));

        public (double Width, double Height) MeasureLabel(string s, double px)
        {
            var t = Text(s, px, default);
            return (t.Width, t.Height);
        }

        public void DrawLabel(string s, double px, double x, double y, MapColor c, MapColor halo)
        {
            var h = Text(s, px, halo);
            foreach (var (dx, dy) in new[] { (-1.2, 0.0), (1.2, 0.0), (0.0, -1.2), (0.0, 1.2), (-0.9, -0.9), (0.9, 0.9), (-0.9, 0.9), (0.9, -0.9) })
                _g.DrawText(h, new Point(x + dx, y + dy));
            _g.DrawText(Text(s, px, c), new Point(x, y));
        }
    }
}
