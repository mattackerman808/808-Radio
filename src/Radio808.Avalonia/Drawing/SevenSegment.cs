using Avalonia;
using Avalonia.Media;

namespace Radio808.Avalonia.Drawing;

/// <summary>
/// Seven-segment digits, like a VFD clock: every segment is always there, the unlit ones a faint ghost. Digits lean
/// slightly, the way they do on the real displays.
/// </summary>
internal static class SevenSegment
{
    // segments a..g as bits 0..6: a top, b top right, c bottom right, d bottom, e bottom left, f top left, g middle
    private static readonly byte[] Digits =
    {
        0b0111111, 0b0000110, 0b1011011, 0b1001111, 0b1100110, 0b1101101, 0b1111101, 0b0000111, 0b1111111, 0b1101111,
    };

    /// <summary>Width of one digit for a given height.</summary>
    public static double DigitWidth(double h) => h * 0.56;

    /// <summary>Draws text of digits, ':', '.' and ' ' (blank digit) at (x, y), <paramref name="h"/> tall. Returns the right edge.</summary>
    public static double Draw(DrawingContext g, string text, double x, double y, double h, Color on, Color ghost)
    {
        var onB = G.Brush(on);
        var offB = G.Brush(ghost);
        double w = DigitWidth(h), gap = h * 0.16;
        foreach (char ch in text)
        {
            if (ch == ':')
            {
                double r = h * 0.075, cx = x + h * 0.11;
                g.DrawEllipse(onB, null, new Point(cx + Lean(h * 0.32, h), y + h * 0.32), r, r);
                g.DrawEllipse(onB, null, new Point(cx + Lean(h * 0.70, h), y + h * 0.70), r, r);
                x += h * 0.24;
                continue;
            }
            if (ch == '.')   // decimal point: in the gap after the previous digit, on the baseline
            {
                double r = h * 0.07;
                g.DrawEllipse(onB, null, new Point(x - gap / 2 + Lean(h - r, h), y + h - r), r, r);
                continue;
            }
            int mask = ch is >= '0' and <= '9' ? Digits[ch - '0'] : 0;
            for (int s = 0; s < 7; s++)
                g.DrawGeometry((mask & (1 << s)) != 0 ? onB : offB, null, Segment(s, x, y, w, h));
            x += w + gap;
        }
        return x;
    }

    private const double Slant = 0.08;
    private static double Lean(double dy, double h) => (h - dy) * Slant;

    /// <summary>One segment as a six-sided bar (pointed ends), leaning with the digit.</summary>
    private static Geometry Segment(int s, double x, double y, double w, double h)
    {
        double t = h * 0.12, half = t / 2, mid = h / 2, gap = t * 0.18;
        (double x0, double y0, double x1, double y1) = s switch
        {
            0 => (0, 0, w, 0),          // a
            1 => (w, 0, w, mid),        // b
            2 => (w, mid, w, h),        // c
            3 => (0, h, w, h),          // d
            4 => (0, mid, 0, h),        // e
            5 => (0, 0, 0, mid),        // f
            _ => (0, mid, w, mid),      // g
        };
        x0 = half + x0 * (w - t) / w; x1 = half + x1 * (w - t) / w;
        y0 = half + y0 * (h - t) / h; y1 = half + y1 * (h - t) / h;
        Point P(double px, double py) => new(x + px + Lean(py, h), y + py);
        if (y0 == y1)   // horizontal
        {
            double a = x0 + half + gap, b = x1 - half - gap;
            return G.Polygon(P(a - half, y0), P(a, y0 - half), P(b, y0 - half), P(b + half, y0), P(b, y0 + half), P(a, y0 + half));
        }
        else            // vertical
        {
            double a = y0 + half + gap, b = y1 - half - gap;
            return G.Polygon(P(x0, a - half), P(x0 + half, a), P(x0 + half, b), P(x0, b + half), P(x0 - half, b), P(x0 - half, a));
        }
    }
}
