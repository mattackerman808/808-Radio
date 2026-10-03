using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Radio808.App;

/// <summary>The "808 Radio" mark: original artwork in the style of 808 HD's waves mark.</summary>
internal static class BrandMark
{
    public static readonly Color Orange = Color.FromArgb(0xF7, 0x94, 0x1D);

    /// <summary>Draws the mark with its left edge at <paramref name="x"/>, filling height <paramref name="h"/>. Returns its width.</summary>
    public static float Draw(Graphics g, float x, float y, float h, Color fore)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var numFont = new Font("Segoe UI", h * 0.72f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var wordFont = new Font("Segoe UI Semibold", h * 0.52f, FontStyle.Regular, GraphicsUnit.Pixel);
        var fmt = StringFormat.GenericTypographic;
        float w808 = g.MeasureString("808", numFont, PointF.Empty, fmt).Width;
        float numTop = y + (h - numFont.GetHeight(g)) / 2;
        using (var b = new SolidBrush(fore)) g.DrawString("808", numFont, b, x, numTop, fmt);
        float wx = x + w808 + h * 0.18f;
        float wordTop = y + (h - wordFont.GetHeight(g)) / 2 + h * 0.03f;
        using (var b = new SolidBrush(Orange)) g.DrawString("Radio", wordFont, b, wx, wordTop, fmt);
        float wWord = g.MeasureString("Radio", wordFont, PointF.Empty, fmt).Width;

        // three broadcast arcs after the name
        float cx = wx + wWord + h * 0.1f, cy = y + h / 2f;
        for (int i = 1; i <= 3; i++)
        {
            float r = h * 0.15f * i;
            using var pen = new Pen(Color.FromArgb(255 - (i - 1) * 60, Orange), Math.Max(1.5f, h / 11f))
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -45, 90);
        }
        return cx + h * 0.15f * 3 - x;
    }
}
