using System.Drawing;
using System.Drawing.Drawing2D;

namespace Radio808.App;

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
    public static float DigitWidth(float h) => h * 0.56f;

    /// <summary>Draws text of digits, ':' and ' ' (blank digit) at (x, y), <paramref name="h"/> tall. Returns the right edge.</summary>
    public static float Draw(Graphics g, string text, float x, float y, float h, Color on, Color ghost)
    {
        using var onB = new SolidBrush(on);
        using var offB = new SolidBrush(ghost);
        float w = DigitWidth(h), gap = h * 0.16f;
        foreach (char ch in text)
        {
            if (ch == ':')
            {
                float r = h * 0.075f, cx = x + h * 0.11f;
                g.FillEllipse(onB, cx - r + Lean(h * 0.32f, h), y + h * 0.32f - r, 2 * r, 2 * r);
                g.FillEllipse(onB, cx - r + Lean(h * 0.70f, h), y + h * 0.70f - r, 2 * r, 2 * r);
                x += h * 0.24f;
                continue;
            }
            int mask = ch is >= '0' and <= '9' ? Digits[ch - '0'] : 0;
            for (int s = 0; s < 7; s++)
            {
                using var seg = Segment(s, x, y, w, h);
                g.FillPath((mask & (1 << s)) != 0 ? onB : offB, seg);
            }
            x += w + gap;
        }
        return x;
    }

    private const float Slant = 0.08f;
    private static float Lean(float dy, float h) => (h - dy) * Slant;

    /// <summary>One segment as a six-sided bar (pointed ends), leaning with the digit.</summary>
    private static GraphicsPath Segment(int s, float x, float y, float w, float h)
    {
        float t = h * 0.12f, half = t / 2, mid = h / 2, gap = t * 0.18f;
        // segment endpoints in the digit's own (unleaned) frame, horizontal or vertical
        (float x0, float y0, float x1, float y1) = s switch
        {
            0 => (0, 0, w, 0),          // a
            1 => (w, 0, w, mid),        // b
            2 => (w, mid, w, h),        // c
            3 => (0, h, w, h),          // d
            4 => (0, mid, 0, h),        // e
            5 => (0, 0, 0, mid),        // f
            _ => (0, mid, w, mid),      // g
        };
        // inset so the segments don't touch, and pull the frame in by half a thickness so the bars stay inside w x h
        x0 = half + x0 * (w - t) / w; x1 = half + x1 * (w - t) / w;
        y0 = half + y0 * (h - t) / h; y1 = half + y1 * (h - t) / h;
        PointF P(float px, float py) => new(x + px + Lean(py, h), y + py);
        var path = new GraphicsPath();
        if (y0 == y1)   // horizontal
        {
            float a = x0 + half + gap, b = x1 - half - gap;
            path.AddPolygon(new[] { P(a - half, y0), P(a, y0 - half), P(b, y0 - half), P(b + half, y0), P(b, y0 + half), P(a, y0 + half) });
        }
        else            // vertical
        {
            float a = y0 + half + gap, b = y1 - half - gap;
            path.AddPolygon(new[] { P(x0, a - half), P(x0 + half, a), P(x0 + half, b), P(x0, b + half), P(x0 - half, b), P(x0 - half, a) });
        }
        return path;
    }
}
