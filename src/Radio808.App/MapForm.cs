using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Radio808.Core.Hd;
using Radio808.Shared.Map;

namespace Radio808.App;

/// <summary>The weather map: the station's radar image over a street map of the same box, with a time stamp.</summary>
internal sealed class MapForm : Form
{
    private readonly MapView _view;

    public MapForm(string title, Image overlay, MapBounds bounds, DateTime time, Icon? icon)
    {
        Text = title;
        if (icon != null) Icon = icon;
        double aspect = BaseMap.Aspect(bounds);
        int w = (int)Math.Round(820 * DeviceDpi / 96.0);
        ClientSize = new Size(w, (int)Math.Round(w * aspect));
        MinimumSize = new Size(300, 300);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(0xf5, 0xf3, 0xee);
        Controls.Add(_view = new MapView(overlay, bounds, time) { Dock = DockStyle.Fill });
        FormClosed += (_, _) => _view.Dispose();
    }

    /// <summary>A newer image for the same window.</summary>
    public void Update(Image overlay, DateTime time) => _view.Update(overlay, time);

    private sealed class MapView : Control
    {
        private Image _overlay;
        private readonly MapBounds _bounds;
        private DateTime _time;
        private Bitmap? _base;
        private string _status = "fetching the map…";
        private int _baseW, _baseH;
        private readonly System.Windows.Forms.Timer _resize = new() { Interval = 250 };
        private static readonly Font Stamp = new("Segoe UI Semibold", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        private static readonly Font Credit = new("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        private static readonly Font StatusFont = new("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);

        public MapView(Image overlay, MapBounds bounds, DateTime time)
        {
            _overlay = overlay; _bounds = bounds; _time = time;
            DoubleBuffered = true;
            ResizeRedraw = true;
            _resize.Tick += (_, _) => { _resize.Stop(); Refetch(); };
        }

        public void Update(Image overlay, DateTime time) { _overlay.Dispose(); _overlay = overlay; _time = time; Invalidate(); }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Refetch(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); _resize.Stop(); _resize.Start(); }

        /// <summary>The base map is rendered at the view's pixel size, once per size (debounced while resizing).</summary>
        private void Refetch()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0 || (w == _baseW && h == _baseH) || w > 4096 || h > 4096) return;
            _baseW = w; _baseH = h;
            _ = FetchAsync(w, h);
        }

        private async Task FetchAsync(int w, int h)
        {
            try
            {
                var bmp = await MapImage.RenderAsync(_bounds, w, h);
                if (w != _baseW || h != _baseH) { bmp?.Dispose(); return; }   // resized meanwhile
                if (bmp == null) _status = "no map (offline, nothing cached yet); the radar image alone";
                else { _base?.Dispose(); _base = bmp; _status = ""; }
            }
            catch (Exception ex) { _status = "map failed: " + ex.Message; }
            if (IsHandleCreated) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (_base != null) g.DrawImage(_base, r);
            else
            {
                g.Clear(Color.FromArgb(0x10, 0x14, 0x18));
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var b = new SolidBrush(Color.FromArgb(0xC0, 0xC8, 0xD0));
                g.DrawString(_status, StatusFont, b, r, fmt);
            }
            // the radar image: a plain latitude/longitude rectangle placed by its corners on the Mercator map (over a
            // one-degree box the remaining curvature is well under a pixel)
            g.DrawImage(_overlay, r);
            string when = _time == DateTime.MinValue ? "" : $"radar received {_time:H:mm}";
            if (when.Length > 0)
            {
                float w = g.MeasureString(when, Stamp).Width + 14;
                using var bg = new SolidBrush(Color.FromArgb(200, 0x10, 0x14, 0x18));
                FillRounded(g, bg, new RectangleF(10, 10, w, 24), 5);
                g.DrawString(when, Stamp, Brushes.WhiteSmoke, new PointF(17, 14));
            }
            float aw = g.MeasureString(BaseMap.Attribution, Credit).Width + 10;
            using var cbg = new SolidBrush(Color.FromArgb(190, 0xF7, 0xF5, 0xF0));
            using var ctext = new SolidBrush(Color.FromArgb(0x40, 0x46, 0x4E));
            FillRounded(g, cbg, new RectangleF(r.Width - aw - 6, r.Height - 20, aw, 16), 3);
            g.DrawString(BaseMap.Attribution, Credit, ctext, new PointF(r.Width - aw - 2, r.Height - 19));
        }

        private static void FillRounded(Graphics g, Brush b, RectangleF r, float radius)
        {
            using var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(b, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _resize.Dispose(); _base?.Dispose(); _overlay.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

/// <summary>The shared base map (<see cref="BaseMap"/>) as a GDI+ bitmap, rendered once per box and size and cached as a PNG.</summary>
internal static class MapImage
{
    /// <summary>Null if the tiles couldn't be fetched (offline) and nothing is cached.</summary>
    public static async Task<Bitmap?> RenderAsync(MapBounds b, int width, int height)
    {
        string file = BaseMap.CacheFile(b, width, height);
        if (File.Exists(file))
        {
            try { using var cached = new Bitmap(file); return new Bitmap(cached); }   // a copy, so the file isn't held open
            catch { }
        }
        var tiles = await BaseMap.FetchAsync(b, width);
        if (tiles == null) return null;
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var canvas = new GdiMapCanvas(g))
            BaseMap.Draw(canvas, b, width, height, tiles);
        try { bmp.Save(file, ImageFormat.Png); } catch { }
        return bmp;
    }
}

/// <summary>The map canvas over GDI+.</summary>
internal sealed class GdiMapCanvas : IMapCanvas, IDisposable
{
    private readonly Graphics _g;
    private readonly Dictionary<MapColor, SolidBrush> _brushes = new();
    private readonly Dictionary<double, Font> _fonts = new();
    private static readonly StringFormat Typographic = StringFormat.GenericTypographic;

    public GdiMapCanvas(Graphics g)
    {
        _g = g;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private SolidBrush Brush(MapColor c)
    {
        if (!_brushes.TryGetValue(c, out var b)) _brushes[c] = b = new SolidBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        return b;
    }

    private Font Font(double px)
    {
        double key = Math.Round(px, 1);
        if (!_fonts.TryGetValue(key, out var f)) _fonts[key] = f = new Font("Segoe UI", (float)key, FontStyle.Bold, GraphicsUnit.Pixel);
        return f;
    }

    private static PointF[] Pts(MapPt[] p)
    {
        var r = new PointF[p.Length];
        for (int i = 0; i < p.Length; i++) r[i] = new PointF((float)p[i].X, (float)p[i].Y);
        return r;
    }

    public void FillRect(double x, double y, double w, double h, MapColor c) => _g.FillRectangle(Brush(c), (float)x, (float)y, (float)w, (float)h);

    public void FillPolygon(MapPt[][] rings, MapColor c)
    {
        using var path = new GraphicsPath(FillMode.Alternate);   // even-odd: inner rings are holes
        foreach (var ring in rings) if (ring.Length >= 3) path.AddPolygon(Pts(ring));
        if (path.PointCount > 0) _g.FillPath(Brush(c), path);
    }

    public void StrokeLines(MapPt[][] lines, MapColor c, double width, bool dashed)
    {
        using var path = new GraphicsPath();
        foreach (var line in lines)
            if (line.Length >= 2) { path.StartFigure(); path.AddLines(Pts(line)); }
        if (path.PointCount == 0) return;
        using var pen = new Pen(Brush(c), (float)Math.Max(width, 0.1)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (dashed) pen.DashPattern = new[] { 2f, 2f };   // in units of the width, like the Mac
        _g.DrawPath(pen, path);
    }

    public (double Width, double Height) MeasureLabel(string s, double px)
    {
        var size = _g.MeasureString(s, Font(px), PointF.Empty, Typographic);
        return (size.Width, size.Height);
    }

    public void DrawLabel(string s, double px, double x, double y, MapColor c, MapColor halo)
    {
        var f = Font(px);
        foreach (var (dx, dy) in new[] { (-1.2f, 0f), (1.2f, 0f), (0f, -1.2f), (0f, 1.2f), (-0.9f, -0.9f), (0.9f, 0.9f), (-0.9f, 0.9f), (0.9f, -0.9f) })
            _g.DrawString(s, f, Brush(halo), new PointF((float)x + dx, (float)y + dy), Typographic);
        _g.DrawString(s, f, Brush(c), new PointF((float)x, (float)y), Typographic);
    }

    public void Dispose()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        foreach (var f in _fonts.Values) f.Dispose();
    }
}
