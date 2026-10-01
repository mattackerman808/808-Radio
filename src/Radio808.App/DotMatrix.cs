using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Radio808.App;

/// <summary>
/// Dot-matrix text like a car stereo display. Glyphs are 5 x 8 dots (7 rows of capital height plus a descender
/// row), sampled at startup from Consolas Bold: each dot is on if the rendered glyph covers enough of its cell.
/// </summary>
internal static class DotMatrix
{
    public const int Cols = 5, Rows = 8, CellCols = Cols + 1;   // one blank column between characters

    private static readonly Dictionary<char, bool[,]> Cache = new();
    private static float _em, _capTop, _baseline, _advance;

    /// <summary>Columns of dots for a string (each column is Rows tall), including inter-character gaps.</summary>
    public static List<bool[]> Columns(string text)
    {
        var cols = new List<bool[]>(text.Length * CellCols);
        foreach (char ch in text)
        {
            var g = Glyph(ch);
            for (int x = 0; x < Cols; x++)
            {
                var col = new bool[Rows];
                for (int y = 0; y < Rows; y++) col[y] = g[x, y];
                cols.Add(col);
            }
            cols.Add(new bool[Rows]);
        }
        return cols;
    }

    public static bool[,] Glyph(char c)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(c, out var g)) return g;
            g = c == ' ' ? new bool[Cols, Rows] : Build(c);
            Cache[c] = g;
            return g;
        }
    }

    private static bool[,] Build(char c)
    {
        const int Em = 120;
        using var font = new Font("Consolas", Em, FontStyle.Bold, GraphicsUnit.Pixel);
        if (_em == 0)
        {
            var ff = font.FontFamily;
            float ascent = Em * ff.GetCellAscent(FontStyle.Bold) / ff.GetEmHeight(FontStyle.Bold);
            _baseline = ascent;
            _capTop = InkTop('H', font);
            _advance = Em * 0.55f;   // Consolas advance width
            _em = Em;
        }
        int w = (int)(_advance * 1.4f), h = (int)(Em * 1.5f);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.DrawString(c.ToString(), font, Brushes.White, -Em * 0.1f, 0, StringFormat.GenericTypographic);
        }
        // map: columns across the advance width, rows from cap top to baseline (7) plus one descender row
        var glyph = new bool[Cols, Rows];
        float rowH = (_baseline - _capTop) / 7f;
        float x0 = -Em * 0.1f + _advance * 0.06f, colW = _advance * 0.88f / Cols;
        for (int gx = 0; gx < Cols; gx++)
            for (int gy = 0; gy < Rows; gy++)
            {
                int ax = (int)(x0 + gx * colW), bx = (int)(x0 + (gx + 1) * colW);
                int ay = (int)(_capTop + gy * rowH), by = (int)(_capTop + (gy + 1) * rowH);
                int on = 0, total = 0;
                for (int y = Math.Max(0, ay); y < Math.Min(h, by); y++)
                    for (int x = Math.Max(0, ax); x < Math.Min(w, bx); x++)
                    {
                        total++;
                        if (bmp.GetPixel(x, y).R > 110) on++;
                    }
                glyph[gx, gy] = total > 0 && on > total * 0.34;
            }
        return glyph;
    }

    private static float InkTop(char c, Font font)
    {
        int w = (int)(font.Size), h = (int)(font.Size * 1.5f);
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.DrawString(c.ToString(), font, Brushes.White, 0, 0, StringFormat.GenericTypographic);
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bmp.GetPixel(x, y).R > 128) return y;
        return 0;
    }

    /// <summary>
    /// Draws a window of <paramref name="cells"/> characters' worth of dot columns starting at column
    /// <paramref name="offset"/>, with unlit ("ghost") dots behind, and a soft glow on lit dots.
    /// </summary>
    public static void Draw(Graphics g, List<bool[]> columns, int offset, float x, float y, float pitch, int cells, Color on, Color ghost, bool glow = true)
    {
        int width = cells * CellCols - 1;
        float d = pitch * 0.74f, gd = pitch * 1.7f;
        using var ghostB = new SolidBrush(ghost);
        using var onB = new SolidBrush(on);
        using var glowB = new SolidBrush(Color.FromArgb(38, on));
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int cx = 0; cx < width; cx++)
        {
            int ci = cx + offset;
            var col = ci >= 0 && ci < columns.Count ? columns[ci] : null;
            bool gap = (cx % CellCols) == Cols;   // the blank column between characters stays dark
            for (int ry = 0; ry < Rows; ry++)
            {
                float px = x + cx * pitch, py = y + ry * pitch;
                bool lit = col != null && col[ry];
                if (!lit)
                {
                    if (!gap) g.FillEllipse(ghostB, px - d / 2, py - d / 2, d, d);
                    continue;
                }
                if (glow) g.FillEllipse(glowB, px - gd / 2, py - gd / 2, gd, gd);
                g.FillEllipse(onB, px - d / 2, py - d / 2, d, d);
            }
        }
        g.SmoothingMode = mode;
    }
}
