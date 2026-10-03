using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Radio808.Avalonia.Drawing;
using Radio808.Core.Hd;
using Radio808.Shared.Map;

namespace Radio808.Avalonia.Map;

/// <summary>The weather map: the station's radar image over a street map of the same box, with a time stamp.</summary>
internal sealed class MapWindow : Window
{
    private readonly MapView _view;

    public MapWindow(string title, Bitmap overlay, MapBounds bounds, DateTime time)
    {
        Title = title;
        double aspect = BaseMap.Aspect(bounds);
        Width = 820; Height = Math.Round(820 * aspect) + 0;
        MinWidth = 300; MinHeight = 300;
        Background = new SolidColorBrush(Color.FromRgb(0xf5, 0xf3, 0xee));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = _view = new MapView(overlay, bounds, time);
    }

    /// <summary>Development: --mapsnap &lt;radar.png&gt; N W S E &lt;out.png&gt;: the window, rendered to a file after the map loads.</summary>
    public async System.Threading.Tasks.Task SnapshotTo(string path, double seconds)
    {
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(seconds));
        double scale = RenderScaling;
        var size = new PixelSize((int)(_view.Bounds.Width * scale), (int)(_view.Bounds.Height * scale));
        using var rtb = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        rtb.Render(_view);
        rtb.Save(path);
        Console.WriteLine($"map snapshot: {path} {size.Width}x{size.Height}");
    }

    /// <summary>A newer image for the same window.</summary>
    public void Update(Bitmap overlay, DateTime time) => _view.Update(overlay, time);

    private sealed class MapView : Control
    {
        private Bitmap _overlay;
        private readonly MapBounds _bounds;
        private DateTime _time;
        private Bitmap? _base;
        private string _status = "fetching the map…";
        private int _baseW, _baseH;

        public MapView(Bitmap overlay, MapBounds bounds, DateTime time)
        {
            _overlay = overlay; _bounds = bounds; _time = time;
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        }

        public void Update(Bitmap overlay, DateTime time) { _overlay = overlay; _time = time; InvalidateVisual(); }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // the base map is rendered at the view's pixel size (2x for a Retina display), once per size
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            int w = (int)Math.Round(finalSize.Width * scale), h = (int)Math.Round(finalSize.Height * scale);
            if (w > 0 && h > 0 && (w != _baseW || h != _baseH) && w <= 4096 && h <= 4096)
            {
                _baseW = w; _baseH = h;
                _ = FetchAsync(w, h);
            }
            return base.ArrangeOverride(finalSize);
        }

        private async System.Threading.Tasks.Task FetchAsync(int w, int h)
        {
            try
            {
                var bmp = await MapImage.RenderAsync(_bounds, w, h);
                if (w != _baseW || h != _baseH) return;   // resized meanwhile
                if (bmp == null) _status = "no map (offline, nothing cached yet); the radar image alone";
                else { _base = bmp; _status = ""; }
            }
            catch (Exception ex) { _status = "map failed: " + ex.Message; }
            InvalidateVisual();
        }

        public override void Render(DrawingContext g)
        {
            var r = new Rect(0, 0, Bounds.Width, Bounds.Height);
            if (_base != null) g.DrawImage(_base, r);
            else
            {
                g.FillRectangle(G.Brush(Color.FromRgb(0x10, 0x14, 0x18)), r);
                g.Label(_status, 13, Color.FromRgb(0xC0, 0xC8, 0xD0), new Rect(0, r.Height / 2 - 10, r.Width, 20), G.Align.Center);
            }
            // the radar image: a plain latitude/longitude rectangle (600 x 600 over equal spans of each), placed by
            // its corners on the Mercator map; over a one-degree box the remaining curvature is well under a pixel
            g.DrawImage(_overlay, r);
            // time stamp and attribution
            string when = _time == DateTime.MinValue ? "" : $"radar received {_time:H:mm}";
            if (when.Length > 0)
            {
                double w = G.TextWidth(when, 12, bold: true) + 14;
                g.FillRounded(G.Brush(Color.FromArgb(200, 0x10, 0x14, 0x18)), new Rect(10, 10, w, 24), 5);
                g.Label(when, 12, Color.FromRgb(0xF0, 0xF4, 0xF8), new Rect(17, 10, w, 24), G.Align.Near, bold: true);
            }
            double aw = G.TextWidth(BaseMap.Attribution, 10) + 10;
            g.FillRounded(G.Brush(Color.FromArgb(190, 0xF7, 0xF5, 0xF0)), new Rect(r.Width - aw - 6, r.Height - 20, aw, 16), 3);
            g.Label(BaseMap.Attribution, 10, Color.FromRgb(0x40, 0x46, 0x4E), new Rect(r.Width - aw - 1, r.Height - 20, aw, 16), G.Align.Near);
        }
    }
}
