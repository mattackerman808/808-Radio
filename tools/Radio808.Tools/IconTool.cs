using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace Radio808.Tools;

/// <summary>Renders the 808 Radio app icon (multi-size .ico with PNG entries) and a preview PNG.</summary>
internal static class IconTool
{
    private static readonly Color Orange = Color.FromArgb(0xF7, 0x94, 0x1D);

    public static int Run(string outIco)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using var bmp = Render(s);
            if (s >= 256)
            {
                // 256 px as PNG (the format Windows expects at that size)
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
                bmp.Save(Path.ChangeExtension(outIco, ".png"), ImageFormat.Png);
            }
            else pngs.Add(Dib(bmp));   // smaller sizes as classic 32-bit DIBs, which every reader handles
        }
        using var f = new BinaryWriter(File.Create(outIco));
        f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            f.Write((byte)0); f.Write((byte)0); f.Write((short)1); f.Write((short)32);
            f.Write(pngs[i].Length); f.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) f.Write(p);
        Console.WriteLine($"wrote {outIco} ({sizes.Length} sizes)");
        return 0;
    }

    /// <summary>An icon-format DIB: BITMAPINFOHEADER (double height), BGRA rows bottom-up, then an empty AND mask.</summary>
    private static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
            }
        int maskRow = ((w + 31) / 32) * 4;
        bw.Write(new byte[maskRow * h]);   // alpha carries transparency; the AND mask is unused
        return ms.ToArray();
    }

    private static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        float r = s * 0.2f, inset = s >= 32 ? s * 0.03f : 0;
        var tile = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
        using (var path = Rounded(tile, r))
        {
            using (var b = new LinearGradientBrush(tile, Color.FromArgb(0x30, 0x34, 0x3B), Color.FromArgb(0x0B, 0x0C, 0x0F), 90f)) g.FillPath(b, path);
            if (s >= 32) using (var pen = new Pen(Color.FromArgb(0x55, 0x5B, 0x64), Math.Max(1, s / 64f))) g.DrawPath(pen, path);
        }
        if (s <= 24)
        {
            // just the broadcast arcs: a dot and three arcs, radiating right
            float cx = s * 0.32f, cy = s * 0.5f;
            using (var dot = new SolidBrush(Orange)) g.FillEllipse(dot, cx - s * 0.09f, cy - s * 0.09f, s * 0.18f, s * 0.18f);
            for (int i = 1; i <= 3; i++)
            {
                float rr = s * 0.13f * i + s * 0.04f;
                using var pen = new Pen(Color.FromArgb(255 - (i - 1) * 50, Orange), Math.Max(1.4f, s * 0.085f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, cx - rr, cy - rr, 2 * rr, 2 * rr, -50, 100);
            }
            return bmp;
        }
        // "808" with broadcast arcs radiating to its right (like the brand mark), centered on the tile
        using var font = new Font("Segoe UI", s * 0.33f, FontStyle.Bold, GraphicsUnit.Pixel);
        var tf = StringFormat.GenericTypographic;
        var size = g.MeasureString("808", font, PointF.Empty, tf);
        float arcsW = s * 0.2f, gap = s * 0.03f;
        float x = (s - (size.Width + gap + arcsW)) / 2, mid = s / 2f;
        // draw at a point (a layout rectangle would need to be taller than the line or GDI+ drops the text)
        using (var tb = new SolidBrush(Color.FromArgb(0xEC, 0xEF, 0xF3)))
            g.DrawString("808", font, tb, x, mid - font.GetHeight(g) * 0.55f, tf);
        float ax = x + size.Width + gap, ay = mid;
        for (int i = 1; i <= 3; i++)
        {
            float rr = s * 0.062f * i;
            using var pen = new Pen(Color.FromArgb(255 - (i - 1) * 55, Orange), Math.Max(1.5f, s * 0.035f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, ax - rr, ay - rr, 2 * rr, 2 * rr, -45, 90);
        }
        return bmp;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
