using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Radio808.App;

internal enum TextKind
{
    /// <summary>Section titles: Segoe UI Semibold 11 px.</summary>
    Title,
    /// <summary>Labels: Segoe UI 10 px, one line, trimmed to the given width.</summary>
    Small,
    /// <summary>The "TUNED" banner: Segoe UI Semibold 13 px, centered in its box.</summary>
    Banner,
}

/// <summary>
/// The drawing operations the instrument panel's live sections (spectrum, waterfall, multiplex, meters) need, in design
/// coordinates. Implemented with GDI+ (<see cref="GdiCanvas"/>: the flip animation, and the fallback) and Direct2D
/// (<see cref="D2DCanvas"/>: the live panel, on the GPU), so both draw from the same code and look the same.
/// </summary>
internal interface IPanelCanvas
{
    void Fill(RectangleF r, Color c);
    void Stroke(RectangleF r, Color c, float width = 1);
    void Line(float x0, float y0, float x1, float y1, Color c, float width = 1, DashStyle dash = DashStyle.Solid);
    void Text(string s, RectangleF r, TextKind kind, Color c, StringAlignment align = StringAlignment.Near);
    float MeasureText(string s, TextKind kind);
    /// <summary>A polyline through the points.</summary>
    void Trace(PointF[] pts, Color c, float width);
    /// <summary>The area under a trace down to <paramref name="bottom"/>, shaded from <paramref name="top"/> (at the top
    /// of <paramref name="shade"/>) to <paramref name="low"/> (at its bottom). Not anti-aliased: a trace covers its edge.</summary>
    void Area(PointF[] pts, float bottom, RectangleF shade, Color top, Color low);
    /// <summary>
    /// Rows [<paramref name="srcY"/>, +<paramref name="rows"/>) of the waterfall image, stretched to <paramref name="dest"/>,
    /// nearest-neighbor. The image is <paramref name="width"/> x <paramref name="height"/> pixels, 0x00RRGGBB.
    /// </summary>
    void Waterfall(WaterfallImage img, int srcY, int rows, RectangleF dest);
    void PushClip(RectangleF r);
    void PopClip();
    void PushTranslate(float dx, float dy);
    void PopTranslate();
}

/// <summary>The waterfall's pixels (0x00RRGGBB, row-major), shared by both canvases. <see cref="Version"/> changes on
/// every write, so the GPU copy is only refreshed when needed.</summary>
internal sealed class WaterfallImage : IDisposable
{
    public readonly int Width, Height;
    public readonly int[] Pixels;
    public int Version;
    private System.Runtime.InteropServices.GCHandle _pin;
    private Bitmap? _gdi;

    public WaterfallImage(int width, int height)
    {
        Width = width; Height = height;
        Pixels = new int[width * height];
        _pin = System.Runtime.InteropServices.GCHandle.Alloc(Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
    }

    /// <summary>A GDI+ bitmap over the same pixels (no copy).</summary>
    public Bitmap Gdi => _gdi ??= new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppRgb, _pin.AddrOfPinnedObject());

    public void Dispose()
    {
        _gdi?.Dispose();
        if (_pin.IsAllocated) _pin.Free();
    }
}

internal sealed class GdiCanvas : IPanelCanvas
{
    private static readonly Font TitleFont = new("Segoe UI Semibold", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font SmallFont = new("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font BannerFont = new("Segoe UI Semibold", 13, FontStyle.Regular, GraphicsUnit.Pixel);

    private readonly Graphics _g;
    private readonly Stack<GraphicsState> _states = new();

    public GdiCanvas(Graphics g) => _g = g;

    private static Font FontOf(TextKind k) => k switch { TextKind.Title => TitleFont, TextKind.Banner => BannerFont, _ => SmallFont };

    public void Fill(RectangleF r, Color c)
    {
        using var b = new SolidBrush(c);
        _g.FillRectangle(b, r);
    }

    public void Stroke(RectangleF r, Color c, float width = 1)
    {
        using var p = new Pen(c, width);
        _g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
    }

    public void Line(float x0, float y0, float x1, float y1, Color c, float width = 1, DashStyle dash = DashStyle.Solid)
    {
        using var p = new Pen(c, width) { DashStyle = dash };
        _g.DrawLine(p, x0, y0, x1, y1);
    }

    public void Text(string s, RectangleF r, TextKind kind, Color c, StringAlignment align = StringAlignment.Near)
    {
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = align, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = kind == TextKind.Banner ? StringAlignment.Center : StringAlignment.Near,
        };
        _g.DrawString(s, FontOf(kind), b, r, fmt);
    }

    public float MeasureText(string s, TextKind kind) =>
        _g.MeasureString(s, FontOf(kind), PointF.Empty, StringFormat.GenericTypographic).Width;

    public void Trace(PointF[] pts, Color c, float width)
    {
        using var p = new Pen(c, width);
        _g.DrawLines(p, pts);
    }

    public void Area(PointF[] pts, float bottom, RectangleF shade, Color top, Color low)
    {
        var poly = new PointF[pts.Length + 2];
        pts.CopyTo(poly, 0);
        poly[^2] = new PointF(pts[^1].X, bottom);
        poly[^1] = new PointF(pts[0].X, bottom);
        var mode = _g.SmoothingMode;
        _g.SmoothingMode = SmoothingMode.None;
        using (var b = new LinearGradientBrush(shade, top, low, 90f)) _g.FillPolygon(b, poly);
        _g.SmoothingMode = mode;
    }

    public void Waterfall(WaterfallImage img, int srcY, int rows, RectangleF dest)
    {
        if (rows <= 0) return;
        var st = _g.Save();
        _g.InterpolationMode = InterpolationMode.NearestNeighbor;
        _g.PixelOffsetMode = PixelOffsetMode.Half;
        _g.DrawImage(img.Gdi, dest, new RectangleF(0, srcY, img.Width, rows), GraphicsUnit.Pixel);
        _g.Restore(st);
    }

    public void PushClip(RectangleF r)
    {
        _states.Push(_g.Save());
        _g.SetClip(r, CombineMode.Intersect);
    }

    public void PopClip() => _g.Restore(_states.Pop());

    public void PushTranslate(float dx, float dy)
    {
        _states.Push(_g.Save());
        _g.TranslateTransform(dx, dy);
    }

    public void PopTranslate() => _g.Restore(_states.Pop());
}
