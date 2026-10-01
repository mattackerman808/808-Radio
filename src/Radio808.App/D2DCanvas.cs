using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using D2DDashStyle = Vortice.Direct2D1.DashStyle;
using Color = System.Drawing.Color;
using DashStyle = System.Drawing.Drawing2D.DashStyle;

namespace Radio808.App;

/// <summary>
/// <see cref="IPanelCanvas"/> on a Direct2D device context (the GPU). Used from the panel's render thread only.
/// Brushes, stroke styles, text formats and the waterfall bitmap are made once and reused.
/// </summary>
internal sealed class D2DCanvas : IDisposable
{
    private readonly ID2D1Factory1 _factory;
    private readonly IDWriteFactory _dwrite;
    private ID2D1DeviceContext _ctx = null!;
    private ID2D1SolidColorBrush _brush = null!;
    private readonly ID2D1StrokeStyle1 _dot, _dash;
    private readonly IDWriteTextFormat _title, _small, _banner;
    private ID2D1Bitmap1? _water;
    private int _waterVersion = -1;
    private readonly Stack<Matrix3x2> _transforms = new();

    public D2DCanvas(ID2D1Factory1 factory, IDWriteFactory dwrite)
    {
        _factory = factory;
        _dwrite = dwrite;
        _dot = factory.CreateStrokeStyle(new StrokeStyleProperties1 { DashStyle = D2DDashStyle.Dot, DashCap = CapStyle.Flat });
        _dash = factory.CreateStrokeStyle(new StrokeStyleProperties1 { DashStyle = D2DDashStyle.Dash, DashCap = CapStyle.Flat });
        _title = Format("Segoe UI", FontWeight.SemiBold, 11);
        _small = Format("Segoe UI", FontWeight.Normal, 10);
        _banner = Format("Segoe UI", FontWeight.SemiBold, 13);
        _banner.ParagraphAlignment = ParagraphAlignment.Center;
    }

    private IDWriteTextFormat Format(string family, FontWeight weight, float size)
    {
        var f = _dwrite.CreateTextFormat(family, weight, Vortice.DirectWrite.FontStyle.Normal, size);
        f.WordWrapping = WordWrapping.NoWrap;
        return f;
    }

    /// <summary>Binds to a device context (on device creation or re-creation).</summary>
    public void Attach(ID2D1DeviceContext ctx)
    {
        _ctx = ctx;
        _brush?.Dispose();
        _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _water?.Dispose();
        _water = null;
        _waterVersion = -1;
    }

    private ID2D1SolidColorBrush Brush(Color c)
    {
        _brush.Color = C4(c);
        return _brush;
    }

    private static Color4 C4(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
    private static Rect R(RectangleF r) => new(r.X, r.Y, r.Width, r.Height);

    public IPanelCanvas View(Color background) => new View_(this);

    private sealed class View_ : IPanelCanvas
    {
        private readonly D2DCanvas _d;
        public View_(D2DCanvas d) => _d = d;
        private ID2D1DeviceContext Ctx => _d._ctx;

        public void Fill(RectangleF r, Color c) => Ctx.FillRectangle(R(r), _d.Brush(c));

        public void Stroke(RectangleF r, Color c, float width = 1) => Ctx.DrawRectangle(R(r), _d.Brush(c), width);

        public void Line(float x0, float y0, float x1, float y1, Color c, float width = 1, DashStyle dash = DashStyle.Solid)
        {
            var style = dash switch { DashStyle.Dot => _d._dot, DashStyle.Dash => _d._dash, _ => null };
            Ctx.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), _d.Brush(c), width, style);
        }

        private IDWriteTextFormat FormatOf(TextKind k) => k switch { TextKind.Title => _d._title, TextKind.Banner => _d._banner, _ => _d._small };

        public void Text(string s, RectangleF r, TextKind kind, Color c, StringAlignment align = StringAlignment.Near)
        {
            var f = FormatOf(kind);
            f.TextAlignment = align switch
            {
                StringAlignment.Center => TextAlignment.Center,
                StringAlignment.Far => TextAlignment.Trailing,
                _ => TextAlignment.Leading,
            };
            // titles are drawn unbounded in GDI+; give them room
            var rect = kind == TextKind.Title ? new RectangleF(r.X, r.Y, Math.Max(r.Width, 900), Math.Max(r.Height, 16)) : r;
            Ctx.DrawText(s, f, R(rect), _d.Brush(c), kind == TextKind.Small ? DrawTextOptions.Clip : DrawTextOptions.None);
        }

        public float MeasureText(string s, TextKind kind)
        {
            using var layout = _d._dwrite.CreateTextLayout(s, FormatOf(kind), 2000, 100);
            return layout.Metrics.WidthIncludingTrailingWhitespace;
        }

        private ID2D1PathGeometry Path(PointF[] pts, float? closeAt)
        {
            var geo = _d._factory.CreatePathGeometry();
            using var sink = geo.Open();
            sink.BeginFigure(new Vector2(pts[0].X, pts[0].Y), closeAt is null ? FigureBegin.Hollow : FigureBegin.Filled);
            var v = new Vector2[pts.Length - 1 + (closeAt is null ? 0 : 2)];
            for (int i = 1; i < pts.Length; i++) v[i - 1] = new Vector2(pts[i].X, pts[i].Y);
            if (closeAt is float bottom)
            {
                v[^2] = new Vector2(pts[^1].X, bottom);
                v[^1] = new Vector2(pts[0].X, bottom);
            }
            sink.AddLines(v);
            sink.EndFigure(closeAt is null ? FigureEnd.Open : FigureEnd.Closed);
            sink.Close();
            return geo;
        }

        public void Trace(PointF[] pts, Color c, float width)
        {
            using var geo = Path(pts, null);
            Ctx.DrawGeometry(geo, _d.Brush(c), width);
        }

        public void Area(PointF[] pts, float bottom, RectangleF shade, Color top, Color low)
        {
            using var geo = Path(pts, bottom);
            using var stops = Ctx.CreateGradientStopCollection(new[] { new GradientStop(0, C4(top)), new GradientStop(1, C4(low)) });
            using var b = Ctx.CreateLinearGradientBrush(
                new LinearGradientBrushProperties(new Vector2(shade.X, shade.Top), new Vector2(shade.X, shade.Bottom)), stops);
            var aa = Ctx.AntialiasMode;
            Ctx.AntialiasMode = AntialiasMode.Aliased;
            Ctx.FillGeometry(geo, b);
            Ctx.AntialiasMode = aa;
        }

        public void Waterfall(WaterfallImage img, int srcY, int rows, RectangleF dest)
        {
            if (rows <= 0) return;
            if (_d._water == null)
                _d._water = Ctx.CreateBitmap(new SizeI(img.Width, img.Height), IntPtr.Zero, 0,
                    new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore)));
            if (_d._waterVersion != img.Version)
            {
                _d._water.CopyFromMemory(img.Pixels, (uint)(img.Width * 4));
                _d._waterVersion = img.Version;
            }
            Ctx.DrawBitmap(_d._water, R(dest), 1, BitmapInterpolationMode.NearestNeighbor, new Rect(0, srcY, img.Width, rows));
        }

        public void PushClip(RectangleF r) => Ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.Left, r.Top, r.Right, r.Bottom), AntialiasMode.Aliased);
        public void PopClip() => Ctx.PopAxisAlignedClip();

        public void PushTranslate(float dx, float dy)
        {
            _d._transforms.Push(Ctx.Transform);
            Ctx.Transform = Matrix3x2.CreateTranslation(dx, dy) * Ctx.Transform;
        }

        public void PopTranslate() => Ctx.Transform = _d._transforms.Pop();
    }

    public void Dispose()
    {
        _water?.Dispose();
        _brush?.Dispose();
        _dot.Dispose(); _dash.Dispose();
        _title.Dispose(); _small.Dispose(); _banner.Dispose();
    }
}
