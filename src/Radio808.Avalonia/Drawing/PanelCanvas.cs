using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Radio808.Avalonia.Drawing;

internal enum TextKind
{
    /// <summary>Section titles: semibold 11 px.</summary>
    Title,
    /// <summary>Labels: 10 px, one line, trimmed to the given width.</summary>
    Small,
    /// <summary>The "TUNED" banner: semibold 13 px, centered in its box.</summary>
    Banner,
    /// <summary>The statistics columns: monospace 11.5 px.</summary>
    Mono,
    /// <summary>Statistics headings: semibold 10.5 px.</summary>
    Heading,
}

internal enum Dash { Solid, Dot, DashLine }

/// <summary>
/// The drawing operations the instrument panel needs, in design coordinates (the Windows app's IPanelCanvas). Here
/// there is one implementation, over Avalonia's DrawingContext. Clipping is not an operation: the panel's clipped
/// content (the sliding spectrum and waterfall) is drawn by a child visual with ClipToBounds (see
/// <see cref="FaceplateControl"/>), because a clip pushed on the drawing context breaks later text on this Avalonia.
/// </summary>
internal interface IPanelCanvas
{
    void Fill(Rect r, Color c);
    void Stroke(Rect r, Color c, double width = 1);
    void Line(double x0, double y0, double x1, double y1, Color c, double width = 1, Dash dash = Dash.Solid);
    void Text(string s, Rect r, TextKind kind, Color c, G.Align align = G.Align.Near);
    double MeasureText(string s, TextKind kind);
    /// <summary>A polyline through the points.</summary>
    void Trace(Point[] pts, Color c, double width);
    /// <summary>The area under a trace down to <paramref name="bottom"/>, shaded from <paramref name="top"/> to <paramref name="low"/> over <paramref name="shade"/>.</summary>
    void Area(Point[] pts, double bottom, Rect shade, Color top, Color low);
    /// <summary>Rows [<paramref name="srcY"/>, +<paramref name="rows"/>) of the waterfall image, stretched to <paramref name="dest"/>, nearest-neighbor.</summary>
    void Waterfall(WaterfallImage img, int srcY, int rows, Rect dest);
    /// <summary>Shifts everything drawn afterwards (until <see cref="PopTranslate"/>).</summary>
    void PushTranslate(double dx, double dy);
    void PopTranslate();
}

/// <summary>The waterfall's pixels (0xFFRRGGBB, row-major). <see cref="Version"/> changes on every write.</summary>
internal sealed class WaterfallImage
{
    public readonly int Width, Height;
    public readonly int[] Pixels;
    public int Version;
    public WaterfallImage(int width, int height) { Width = width; Height = height; Pixels = new int[width * height]; }
}

internal sealed class AvaloniaPanelCanvas : IPanelCanvas
{
    private static readonly FontFamily Mono = new("Menlo, Consolas, monospace");
    private readonly DrawingContext _g;
    private double _dx, _dy;
    private readonly System.Collections.Generic.Stack<(double, double)> _offsets = new();
    private static WriteableBitmap? _water;
    private static int _waterVersion = -1;

    public AvaloniaPanelCanvas(DrawingContext g, double dx = 0, double dy = 0) { _g = g; _dx = dx; _dy = dy; }

    private Rect T(Rect r) => new(r.X + _dx, r.Y + _dy, r.Width, r.Height);
    private Point T(Point p) => new(p.X + _dx, p.Y + _dy);

    private static (Typeface tf, double px) FontOf(TextKind k) => k switch
    {
        TextKind.Title => (new Typeface(G.Ui, FontStyle.Normal, FontWeight.SemiBold), 11),
        TextKind.Banner => (new Typeface(G.Ui, FontStyle.Normal, FontWeight.SemiBold), 13),
        TextKind.Mono => (new Typeface(Mono), 11.5),
        TextKind.Heading => (new Typeface(G.Ui, FontStyle.Normal, FontWeight.SemiBold), 10.5),
        _ => (new Typeface(G.Ui), 10),
    };

    public void Fill(Rect r, Color c) => _g.FillRectangle(G.Brush(c), T(r));
    public void Stroke(Rect r, Color c, double width = 1) => _g.DrawRectangle(null, G.Pen(c, width), T(r));

    public void Line(double x0, double y0, double x1, double y1, Color c, double width = 1, Dash dash = Dash.Solid)
    {
        IPen pen = dash == Dash.Solid ? G.Pen(c, width)
            : new global::Avalonia.Media.Pen(G.Brush(c), width, new DashStyle(dash == Dash.Dot ? new double[] { 1, 2 } : new double[] { 4, 3 }, 0));
        _g.DrawLine(pen, T(new Point(x0, y0)), T(new Point(x1, y1)));
    }

    public void Text(string s, Rect r, TextKind kind, Color c, G.Align align = G.Align.Near)
    {
        var (tf, px) = FontOf(kind);
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, px, G.Brush(c));
        if (kind == TextKind.Title) r = new Rect(r.X, r.Y, Math.Max(r.Width, 900), Math.Max(r.Height, 16));   // titles are unbounded
        ft.MaxTextWidth = Math.Max(1, r.Width);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        ft.TextAlignment = align switch { G.Align.Center => TextAlignment.Center, G.Align.Far => TextAlignment.Right, _ => TextAlignment.Left };
        double y = kind == TextKind.Banner ? r.Y + (r.Height - ft.Height) / 2 : r.Y;
        _g.DrawText(ft, T(new Point(r.X, y)));
    }

    public double MeasureText(string s, TextKind kind)
    {
        var (tf, px) = FontOf(kind);
        return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, px, Brushes.Black).WidthIncludingTrailingWhitespace;
    }

    public void Trace(Point[] pts, Color c, double width)
    {
        if (pts.Length < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(T(pts[0]), false);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(T(pts[i]));
            ctx.EndFigure(false);
        }
        _g.DrawGeometry(null, G.Pen(c, width), geo);
    }

    public void Area(Point[] pts, double bottom, Rect shade, Color top, Color low)
    {
        if (pts.Length < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(T(pts[0]), true);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(T(pts[i]));
            ctx.LineTo(T(new Point(pts[^1].X, bottom)));
            ctx.LineTo(T(new Point(pts[0].X, bottom)));
            ctx.EndFigure(true);
        }
        _g.DrawGeometry(G.Gradient(T(shade), top, low, 90), null, geo);
    }

    public void Waterfall(WaterfallImage img, int srcY, int rows, Rect dest)
    {
        if (rows <= 0) return;
        if (_water == null || _water.PixelSize.Width != img.Width || _water.PixelSize.Height != img.Height)
        {
            _water = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _waterVersion = -1;
        }
        if (_waterVersion != img.Version)
        {
            using var fb = _water.Lock();
            unsafe
            {
                for (int y = 0; y < img.Height; y++)
                {
                    int* row = (int*)((byte*)fb.Address + y * fb.RowBytes);
                    var src = img.Pixels.AsSpan(y * img.Width, img.Width);
                    for (int x = 0; x < img.Width; x++) row[x] = src[x] | unchecked((int)0xFF000000);
                }
            }
            _waterVersion = img.Version;
        }
        _g.DrawImage(_water, new Rect(0, srcY, img.Width, rows), T(dest));
    }

    public void PushTranslate(double dx, double dy) { _offsets.Push((_dx, _dy)); _dx += dx; _dy += dy; }
    public void PopTranslate() { (_dx, _dy) = _offsets.Pop(); }
}
