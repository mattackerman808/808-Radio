using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Radio808.App;

/// <summary>
/// The album art (or station logo), popped out of the display's art square: a borderless window that grows from the
/// square to a bigger image with a caption, and goes away on a click, Esc, or when the radio gets the focus back.
/// </summary>
internal sealed class ArtPopup : Form
{
    private const double GrowSeconds = 0.16;
    private readonly Image _image;
    private readonly string _line1, _line2;
    private readonly Rectangle _from, _to;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _anim = new() { Interval = 10 };
    private readonly Color _accent;

    /// <param name="image">Shown at its full size within the popup (the popup takes its own copy).</param>
    /// <param name="from">The art square on screen, where the popup grows from.</param>
    public ArtPopup(Image image, Rectangle from, string line1, string line2, Color accent)
    {
        _image = new Bitmap(image);
        _line1 = line1;
        _line2 = line2;
        _accent = accent;
        _from = from;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0x0A, 0x0C, 0x0E);
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        // about 2.6x the square, as big as the screen allows, centered on the square (moved onto the screen if needed)
        var screen = Screen.FromRectangle(from).WorkingArea;
        int side = (int)Math.Min(from.Width * 2.6, screen.Height * 0.8);
        side = Math.Max(side, Math.Min(320, screen.Height * 4 / 5));
        int caption = string.IsNullOrEmpty(line1) && string.IsNullOrEmpty(line2) ? 0 : Math.Max(44, side / 7);
        var to = new Rectangle(from.X + from.Width / 2 - side / 2, from.Y + from.Height / 2 - (side + caption) / 2, side, side + caption);
        to.X = Math.Clamp(to.X, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - to.Width - 8));
        to.Y = Math.Clamp(to.Y, screen.Top + 8, Math.Max(screen.Top + 8, screen.Bottom - to.Height - 8));
        _to = to;
        Bounds = from;

        _anim.Tick += (_, _) => Step();
        Shown += (_, _) => { _clock.Restart(); _anim.Start(); };
        Click += (_, _) => Close();
        Deactivate += (_, _) => Close();
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Escape or Keys.Enter or Keys.Space) Close(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
            return cp;
        }
    }

    private void Step()
    {
        double t = Math.Clamp(_clock.Elapsed.TotalSeconds / GrowSeconds, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3);   // ease-out
        int L(int a, int b) => (int)Math.Round(a + (b - a) * e);
        Bounds = Rectangle.FromLTRB(L(_from.Left, _to.Left), L(_from.Top, _to.Top), L(_from.Right, _to.Right), L(_from.Bottom, _to.Bottom));
        if (t >= 1) _anim.Stop();
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // rounded corners
        float r = Math.Max(6, Width / 40f);
        using var path = Rounded(new RectangleF(0, 0, Width, Height), r);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        // the full-size layout, scaled to the current (growing) size, so it looks like one picture zooming out
        float k = (float)Width / _to.Width;
        g.ScaleTransform(k, (float)Height / _to.Height);
        int side = _to.Width, caption = _to.Height - _to.Width;
        float s = Math.Min((float)side / _image.Width, (float)side / _image.Height);
        float w = _image.Width * s, h = _image.Height * s;
        g.DrawImage(_image, (side - w) / 2, (side - h) / 2, w, h);
        if (caption > 0)
        {
            var cr = new RectangleF(0, side, side, caption);
            using (var b = new SolidBrush(Color.FromArgb(0x0A, 0x0C, 0x0E))) g.FillRectangle(b, cr);
            using (var accent = new Pen(_accent, 2)) g.DrawLine(accent, 0, side + 1, side, side + 1);
            float pad = caption * 0.22f;
            using var f1 = new Font("Segoe UI Semibold", caption * 0.3f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var f2 = new Font("Segoe UI", caption * 0.24f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var b1 = new SolidBrush(Color.FromArgb(235, 240, 244));
            using var b2 = new SolidBrush(Color.FromArgb(150, 160, 170));
            g.DrawString(_line1, f1, b1, new RectangleF(pad, side + pad * 0.6f, side - 2 * pad, caption * 0.42f), fmt);
            g.DrawString(_line2, f2, b2, new RectangleF(pad, side + caption * 0.52f, side - 2 * pad, caption * 0.36f), fmt);
        }
        using var edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1 / k);
        using var p = Rounded(new RectangleF(0.5f, 0.5f, _to.Width - 1, _to.Height - 1), Math.Max(6, _to.Width / 40f));
        g.DrawPath(edge, p);
    }

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = 2 * rad;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _anim.Dispose(); _image.Dispose(); Region?.Dispose(); }
        base.Dispose(disposing);
    }
}
