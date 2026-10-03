using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Radio808.Avalonia.Drawing;

/// <summary>
/// Drawing helpers over Avalonia's <see cref="DrawingContext"/> in the shape of the GDI+ calls the Windows faceplate
/// uses (arcs by angle, rounded rectangles, polygons, text in a rectangle with alignment), so the two stay line for line.
/// </summary>
internal static class G
{
    public static readonly FontFamily Ui = new("fonts:Inter#Inter");

    public static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
    public static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    /// <summary>The color with another alpha (GDI+ Color.FromArgb(alpha, color)).</summary>
    public static Color With(this Color c, int alpha) => Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);
    public static IBrush Brush(Color c) => new ImmutableSolidColorBrush(c);
    public static IPen Pen(Color c, double width, bool round = false) =>
        new ImmutablePen(new ImmutableSolidColorBrush(c), width, null, round ? PenLineCap.Round : PenLineCap.Flat);

    /// <summary>A linear gradient across <paramref name="r"/> at <paramref name="angleDeg"/> (GDI+ convention: 90 = top to bottom).</summary>
    public static IBrush Gradient(Rect r, Color c1, Color c2, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180, cx = 0.5, cy = 0.5;
        // the gradient line through the center, long enough to reach the corners in relative units
        double dx = Math.Cos(a) * 0.5, dy = Math.Sin(a) * 0.5;
        if (r.Width > 0 && r.Height > 0)
        {
            // GDI+ maps the angle in the rectangle's own aspect: scale the direction into relative space
            double len = Math.Max(Math.Abs(dx * r.Width), Math.Abs(dy * r.Height));
            dx = dx * r.Width / Math.Max(len, 1e-6) * 0.5; dy = dy * r.Height / Math.Max(len, 1e-6) * 0.5;
        }
        return new ImmutableLinearGradientBrush(
            new[] { new ImmutableGradientStop(0, c1), new ImmutableGradientStop(1, c2) },
            startPoint: new RelativePoint(cx - dx, cy - dy, RelativeUnit.Relative),
            endPoint: new RelativePoint(cx + dx, cy + dy, RelativeUnit.Relative));
    }

    public static Rect R(double x, double y, double w, double h) => new(x, y, w, h);
    public static Rect Inflate(Rect r, double dx, double dy) => new(r.X - dx, r.Y - dy, r.Width + 2 * dx, r.Height + 2 * dy);
    public static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    public static void FillRounded(this DrawingContext g, IBrush b, Rect r, double radius) => g.DrawRectangle(b, null, new RoundedRect(r, radius));
    public static void DrawRounded(this DrawingContext g, IPen p, Rect r, double radius) => g.DrawRectangle(null, p, new RoundedRect(r, radius));
    public static void FillRect(this DrawingContext g, IBrush b, double x, double y, double w, double h) => g.FillRectangle(b, new Rect(x, y, w, h));
    public static void FillEllipse(this DrawingContext g, IBrush b, Rect r) => g.DrawEllipse(b, null, Center(r), r.Width / 2, r.Height / 2);
    public static void FillEllipse(this DrawingContext g, IBrush b, double x, double y, double w, double h) => g.FillEllipse(b, new Rect(x, y, w, h));
    public static void DrawEllipse(this DrawingContext g, IPen p, Rect r) => g.DrawEllipse(null, p, Center(r), r.Width / 2, r.Height / 2);
    public static void DrawEllipse(this DrawingContext g, IPen p, double x, double y, double w, double h) => g.DrawEllipse(p, new Rect(x, y, w, h));
    public static void DrawLine(this DrawingContext g, IPen p, double x1, double y1, double x2, double y2) => g.DrawLine(p, new Point(x1, y1), new Point(x2, y2));

    /// <summary>An arc of the ellipse in <paramref name="r"/>, GDI+ style: degrees clockwise from 3 o'clock.</summary>
    public static void DrawArc(this DrawingContext g, IPen p, Rect r, double startDeg, double sweepDeg) => g.DrawGeometry(null, p, Arc(r, startDeg, sweepDeg));
    public static void DrawArc(this DrawingContext g, IPen p, double x, double y, double w, double h, double startDeg, double sweepDeg) => g.DrawArc(p, new Rect(x, y, w, h), startDeg, sweepDeg);

    public static Geometry Arc(Rect r, double startDeg, double sweepDeg)
    {
        var geo = new StreamGeometry();
        double rx = r.Width / 2, ry = r.Height / 2;
        var c = Center(r);
        Point At(double deg) => new(c.X + rx * Math.Cos(deg * Math.PI / 180), c.Y + ry * Math.Sin(deg * Math.PI / 180));
        using var ctx = geo.Open();
        ctx.BeginFigure(At(startDeg), false);
        // split long sweeps so no single arc segment reaches 360 degrees
        int parts = (int)Math.Ceiling(Math.Abs(sweepDeg) / 120.0);
        for (int i = 1; i <= Math.Max(1, parts); i++)
        {
            double sweep = sweepDeg / Math.Max(1, parts);
            double a1 = startDeg + sweep * i;
            ctx.ArcTo(At(a1), new Size(rx, ry), 0, Math.Abs(sweep) > 180, sweep >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
        }
        ctx.EndFigure(false);
        return geo;
    }

    public static Geometry Polygon(params Point[] pts)
    {
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        ctx.BeginFigure(pts[0], true);
        for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i]);
        ctx.EndFigure(true);
        return geo;
    }

    public static void FillPolygon(this DrawingContext g, IBrush b, params Point[] pts) => g.DrawGeometry(b, null, Polygon(pts));

    public enum Align { Near, Center, Far }

    public static FormattedText Text(string s, double px, Color c, bool bold = false)
    {
        var tf = new Typeface(Ui, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal);
        return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, px, Brush(c));
    }

    /// <summary>Text width in design units (GDI+ MeasureString).</summary>
    public static double TextWidth(string s, double px, bool bold = false) => Text(s, px, Rgb(0, 0, 0), bold).WidthIncludingTrailingWhitespace;

    /// <summary>One line of text in <paramref name="r"/>, vertically centered, with an ellipsis if it doesn't fit.</summary>
    public static void Label(this DrawingContext g, string s, double px, Color c, Rect r, Align align, bool bold = false)
    {
        var ft = Text(s, px, c, bold);
        ft.MaxTextWidth = Math.Max(1, r.Width);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        ft.TextAlignment = align switch { Align.Center => TextAlignment.Center, Align.Far => TextAlignment.Right, _ => TextAlignment.Left };
        g.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
    }

    /// <summary>Draws text with its top-left at (x, y), like GDI+ DrawString with GenericTypographic.</summary>
    public static void DrawString(this DrawingContext g, string s, double px, Color c, double x, double y, bool bold = false) =>
        g.DrawText(Text(s, px, c, bold), new Point(x, y));
}
