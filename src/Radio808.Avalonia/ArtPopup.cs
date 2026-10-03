using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Radio808.Avalonia.Drawing;

namespace Radio808.Avalonia;

/// <summary>
/// The album art (or station logo), popped out of the display's art square: a borderless window that grows from the
/// square to a bigger image with a caption, and goes away on a click, Esc, or when the radio gets the focus back.
/// </summary>
internal sealed class ArtPopup : Window
{
    private const double GrowSeconds = 0.16;
    private readonly Bitmap _image;
    private readonly string _line1, _line2;
    private readonly PixelRect _from, _to;
    private readonly double _scaling;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(10) };
    private readonly Color _accent;
    private readonly Face _face;

    /// <param name="from">The art square on screen (pixels), where the popup grows from.</param>
    /// <param name="screen">The working area of that screen (pixels).</param>
    public ArtPopup(Bitmap image, PixelRect from, PixelRect screen, double scaling, string line1, string line2, Color accent)
    {
        _image = image; _line1 = line1; _line2 = line2; _accent = accent; _from = from; _scaling = scaling;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // about 2.6x the square, as big as the screen allows, centered on the square (moved onto the screen if needed)
        int side = (int)Math.Min(from.Width * 2.6, screen.Height * 0.8);
        side = Math.Max(side, Math.Min((int)(320 * scaling), screen.Height * 4 / 5));
        int caption = string.IsNullOrEmpty(line1) && string.IsNullOrEmpty(line2) ? 0 : Math.Max((int)(44 * scaling), side / 7);
        int x = from.X + from.Width / 2 - side / 2, y = from.Y + from.Height / 2 - (side + caption) / 2;
        x = Math.Clamp(x, screen.X + 8, Math.Max(screen.X + 8, screen.Right - side - 8));
        y = Math.Clamp(y, screen.Y + 8, Math.Max(screen.Y + 8, screen.Bottom - side - caption - 8));
        _to = new PixelRect(x, y, side, side + caption);
        Apply(from);

        Content = _face = new Face(this);
        _anim.Tick += (_, _) => Step();
        Opened += (_, _) => { _clock.Restart(); _anim.Start(); };
        // (closing raises Deactivated itself, so the dismissals go through one guarded, deferred close)
        Deactivated += (_, _) => Dismiss();
        Closing += (_, _) => { _closing = true; _anim.Stop(); };
        _face.PointerPressed += (_, _) => Dismiss();
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter or Key.Space) Dismiss(); };
    }

    private bool _closing;

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _anim.Stop();
        Dispatcher.UIThread.Post(() => { try { Close(); } catch (Exception ex) { Radio808.Shared.AppLog.Write(ex); } });
    }

    private void Apply(PixelRect r)
    {
        Position = r.Position;
        Width = r.Width / _scaling;
        Height = r.Height / _scaling;
    }

    private void Step()
    {
        double t = Math.Clamp(_clock.Elapsed.TotalSeconds / GrowSeconds, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3);   // ease-out
        int L(int a, int b) => (int)Math.Round(a + (b - a) * e);
        Apply(new PixelRect(L(_from.X, _to.X), L(_from.Y, _to.Y), L(_from.Width, _to.Width), L(_from.Height, _to.Height)));
        if (t >= 1) _anim.Stop();
        _face.InvalidateVisual();
    }

    /// <summary>The full-size layout, scaled to the current (growing) size, so it looks like one picture zooming out.</summary>
    private sealed class Face : Control
    {
        private readonly ArtPopup _p;
        public Face(ArtPopup p) => _p = p;

        public override void Render(DrawingContext g)
        {
            double toW = _p._to.Width / _p._scaling, toH = _p._to.Height / _p._scaling;
            double k = Bounds.Width / toW;
            using var _ = g.PushTransform(Matrix.CreateScale(k, Bounds.Height / toH));
            double side = toW, caption = toH - toW, radius = Math.Max(6, side / 40);
            var all = new Rect(0, 0, side, side + caption);
            g.FillRounded(G.Brush(Color.FromRgb(0x0A, 0x0C, 0x0E)), all, radius);
            double s = Math.Min(side / _p._image.PixelSize.Width, side / _p._image.PixelSize.Height);
            double w = _p._image.PixelSize.Width * s, h = _p._image.PixelSize.Height * s;
            g.DrawImage(_p._image, new Rect((side - w) / 2, (side - h) / 2, w, h));
            if (caption > 0)
            {
                g.DrawLine(G.Pen(_p._accent, 2), 0, side + 1, side, side + 1);
                double pad = caption * 0.22;
                g.Label(_p._line1, caption * 0.3, Color.FromRgb(235, 240, 244), new Rect(pad, side + pad * 0.6, side - 2 * pad, caption * 0.42), G.Align.Near, bold: true);
                g.Label(_p._line2, caption * 0.24, Color.FromRgb(150, 160, 170), new Rect(pad, side + caption * 0.52, side - 2 * pad, caption * 0.36), G.Align.Near);
            }
            g.DrawRounded(G.Pen(Color.FromArgb(70, 255, 255, 255), 1 / k), new Rect(0.5, 0.5, side - 1, side + caption - 1), radius);
        }
    }
}
