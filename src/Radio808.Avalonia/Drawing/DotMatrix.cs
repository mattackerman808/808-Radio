using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Radio808.Shared.Display;

namespace Radio808.Avalonia.Drawing;

/// <summary>
/// Dot-matrix text like a car stereo display: 5 x 8 dots per character (7 rows of capital height plus a descender
/// row), from a built-in glyph table (the classic 5 x 7 display font), so it looks the same on every platform.
/// </summary>
internal static class DotMatrix
{
    public const int Cols = 5, Rows = 8, CellCols = Cols + 1;   // one blank column between characters

    /// <summary>Columns of dots for a string (each column is Rows tall), including inter-character gaps.</summary>
    public static List<bool[]> Columns(string text) => DotFont.Columns(text);

    /// <summary>
    /// Draws a window of <paramref name="cells"/> characters' worth of dot columns starting at column
    /// <paramref name="offset"/>, with unlit ("ghost") dots behind, and a soft glow on lit dots. Characters can take
    /// their own colors from <paramref name="cellColors"/> (indexed by character, before the offset). One matrix is
    /// one call: on a coarse pixel grid, dots from separate calls at different origins would not line up.
    /// </summary>
    public static void Draw(DrawingContext g, List<bool[]> columns, int offset, double x, double y, double pitch, int cells, Color on, Color ghost, bool glow = true, IReadOnlyList<Color>? cellColors = null)
    {
        int width = cells * CellCols - 1;
        double d = pitch * 0.74, gd = pitch * 1.7;
        var ghostB = G.Brush(ghost);
        if (pitch * _device < 3) { DrawSnapped(g, columns, offset, x, y, pitch, width, d, on, ghostB, cellColors); return; }
        for (int cx = 0; cx < width; cx++)
        {
            int ci = cx + offset;
            var col = ci >= 0 && ci < columns.Count ? columns[ci] : null;
            bool gap = (cx % CellCols) == Cols;   // the blank column between characters stays dark
            var c = ColorAt(cellColors, ci, on);
            for (int ry = 0; ry < Rows; ry++)
            {
                double px = x + cx * pitch, py = y + ry * pitch;
                bool lit = col != null && col[ry];
                if (!lit)
                {
                    if (!gap) g.DrawEllipse(ghostB, null, new Point(px, py), d / 2, d / 2);
                    continue;
                }
                if (glow) g.DrawEllipse(G.Brush(c.With(38)), null, new Point(px, py), gd / 2, gd / 2);
                g.DrawEllipse(G.Brush(c), null, new Point(px, py), d / 2, d / 2);
            }
        }
    }

    /// <summary>The lit color of column <paramref name="ci"/>: its character's entry in <paramref name="cellColors"/>, else <paramref name="on"/>.</summary>
    private static Color ColorAt(IReadOnlyList<Color>? cellColors, int ci, Color on)
        => cellColors != null && ci >= 0 && ci / CellCols < cellColors.Count ? cellColors[ci / CellCols] : on;

    // ---- the device pixel grid, for dots only a pixel or two across
    private static double _device = 1, _deviceX, _deviceY;

    /// <summary>
    /// Where design coordinates land on device pixels for the visual about to draw: device = design * scale + offset.
    /// The faceplate sets it at the top of each Render (its design scale times the window's render scaling).
    /// </summary>
    public static void SetDevice(double scale, double offsetX, double offsetY) { _device = scale; _deviceX = offsetX; _deviceY = offsetY; }

    /// <summary>
    /// Tiny dots (under three device pixels of pitch, e.g. the HD programs matrix on a 1x monitor) are drawn as
    /// pixel-aligned squares on a whole-pixel pitch: antialiased circles that size smear into a blur.
    /// </summary>
    private static void DrawSnapped(DrawingContext g, List<bool[]> columns, int offset, double x, double y, double pitch, int width, double d, Color on, IBrush ghostB, IReadOnlyList<Color>? cellColors)
    {
        double ds = _device;
        int pp = Math.Max(2, (int)Math.Round(pitch * ds));                      // pixels per dot pitch
        int dot = Math.Max(1, Math.Min(pp - 1, (int)Math.Round(d * ds)));      // dot size, leaving a dark pixel between
        double ox = Math.Round(x * ds + _deviceX - dot / 2.0) - _deviceX, oy = Math.Round(y * ds + _deviceY - dot / 2.0) - _deviceY;
        double size = dot / ds;
        for (int cx = 0; cx < width; cx++)
        {
            int ci = cx + offset;
            var col = ci >= 0 && ci < columns.Count ? columns[ci] : null;
            bool gap = (cx % CellCols) == Cols;
            double px = (ox + cx * pp) / ds;
            var onB = G.Brush(ColorAt(cellColors, ci, on));
            for (int ry = 0; ry < Rows; ry++)
            {
                bool lit = col != null && col[ry];
                if (!lit && gap) continue;
                g.FillRectangle(lit ? onB : ghostB, new Rect(px, (oy + ry * pp) / ds, size, size));
            }
        }
    }
}
