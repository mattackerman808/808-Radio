using Radio808.Shared.Display;

namespace Radio808.TV;

/// <summary>The display's dot-matrix text and seven-segment digits, drawn with CoreGraphics (same geometry as the Mac faceplate).</summary>
public static class Segments
{
    /// <summary>
    /// A window of <paramref name="cells"/> characters' worth of dot columns from <paramref name="offset"/>, unlit
    /// "ghost" dots behind, and a soft glow on the lit ones.
    /// </summary>
    public static void DrawDots(CGContext ctx, List<bool[]> columns, int offset, nfloat x, nfloat y, nfloat pitch, int cells, UIColor on, UIColor ghost, bool glow = true)
    {
        int width = cells * DotFont.CellCols - 1;
        nfloat d = pitch * 0.74f, gd = pitch * 1.7f;
        // three passes (ghost, glow, lit) as single paths: far cheaper than one fill per dot
        for (int pass = 0; pass < 3; pass++)
        {
            if (pass == 1 && !glow) continue;
            for (int cx = 0; cx < width; cx++)
            {
                int ci = cx + offset;
                var col = ci >= 0 && ci < columns.Count ? columns[ci] : null;
                bool gap = (cx % DotFont.CellCols) == DotFont.Cols;
                for (int ry = 0; ry < DotFont.Rows; ry++)
                {
                    bool lit = col != null && col[ry];
                    if (pass == 0 ? lit || gap : !lit) continue;
                    nfloat px = x + cx * pitch, py = y + ry * pitch, r = pass == 1 ? gd / 2 : d / 2;
                    ctx.AddEllipseInRect(new CGRect(px - r, py - r, 2 * r, 2 * r));
                }
            }
            ctx.SetFillColor((pass == 0 ? ghost : pass == 1 ? on.With(38) : on).CGColor);
            ctx.FillPath();
        }
    }

    // segments a..g as bits 0..6: a top, b top right, c bottom right, d bottom, e bottom left, f top left, g middle
    private static readonly byte[] Digits =
    {
        0b0111111, 0b0000110, 0b1011011, 0b1001111, 0b1100110, 0b1101101, 0b1111101, 0b0000111, 0b1111111, 0b1101111,
    };

    public static nfloat DigitWidth(nfloat h) => h * 0.56f;

    /// <summary>Width of a string of digits, ':' and '.' at height <paramref name="h"/>.</summary>
    public static nfloat SegmentsWidth(string text, nfloat h)
    {
        nfloat w = 0, dw = DigitWidth(h), gap = h * 0.16f;
        foreach (char ch in text) w += ch == ':' ? h * 0.24f : ch == '.' ? 0 : dw + gap;
        return w - gap;
    }

    /// <summary>Seven-segment digits, ':', '.' and ' ' (a blank digit): every segment is there, the unlit ones a ghost. Returns the right edge.</summary>
    public static nfloat DrawSegments(CGContext ctx, string text, nfloat x, nfloat y, nfloat h, UIColor on, UIColor ghost)
    {
        nfloat w = DigitWidth(h), gap = h * 0.16f;
        foreach (char ch in text)
        {
            if (ch == ':')
            {
                nfloat r = h * 0.075f, cx = x + h * 0.11f;
                ctx.SetFillColor(on.CGColor);
                ctx.FillEllipseInRect(Circle(cx + Lean(h * 0.32f, h), y + h * 0.32f, r));
                ctx.FillEllipseInRect(Circle(cx + Lean(h * 0.70f, h), y + h * 0.70f, r));
                x += h * 0.24f;
                continue;
            }
            if (ch == '.')   // decimal point: in the gap after the previous digit, on the baseline
            {
                nfloat r = h * 0.07f;
                ctx.SetFillColor(on.CGColor);
                ctx.FillEllipseInRect(Circle(x - gap / 2 + Lean(h - r, h), y + h - r, r));
                continue;
            }
            int mask = ch is >= '0' and <= '9' ? Digits[ch - '0'] : 0;
            for (int s = 0; s < 7; s++)
            {
                ctx.SetFillColor(((mask & (1 << s)) != 0 ? on : ghost).CGColor);
                Segment(ctx, s, x, y, w, h);
                ctx.FillPath();
            }
            x += w + gap;
        }
        return x;
    }

    private static CGRect Circle(nfloat cx, nfloat cy, nfloat r) => new(cx - r, cy - r, 2 * r, 2 * r);

    private const float Slant = 0.08f;
    private static nfloat Lean(nfloat dy, nfloat h) => (h - dy) * Slant;

    /// <summary>One segment as a six-sided bar (pointed ends), leaning with the digit, added to the context's path.</summary>
    private static void Segment(CGContext ctx, int s, nfloat x, nfloat y, nfloat w, nfloat h)
    {
        nfloat t = h * 0.12f, half = t / 2, mid = h / 2, gap = t * 0.18f;
        (nfloat x0, nfloat y0, nfloat x1, nfloat y1) = s switch
        {
            0 => (0, 0, w, 0), 1 => (w, 0, w, mid), 2 => (w, mid, w, h), 3 => (0, h, w, h),
            4 => (0, mid, 0, h), 5 => (0, 0, 0, mid), _ => ((nfloat)0, mid, w, mid),
        };
        x0 = half + x0 * (w - t) / w; x1 = half + x1 * (w - t) / w;
        y0 = half + y0 * (h - t) / h; y1 = half + y1 * (h - t) / h;
        CGPoint P(nfloat px, nfloat py) => new(x + px + Lean(py, h), y + py);
        CGPoint[] pts;
        if (y0 == y1)
        {
            nfloat a = x0 + half + gap, b = x1 - half - gap;
            pts = new[] { P(a - half, y0), P(a, y0 - half), P(b, y0 - half), P(b + half, y0), P(b, y0 + half), P(a, y0 + half) };
        }
        else
        {
            nfloat a = y0 + half + gap, b = y1 - half - gap;
            pts = new[] { P(x0, a - half), P(x0 + half, a), P(x0 + half, b), P(x0, b + half), P(x0 - half, b), P(x0 - half, a) };
        }
        ctx.MoveTo(pts[0].X, pts[0].Y);
        for (int i = 1; i < pts.Length; i++) ctx.AddLineToPoint(pts[i].X, pts[i].Y);
        ctx.ClosePath();
    }
}
