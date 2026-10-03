using System;
using Avalonia;
using Avalonia.Media;

namespace Radio808.Avalonia.Drawing;

/// <summary>The "808 Radio" mark: original artwork in the style of 808 HD's waves mark.</summary>
internal static class BrandMark
{
    public static readonly Color Orange = G.Rgb(0xF7, 0x94, 0x1D);

    /// <summary>Draws the mark with its left edge at <paramref name="x"/>, filling height <paramref name="h"/>. Returns its width.</summary>
    public static double Draw(DrawingContext g, double x, double y, double h, Color fore)
    {
        var num = new FormattedText("808", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(G.Ui, FontStyle.Normal, FontWeight.Bold), h * 0.72, G.Brush(fore));
        var word = new FormattedText("Radio", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(G.Ui, FontStyle.Normal, FontWeight.SemiBold), h * 0.52, G.Brush(Orange));
        g.DrawText(num, new Point(x, y + (h - num.Height) / 2));
        double wx = x + num.WidthIncludingTrailingWhitespace + h * 0.18;
        g.DrawText(word, new Point(wx, y + (h - word.Height) / 2 + h * 0.03));

        // three broadcast arcs after the name
        double cx = wx + word.WidthIncludingTrailingWhitespace + h * 0.1, cy = y + h / 2;
        for (int i = 1; i <= 3; i++)
        {
            double r = h * 0.15 * i;
            g.DrawArc(G.Pen(Orange.With(255 - (i - 1) * 60), Math.Max(1.5, h / 11), round: true), cx - r, cy - r, 2 * r, 2 * r, -45, 90);
        }
        return cx + h * 0.15 * 3 - x;
    }
}
