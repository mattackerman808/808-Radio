using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Radio808.Avalonia.Drawing;

/// <summary>
/// Dot-matrix text like a car stereo display: 5 x 8 dots per character (7 rows of capital height plus a descender
/// row), from a built-in glyph table (the classic 5 x 7 display font), so it looks the same on every platform.
/// </summary>
internal static class DotMatrix
{
    public const int Cols = 5, Rows = 8, CellCols = Cols + 1;   // one blank column between characters

    /// <summary>Columns of dots for a string (each column is Rows tall), including inter-character gaps.</summary>
    public static List<bool[]> Columns(string text)
    {
        var cols = new List<bool[]>(text.Length * CellCols);
        foreach (char ch in text)
        {
            var rows = Glyph(ch);
            for (int x = 0; x < Cols; x++)
            {
                var col = new bool[Rows];
                for (int y = 0; y < Rows; y++) col[y] = (rows[y] & (0x10 >> x)) != 0;
                cols.Add(col);
            }
            cols.Add(new bool[Rows]);
        }
        return cols;
    }

    /// <summary>
    /// Draws a window of <paramref name="cells"/> characters' worth of dot columns starting at column
    /// <paramref name="offset"/>, with unlit ("ghost") dots behind, and a soft glow on lit dots.
    /// </summary>
    public static void Draw(DrawingContext g, List<bool[]> columns, int offset, double x, double y, double pitch, int cells, Color on, Color ghost, bool glow = true)
    {
        int width = cells * CellCols - 1;
        double d = pitch * 0.74, gd = pitch * 1.7;
        var ghostB = G.Brush(ghost);
        var onB = G.Brush(on);
        var glowB = G.Brush(on.With(38));
        for (int cx = 0; cx < width; cx++)
        {
            int ci = cx + offset;
            var col = ci >= 0 && ci < columns.Count ? columns[ci] : null;
            bool gap = (cx % CellCols) == Cols;   // the blank column between characters stays dark
            for (int ry = 0; ry < Rows; ry++)
            {
                double px = x + cx * pitch, py = y + ry * pitch;
                bool lit = col != null && col[ry];
                if (!lit)
                {
                    if (!gap) g.DrawEllipse(ghostB, null, new Point(px, py), d / 2, d / 2);
                    continue;
                }
                if (glow) g.DrawEllipse(glowB, null, new Point(px, py), gd / 2, gd / 2);
                g.DrawEllipse(onB, null, new Point(px, py), d / 2, d / 2);
            }
        }
    }

    private static byte[] Glyph(char c)
    {
        if (c >= 'a' && c <= 'z') c = (char)(c - 32);   // the display is upper case
        c = c switch { '’' or '‘' or '`' => '\'', '“' or '”' => '"', '–' or '—' => '-', '·' => '.', _ => c };
        return Font.TryGetValue(c, out var g) ? g : Font[' '];
    }

    // rows top to bottom, 5 bits each (bit 4 = left column); the 8th row is the descender row
    private static readonly Dictionary<char, byte[]> Font = new()
    {
        [' '] = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 },
        ['!'] = new byte[] { 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100, 0 },
        ['"'] = new byte[] { 0b01010, 0b01010, 0b01010, 0, 0, 0, 0, 0 },
        ['#'] = new byte[] { 0b01010, 0b01010, 0b11111, 0b01010, 0b11111, 0b01010, 0b01010, 0 },
        ['$'] = new byte[] { 0b00100, 0b01111, 0b10100, 0b01110, 0b00101, 0b11110, 0b00100, 0 },
        ['%'] = new byte[] { 0b11000, 0b11001, 0b00010, 0b00100, 0b01000, 0b10011, 0b00011, 0 },
        ['&'] = new byte[] { 0b01100, 0b10010, 0b10100, 0b01000, 0b10101, 0b10010, 0b01101, 0 },
        ['\''] = new byte[] { 0b01100, 0b00100, 0b01000, 0, 0, 0, 0, 0 },
        ['('] = new byte[] { 0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010, 0 },
        [')'] = new byte[] { 0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000, 0 },
        ['*'] = new byte[] { 0, 0b00100, 0b10101, 0b01110, 0b10101, 0b00100, 0, 0 },
        ['+'] = new byte[] { 0, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0, 0 },
        [','] = new byte[] { 0, 0, 0, 0, 0, 0b01100, 0b00100, 0b01000 },
        ['-'] = new byte[] { 0, 0, 0, 0b11111, 0, 0, 0, 0 },
        ['.'] = new byte[] { 0, 0, 0, 0, 0, 0b01100, 0b01100, 0 },
        ['/'] = new byte[] { 0, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0, 0 },
        ['0'] = new byte[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110, 0 },
        ['1'] = new byte[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110, 0 },
        ['2'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111, 0 },
        ['3'] = new byte[] { 0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110, 0 },
        ['4'] = new byte[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010, 0 },
        ['5'] = new byte[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110, 0 },
        ['6'] = new byte[] { 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110, 0 },
        ['7'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0 },
        ['8'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110, 0 },
        ['9'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100, 0 },
        [':'] = new byte[] { 0, 0b01100, 0b01100, 0, 0b01100, 0b01100, 0, 0 },
        [';'] = new byte[] { 0, 0b01100, 0b01100, 0, 0b01100, 0b00100, 0b01000, 0 },
        ['<'] = new byte[] { 0b00010, 0b00100, 0b01000, 0b10000, 0b01000, 0b00100, 0b00010, 0 },
        ['='] = new byte[] { 0, 0, 0b11111, 0, 0b11111, 0, 0, 0 },
        ['>'] = new byte[] { 0b01000, 0b00100, 0b00010, 0b00001, 0b00010, 0b00100, 0b01000, 0 },
        ['?'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0, 0b00100, 0 },
        ['@'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b01101, 0b10101, 0b10101, 0b01110, 0 },
        ['A'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001, 0 },
        ['B'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110, 0 },
        ['C'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110, 0 },
        ['D'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110, 0 },
        ['E'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111, 0 },
        ['F'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000, 0 },
        ['G'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111, 0 },
        ['H'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001, 0 },
        ['I'] = new byte[] { 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110, 0 },
        ['J'] = new byte[] { 0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100, 0 },
        ['K'] = new byte[] { 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001, 0 },
        ['L'] = new byte[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111, 0 },
        ['M'] = new byte[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001, 0 },
        ['N'] = new byte[] { 0b10001, 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0 },
        ['O'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110, 0 },
        ['P'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000, 0 },
        ['Q'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101, 0 },
        ['R'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001, 0 },
        ['S'] = new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110, 0 },
        ['T'] = new byte[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0 },
        ['U'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110, 0 },
        ['V'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100, 0 },
        ['W'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010, 0 },
        ['X'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001, 0 },
        ['Y'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0 },
        ['Z'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111, 0 },
        ['['] = new byte[] { 0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110, 0 },
        ['\\'] = new byte[] { 0, 0b10000, 0b01000, 0b00100, 0b00010, 0b00001, 0, 0 },
        [']'] = new byte[] { 0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110, 0 },
        ['^'] = new byte[] { 0b00100, 0b01010, 0b10001, 0, 0, 0, 0, 0 },
        ['_'] = new byte[] { 0, 0, 0, 0, 0, 0, 0b11111, 0 },
        ['|'] = new byte[] { 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0 },
        ['°'] = new byte[] { 0b01100, 0b10010, 0b01100, 0, 0, 0, 0, 0 },
    };
}
