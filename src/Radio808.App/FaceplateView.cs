using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Radio808.Core.Dsp;
using Radio808.Core.Hd;
using Radio808.Core.Radio;
using Radio808.Shared;

namespace Radio808.App;

/// <summary>
/// The radio as a single-DIN car stereo: glossy faceplate, illuminated keys, a volume knob with a light ring, and a
/// dot-matrix display. Custom painted on a 1000 x 300 design canvas that scales to the window; everything clickable
/// registers a hit rectangle while painting.
/// </summary>
internal sealed class FaceplateView : Control
{
    private const float W = 1000, ClosedH = 300, OpenH = 570;
    private const int MainCells = 13;
    private const int Line2Cells = 26;   // the small dot-matrix line, full width (to the art square)
    private float H => _open || _anim > 0 ? OpenH : ClosedH;

    // flip-down faceplate: _anim 0 = closed, 1 = fully open
    private bool _open;
    private float _anim;
    private readonly Timer _animTimer = new() { Interval = 15 };
    private readonly Timer _fastPaint = new() { Interval = 16 };   // smooth repaints while the spectrum slides
    private Point? _hoverHiddenAt;
    private readonly NerdPanel _nerd;

    /// <summary>Raised before opening (true) and after closing (false): the window should change height.</summary>
    public event Action<bool>? OpenLayout;
    public bool IsOpen => _open;
    /// <summary>Current design canvas height (the window keeps W:H).</summary>
    public float DesignHeight => H;
    /// <summary>The window's shape for the current state, in design coordinates.</summary>
    public RectangleF CurrentOutline => H > ClosedH ? new RectangleF(12, 14, 976, 546) : Outline;

    public static readonly (string Name, Color Color)[] Illuminations =
    {
        ("Cyan", Color.FromArgb(0x2B, 0xE4, 0xF2)), ("Amber", Color.FromArgb(0xFF, 0xA8, 0x26)),
        ("Green", Color.FromArgb(0x5C, 0xFF, 0x86)), ("Red", Color.FromArgb(0xFF, 0x45, 0x45)),
        ("Blue", Color.FromArgb(0x4A, 0x8C, 0xFF)), ("White", Color.FromArgb(0xE6, 0xF2, 0xFF)),
    };

    private static readonly Color Body1 = Color.FromArgb(0x30, 0x34, 0x3B), Body2 = Color.FromArgb(0x0D, 0x0F, 0x12);
    private static readonly Color Face1 = Color.FromArgb(0x1A, 0x1D, 0x22), Face2 = Color.FromArgb(0x08, 0x09, 0x0B);
    private static readonly Color Key1 = Color.FromArgb(0x2A, 0x2E, 0x35), Key2 = Color.FromArgb(0x0E, 0x10, 0x13);
    private static readonly Color Silver = Color.FromArgb(0xB8, 0xBE, 0xC6), Grey = Color.FromArgb(0x6E, 0x76, 0x80);
    private static readonly Color Alert = Color.FromArgb(0xFF, 0x4A, 0x4A);

    private readonly RadioController _c;
    private float _scale = 1, _ox, _oy;

    private sealed record Hit(RectangleF R, string Id, Action? Click, Action? Hold = null);
    private readonly List<Hit> _hits = new();
    private string? _hover, _pressed;
    private bool _holdFired, _knobDrag, _knobMoved;
    private PointF _knobStart;
    private float _knobStartVol;
    private readonly Timer _hold = new() { Interval = 650 };
    private Action? _holdAction;

    // display state
    private string _mainText = "";
    private List<bool[]> _mainCols = new();
    private int _scrollChars, _scrollTicks;
    // the small line's info, scrolled like the big line when it doesn't fit
    private string? _subText;
    private List<bool[]> _subCols = new();
    private int _subScroll, _subTicks, _subCells = 10;
    // the HD programs mini matrix, scrolled the same way
    private const int HdCells = 13;
    private string? _hdKey;
    private int _hdPage, _hdPageCount, _hdPageLen, _hdTicks, _hdScroll;
    private string? _flash;
    private DateTime _flashUntil;

    private byte[]? _artKey;
    private Image? _art;

    private const int Bars = 16;
    private readonly float[] _bars = new float[Bars];
    private bool _specValid;

    public event Action<string>? MapRequested;
    public event Action<Point>? MenuRequested;
    /// <summary>The user pressed on bare faceplate: the window should start moving.</summary>
    public event Action? DragRequested;
    public event Action? MinimizeRequested, CloseRequested;
    /// <summary>Asked for each mouse position: true if it's on the window's resize border.</summary>
    public Func<Point, bool>? IsResizeBorder;

    /// <summary>The faceplate's outline in design coordinates (the window's shape).</summary>
    public static readonly RectangleF Outline = new(12, 14, 976, 272);
    public const float OutlineRadius = 26;

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;
        if (m.Msg == WM_NCHITTEST && IsResizeBorder != null)
        {
            var screen = new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF));
            if (IsResizeBorder(PointToClient(screen))) { m.Result = HTTRANSPARENT; return; }   // let the form resize
        }
        if (m.Msg == 0x000F)   // WM_PAINT: time the whole frame, including the double buffer's copy to the screen
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            base.WndProc(ref m);
            Timing.Frame(t0);
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Paint timings (reported by --bench).</summary>
    public PaintTiming Timing { get; } = new();
    private long _secT;

    public FaceplateView(RadioController c)
    {
        _c = c;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            _holdFired = true;
            _holdAction?.Invoke();
            Invalidate();
        };
        _nerd = new NerdPanel(c);
        _fastPaint.Tick += (_, _) =>
        {
            if (PanelLive) { _fastPaint.Stop(); return; }   // the frame pacer is already painting every refresh
            Invalidate();
            if (!_nerd.Animating) _fastPaint.Stop();
        };
        // The instrument panel's moving parts: on the GPU (its own window and render thread, at the display's refresh),
        // or, if that's unavailable, with GDI+ on the UI thread paced by the frame pacer.
        _gpu = new GpuPanel(cv =>
        {
            _nerd.FrameTick(_c.Engine);
            _nerd.DrawGpu(cv, PanelArea, Lit);
        }) { Visible = false };
        _gpu.Failed += ex => BeginInvoke(() =>
        {
            _gpuFailed = true;
            StopPanelFrames();
            if (PanelLive) StartPanelFrames();   // carry on with GDI+
        });
        Controls.Add(_gpu);
        _pacer = new FramePacer(this, () =>
        {
            if (FaceplateLive)
            {
                // closed: the analyzer at the frame rate (the rest of the faceplate updates on the 100 ms tick)
                PullAudio();
                if (_analyzerShown) { Invalidate(AnalyzerPixels); Update(); }
                return;
            }
            if (!PanelLive) { _pacer!.Stop(); return; }
            _nerd.FrameTick(_c.Engine);
            InvalidateDesign(_nerd.FastRects);
            if (_slowDue) { _slowDue = false; InvalidateDesign(_nerd.SlowRects); }
            Update();   // paint now, so the frame is on screen before the pacer asks for the next one
        });
        // any retune (keys, presets, seek) slides the open spectrum too
        c.Changed += () => { if (_open) _fastPaint.Start(); };
        _animTimer.Tick += (_, _) =>
        {
            float step = 15f / 380f;
            _anim = _open ? Math.Min(1, _anim + step) : Math.Max(0, _anim - step);
            if (_open && _anim >= 1 || !_open && _anim <= 0)
            {
                _animTimer.Stop();
                if (_open) StartPanelFrames();
                else
                {
                    OpenLayout?.Invoke(false);   // fully folded back up: shrink the window
                    StartFaceplateFrames();
                    _chassisBg?.Dispose();
                    _chassisBg = null;
                }
            }
            Invalidate();
        };
    }

    /// <summary>Flips the faceplate down to reveal the instrument panel, or back up.</summary>
    public void ToggleOpen()
    {
        if (_animTimer.Enabled) return;
        _open = !_open;
        if (_open) { _pacer.Stop(); OpenLayout?.Invoke(true); }   // grow the window first, then fold the faceplate down
        else StopPanelFrames();                // GDI+ draws the panel while it folds away
        _animTimer.Start();
    }

    private readonly FramePacer _pacer;
    private readonly GpuPanel _gpu;
    private bool _slowDue, _gpuFailed;

    /// <summary>(Re)starts the panel's live frames at the frame rate setting: on the GPU if possible.</summary>
    public void StartPanelFrames()
    {
        if (!PanelLive) return;
        int fps = _c.Settings.PanelFps;   // 0 = every display refresh
        if (!_gpuFailed && Environment.GetEnvironmentVariable("R808_NO_GPU") != "1")
        {
            _pacer.Stop();
            PositionGpu();
            _nerd.LiveOnGpu = true;
            _gpu.Visible = true;
            _gpu.Start(fps);
            return;
        }
        _pacer.Start(fps > 0 ? Math.Clamp(fps, 10, 120) : 60);   // GDI+ can't keep up with a fast display: 60 at most
    }

    private void StopPanelFrames()
    {
        _gpu.Stop();
        _gpu.Visible = false;
        _nerd.LiveOnGpu = false;
        _pacer.Stop();
        Invalidate();
    }

    /// <summary>Puts the GPU window over the panel's live column, and gives it the design → pixel transform.</summary>
    private void PositionGpu()
    {
        if (!_gpu.Visible && !PanelLive) return;
        ComputeTransform();
        var a = _nerd.LiveArea;
        if (a.Width <= 0) { _nerd.Layout(PanelArea); a = _nerd.LiveArea; }
        var px = Rectangle.FromLTRB((int)Math.Floor(_ox + a.Left * _scale), (int)Math.Floor(_oy + a.Top * _scale),
            (int)Math.Ceiling(_ox + a.Right * _scale), (int)Math.Ceiling(_oy + a.Bottom * _scale));
        _gpu.Bounds = px;
        _gpu.SetView(System.Numerics.Matrix3x2.CreateScale(_scale) * System.Numerics.Matrix3x2.CreateTranslation(_ox - px.Left, _oy - px.Top),
            PanelBackground);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_gpu?.Visible == true) PositionGpu();
    }

    private void ComputeTransform()
    {
        _scale = Math.Min(Width / W, Height / H);
        _ox = (Width - W * _scale) / 2;
        _oy = (Height - H * _scale) / 2;
    }

    /// <summary>Frames per second the panel is actually running at (0 when closed), and on what.</summary>
    public string PanelRenderer => _gpu.Running ? $"GPU {_gpu.Fps:0} fps of {_gpu.RefreshHz:0} Hz, {_gpu.FrameMs:0.00} ms CPU/frame"
        : _pacer.Running ? $"GDI+ {_pacer.Fps:0} fps" : "closed";

    /// <summary>The panel is fully open (not mid-flip).</summary>
    private bool PanelLive => _open && _anim >= 1;

    /// <summary>The faceplate is up (not open, not mid-flip).</summary>
    private bool FaceplateLive => !_open && _anim <= 0;

    /// <summary>Runs the faceplate's analyzer at up to 60 fps (GDI+: it repaints just that square).</summary>
    private void StartFaceplateFrames()
    {
        if (FaceplateLive) _pacer.Start(60);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        StartFaceplateFrames();
    }

    /// <summary>Invalidates design-coordinate rectangles (a pixel of margin for anti-aliasing).</summary>
    private void InvalidateDesign(RectangleF[] rects)
    {
        if (_scale <= 0) { Invalidate(); return; }
        foreach (var r in rects)
            Invalidate(Rectangle.FromLTRB((int)Math.Floor(_ox + r.Left * _scale) - 2, (int)Math.Floor(_oy + r.Top * _scale) - 2,
                (int)Math.Ceiling(_ox + r.Right * _scale) + 2, (int)Math.Ceiling(_oy + r.Bottom * _scale) + 2));
    }

    private Color Lit => Illuminations[Math.Clamp(_c.Settings.Illumination, 0, Illuminations.Length - 1)].Color;

    /// <summary>Shows a short message on the display for a moment (e.g. "VOL 18", "P3 SAVED").</summary>
    public void Flash(string text, double seconds = 1.6)
    {
        _flash = text.ToUpperInvariant();
        _flashUntil = DateTime.UtcNow.AddSeconds(seconds);
        Invalidate();
    }

    // ------------------------------------------------------------------ periodic update (100 ms)

    public void Tick()
    {
        if (PanelLive)
        {
            // the frame pacer handles the spectrum; here just the slower sections (the faceplate is folded away), which
            // ride along with the next frame rather than painting on their own between frames
            _nerd.Tick(_c.Engine);
            if (_gpu.Running) InvalidateDesign(_nerd.SlowRects);   // the GPU draws the rest by itself
            else _slowDue = true;
            return;
        }
        if (!_pacer.Running) PullAudio();   // (the pacer does it per frame while the faceplate is up)
        if (_open) _nerd.Tick(_c.Engine);
        // marquees (big line and small line): hold 2 s at the start, then one character every 300 ms, a gap, and
        // around again
        Marquee(_mainCols.Count / DotMatrix.CellCols, MainCells, ref _scrollChars, ref _scrollTicks);
        Marquee(_subCols.Count / DotMatrix.CellCols, _subCells, ref _subScroll, ref _subTicks);
        HdPages();
        Invalidate();
    }

    /// <summary>
    /// The faceplate's audio spectrum analyzer, like an old head unit's: 16 bands from 40 Hz to 16 kHz, from the audio
    /// that's playing. Bars rise instantly and fall steadily; a peak tick holds above each bar, then drops.
    /// </summary>
    private void PullAudio()
    {
        var eng = _c.Engine;
        if (eng == null) { _specValid = false; return; }
        if (!eng.TryGetLatestAudio(_audioBlock)) return;
        _analyzer ??= new AudioAnalyzer(Bars, FmReceiver.AudioRate);
        _analyzer.Analyze(_audioBlock, _bandDb);
        double now = _barClock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _barsAt, 0, 0.25);
        _barsAt = now;
        float fall = (float)(1.6 * dt), peakFall = (float)(1.0 * dt);
        bool silent = _c.Settings.Muted;   // muted: nothing to show, like the real thing
        for (int b = 0; b < Bars; b++)
        {
            // -42 dB → empty, -6 dB → full: on broadcast music a bar typically sits about halfway and moves with the beat
            // (measured over FM and HD recordings with `radio808-tools analyzer`)
            float level = silent ? 0 : Math.Clamp((_bandDb[b] + 42) / 36f, 0, 1);
            _bars[b] = _specValid ? Math.Max(level, _bars[b] - fall) : level;
            if (_bars[b] >= _peaks[b]) { _peaks[b] = _bars[b]; _peakHoldUntil[b] = now + 0.6; }
            else if (now > _peakHoldUntil[b]) _peaks[b] = Math.Max(_bars[b], _peaks[b] - peakFall);
        }
        _specValid = true;
    }

    private AudioAnalyzer? _analyzer;
    private readonly float[] _audioBlock = new float[AudioAnalyzer.BlockSize], _bandDb = new float[Bars];
    private readonly float[] _peaks = new float[Bars];
    private readonly double[] _peakHoldUntil = new double[Bars];
    private readonly System.Diagnostics.Stopwatch _barClock = System.Diagnostics.Stopwatch.StartNew();
    private double _barsAt;

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        _secT = System.Diagnostics.Stopwatch.GetTimestamp();
        _scale = Math.Min(Width / W, Height / H);
        if (_scale <= 0) { g.Clear(Background); return; }
        _ox = (Width - W * _scale) / 2;
        _oy = (Height - H * _scale) / 2;
        // closed, and only the analyzer moved (its fast frames): repaint just that square
        if (H <= ClosedH && AnalyzerPixels.Contains(e.ClipRectangle))
        {
            g.TranslateTransform(_ox, _oy);
            g.ScaleTransform(_scale, _scale);
            PaintAnalyzerOnly(g);
            return;
        }
        // open: the chassis behind the panel never changes, so it's drawn once per window size and copied after that
        if (H > ClosedH)
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(ChassisBackground(), 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
        }
        else g.Clear(Background);
        _secT = Timing.Section("background", _secT);
        g.TranslateTransform(_ox, _oy);
        g.ScaleTransform(_scale, _scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        _hits.Clear();

        if (H > ClosedH)
        {
            DrawChassis(g);
            _secT = Timing.Section("lip", _secT);
            if (_anim < 1)
            {
                // the faceplate folding down: it slides to the hinge at the bottom and flattens toward edge-on
                float t = _anim * _anim * (3 - 2 * _anim);   // smoothstep
                float top = 524 * t, height = ClosedH * (1 - t) + 34 * t;
                var st = g.Save();
                g.TranslateTransform(0, top);
                g.ScaleTransform(1, height / ClosedH);
                DrawFaceplate(g);
                using (var shade = new SolidBrush(Color.FromArgb((int)(170 * t), 0, 0, 0)))
                using (var p = Rounded(Outline, OutlineRadius)) g.FillPath(shade, p);
                g.Restore(st);
                _hits.Clear();   // nothing is clickable mid-flip
            }
            return;
        }
        DrawFaceplate(g);
    }

    private void DrawFaceplate(Graphics g)
    {
        DrawBody(g);
        DrawLeftKeys(g);
        DrawKnob(g);
        DrawDisplay(g);
        DrawKeyStrip(g);
        DrawRightSide(g);
    }

    private static readonly Color Background = Color.FromArgb(0x05, 0x06, 0x07);
    private Bitmap? _chassisBg;

    /// <summary>The window's background with the faceplate open (chassis, screws, the panel's frame), at this size.</summary>
    private Bitmap ChassisBackground()
    {
        if (_chassisBg != null && _chassisBg.Width == Width && _chassisBg.Height == Height) return _chassisBg;
        _chassisBg?.Dispose();
        _chassisBg = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);   // the format GDI+ copies fastest
        using var g = Graphics.FromImage(_chassisBg);
        g.Clear(Background);
        g.TranslateTransform(_ox, _oy);
        g.ScaleTransform(_scale, _scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawChassisBackground(g);
        return _chassisBg;
    }

    private static void DrawChassisBackground(Graphics g)
    {
        var outer = new RectangleF(12, 14, 976, 546);
        using (var p = Rounded(outer, OutlineRadius))
        {
            using (var b = new LinearGradientBrush(outer, Color.FromArgb(0x1C, 0x1F, 0x24), Color.FromArgb(0x0A, 0x0B, 0x0D), 90f)) g.FillPath(b, p);
            using var pen = new Pen(Color.FromArgb(0x48, 0x4E, 0x57), 1.5f);
            g.DrawPath(pen, p);
        }
        // screws in the corners of the mechanism
        foreach (var (sx, sy) in new[] { (30f, 32f), (970f, 32f), (30f, 508f), (970f, 508f) })
        {
            using var b = new SolidBrush(Color.FromArgb(0x2C, 0x30, 0x36));
            g.FillEllipse(b, sx - 5, sy - 5, 10, 10);
            using var pen = new Pen(Color.FromArgb(0x0C, 0x0D, 0x0F), 1.4f);
            g.DrawLine(pen, sx - 3, sy, sx + 3, sy);
        }
        var panel = new RectangleF(44, 26, 912, 490);
        using (var p = Rounded(panel, 8))
        {
            using (var b = new SolidBrush(PanelBackground)) g.FillPath(b, p);
            using var pen = new Pen(Color.FromArgb(0x22, 0x28, 0x2F), 1.5f);
            g.DrawPath(pen, p);
        }
    }

    private static readonly RectangleF PanelArea = new(44, 26, 912, 490);
    private static readonly Color PanelBackground = Color.FromArgb(0x05, 0x08, 0x0A);

    /// <summary>Open: the instrument panel and the folded faceplate's lip, over the cached chassis.</summary>
    private void DrawChassis(Graphics g)
    {
        var lit = Lit;
        var panel = PanelArea;
        if (_anim > 0.6f) _nerd.Draw(g, panel, lit, Timing);
        _secT = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_anim >= 1)
        {
            _hits.Add(new Hit(_nerd.TuneArea, "tune", null));   // click = tune (handled in OnMouseUp, needs the x)
            _hits.Add(new Hit(_nerd.SpanToggle, "span", () => _nerd.Wide = !_nerd.Wide));
        }

        // the folded faceplate's lip along the bottom; click it to close
        if (_anim >= 1)
        {
            var lip = new RectangleF(24, 524, 952, 30);
            bool hot = _hover == "closeface";
            using (var p = Rounded(lip, 8))
            {
                using (var b = new LinearGradientBrush(lip, hot ? Color.FromArgb(0x3A, 0x3F, 0x47) : Body1, Body2, 90f)) g.FillPath(b, p);
                using var pen = new Pen(Color.FromArgb(0x05, 0x05, 0x06), 1.2f);
                g.DrawPath(pen, p);
            }
            using (var hl = new Pen(Color.FromArgb(40, Color.White), 1)) g.DrawLine(hl, lip.X + 10, lip.Y + 1.5f, lip.Right - 10, lip.Y + 1.5f);
            Label(g, "▲  CLOSE FACEPLATE", 11, lit, lip, StringAlignment.Center, bold: true);
            _hits.Add(new Hit(lip, "closeface", ToggleOpen));
            WindowKey(g, new RectangleF(924, 531, 18, 16), "min", () => MinimizeRequested?.Invoke(), close: false);
            WindowKey(g, new RectangleF(948, 531, 18, 16), "close", () => CloseRequested?.Invoke(), close: true);
        }
    }

    private void DrawBody(Graphics g)
    {
        var outer = Outline;
        using (var p = Rounded(outer, OutlineRadius))
        {
            using (var b = new LinearGradientBrush(outer, Body1, Body2, 90f)) g.FillPath(b, p);
            using var pen = new Pen(Color.FromArgb(0x48, 0x4E, 0x57), 1.5f);
            g.DrawPath(pen, p);
        }
        // gloss highlight along the top edge
        using (var hl = new Pen(Color.FromArgb(40, Color.White), 1.2f)) g.DrawLine(hl, 40, 17, 960, 17);
        var face = new RectangleF(24, 26, 952, 248);
        using (var p = Rounded(face, 20))
        {
            using (var b = new LinearGradientBrush(face, Face1, Face2, 90f)) g.FillPath(b, p);
            using var pen = new Pen(Color.FromArgb(0x05, 0x05, 0x06), 2f);
            g.DrawPath(pen, p);
        }
        // brand above the display, model name at the right
        BrandMark.Draw(g, 538, 32, 20, Silver);
        Label(g, "HD-808", 9.5f, Grey, new RectangleF(800, 32, 92, 20), StringAlignment.Far);

        // OPEN: flips the faceplate down (where a head unit has its open/eject key)
        var open = new RectangleF(272, 34, 40, 16);
        bool hotOpen = _hover == "open";
        using (var p = Rounded(open, 4))
        {
            using var b = new SolidBrush(hotOpen ? Color.FromArgb(0x3A, 0x40, 0x48) : Color.FromArgb(0x16, 0x18, 0x1C));
            g.FillPath(b, p);
        }
        using (var b = new SolidBrush(Lit))
        {
            float cx = open.X + open.Width / 2, cy = open.Y + 7;
            g.FillPolygon(b, new[] { new PointF(cx - 5, cy + 2), new PointF(cx + 5, cy + 2), new PointF(cx, cy - 3) });
            g.FillRectangle(b, cx - 5, cy + 4, 10, 1.6f);
        }
        _hits.Add(new Hit(open, "open", ToggleOpen));

        // window keys (the window has no frame): minimize and close, top right
        WindowKey(g, new RectangleF(926, 34, 18, 16), "min", () => MinimizeRequested?.Invoke(), close: false);
        WindowKey(g, new RectangleF(948, 34, 18, 16), "close", () => CloseRequested?.Invoke(), close: true);
    }

    private void WindowKey(Graphics g, RectangleF r, string id, Action click, bool close)
    {
        bool hot = _hover == id;
        using (var p = Rounded(r, 4))
        {
            using var b = new SolidBrush(hot ? (close ? Color.FromArgb(0xC4, 0x2B, 0x2B) : Color.FromArgb(0x3A, 0x40, 0x48)) : Color.FromArgb(0x16, 0x18, 0x1C));
            g.FillPath(b, p);
            using var pen = new Pen(Color.FromArgb(0x05, 0x05, 0x06), 1);
            g.DrawPath(pen, p);
        }
        using var pen2 = new Pen(hot ? Color.White : Grey, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        if (close) { g.DrawLine(pen2, cx - 3.5f, cy - 3.5f, cx + 3.5f, cy + 3.5f); g.DrawLine(pen2, cx + 3.5f, cy - 3.5f, cx - 3.5f, cy + 3.5f); }
        else g.DrawLine(pen2, cx - 4, cy + 1, cx + 4, cy + 1);
        _hits.Add(new Hit(r, id, click));
    }

    private void DrawLeftKeys(Graphics g)
    {
        var lit = Lit;
        // MUTE (patterned, like an illuminated phone key)
        var mute = new RectangleF(36, 40, 86, 54);
        Key(g, mute, "mute", _c.ToggleMute, pattern: true);
        SpeakerIcon(g, new RectangleF(mute.X, mute.Y, mute.Width, mute.Height), _c.Settings.Muted, lit);

        // SRC: HD / FM
        var src = new RectangleF(36, 110, 62, 56);
        Key(g, src, "src", () =>
        {
            _c.SetForceAnalog(!_c.Settings.ForceAnalog);
            Flash(_c.Settings.ForceAnalog ? "SOURCE FM" : "SOURCE HD");
        });
        PowerIcon(g, src.X + src.Width / 2, src.Y + 20, 8, lit);
        Label(g, "SRC", 12, lit, new RectangleF(src.X, src.Y + 32, src.Width, 18), StringAlignment.Center, bold: true);

        // COLOR (patterned)
        var col = new RectangleF(36, 182, 86, 54);
        Key(g, col, "color", CycleColor, pattern: true);
        BulbIcon(g, col.X + col.Width / 2, col.Y + col.Height / 2, lit);
    }

    public void CycleColor()
    {
        _c.Settings.Illumination = (_c.Settings.Illumination + 1) % Illuminations.Length;
        _c.Settings.Save();
        Flash("COLOR " + Illuminations[_c.Settings.Illumination].Name);
    }

    private void DrawKnob(Graphics g)
    {
        float cx = 196, cy = 124, r = 58;
        var lit = Lit;
        // light ring: dim track, lit arc for the volume, glow
        float vol = _c.Settings.Volume;
        bool muted = _c.Settings.Muted;
        var ring = new RectangleF(cx - r - 9, cy - r - 9, 2 * (r + 9), 2 * (r + 9));
        using (var track = new Pen(Color.FromArgb(40, lit), 6)) g.DrawArc(track, ring, 135, 270);
        if (vol > 0.001f)
        {
            var c = muted ? Grey : lit;
            using (var glow = new Pen(Color.FromArgb(60, c), 14) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(glow, ring, 135, 270 * vol);
            using (var arc = new Pen(c, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arc, ring, 135, 270 * vol);
        }
        // the knob: dark metal with a bright rim and a pointer
        var kr = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
        using (var b = new LinearGradientBrush(kr, Color.FromArgb(0x3A, 0x3F, 0x47), Color.FromArgb(0x0B, 0x0C, 0x0E), 70f)) g.FillEllipse(b, kr);
        using (var rim = new Pen(Color.FromArgb(0x5A, 0x61, 0x6B), 2)) g.DrawEllipse(rim, kr);
        var inner = new RectangleF(cx - r + 10, cy - r + 10, 2 * r - 20, 2 * r - 20);
        using (var b = new LinearGradientBrush(inner, Color.FromArgb(0x23, 0x27, 0x2D), Color.FromArgb(0x14, 0x16, 0x1A), 250f)) g.FillEllipse(b, inner);
        double a = (135 + 270 * vol) * Math.PI / 180;
        float px = cx + (float)Math.Cos(a) * (r - 16), py = cy + (float)Math.Sin(a) * (r - 16);
        using (var dot = new SolidBrush(muted ? Grey : lit)) g.FillEllipse(dot, px - 3.5f, py - 3.5f, 7, 7);
        _hits.Add(new Hit(new RectangleF(cx - r - 12, cy - r - 12, 2 * r + 24, 2 * r + 24), "knob", null));

        // DISP / HD CH (next HD program)
        Label(g, "HD CH", 10, lit, new RectangleF(200, 196, 52, 14), StringAlignment.Center, bold: true);   // next HD program
        Label(g, "DISP", 10, lit, new RectangleF(142, 196, 52, 14), StringAlignment.Center, bold: true);
        Key(g, new RectangleF(200, 212, 52, 22), "band", NextProgram);
        Key(g, new RectangleF(142, 212, 52, 22), "disp", NextDisplay);
    }

    /// <summary>
    /// Pages the HD matrix (10 Hz): the program list holds 3 s; a format holds 3 s if it fits, else holds 1 s, scrolls
    /// a character every 300 ms to its end, and holds 1 s more; then the next page.
    /// </summary>
    private void HdPages()
    {
        if (_hdPageCount == 0) { _hdPage = _hdTicks = _hdScroll = 0; return; }
        _hdTicks++;
        bool fits = _hdPage % 2 == 0 || _hdPageLen <= HdCells;
        if (fits)
        {
            if (_hdTicks < 30) return;
        }
        else
        {
            int end = _hdPageLen - HdCells;   // scrolled far enough that the last character shows
            if (_hdScroll < end)
            {
                if (_hdTicks > 10 && (_hdTicks - 10) % 3 == 0) _hdScroll++;
                return;
            }
            if (_hdTicks < 10 + 3 * end + 10) return;
        }
        _hdPage = (_hdPage + 1) % _hdPageCount;
        _hdTicks = _hdScroll = 0;
    }

    private static void Marquee(int len, int cells, ref int chars, ref int ticks)
    {
        if (len <= cells) { chars = ticks = 0; return; }
        ticks++;
        const int start = 20, every = 3;
        if (ticks > start && (ticks - start) % every == 0 && ++chars > len + 3) chars = ticks = 0;
    }

    /// <summary>DISP: the next combination of what the big and small lines show (it flashes its name).</summary>
    public void NextDisplay() => SetDisplay((Mode + 1) % DisplayModes.Length);

    public void SetDisplay(int mode)
    {
        _c.Settings.DisplayMode = Math.Clamp(mode, 0, DisplayModes.Length - 1);
        _scrollChars = _scrollTicks = _subScroll = _subTicks = 0;
        Flash(DisplayModes[Mode].Name, 1.2);
    }

    public void NextProgram()
    {
        var eng = _c.Engine;
        if (eng == null || eng.Hd.Programs.Count == 0) { Flash("NO HD"); return; }
        var hd = eng.Hd;
        var progs = hd.Programs.Keys.ToList();
        int i = progs.IndexOf(eng.Program);
        var p = progs[(i + 1) % progs.Count];
        _c.SetProgram(p);
        // which one of how many, and its format if the station says ("HD2/3 CLASSIC ROCK")
        string type = hd.Programs[p] is { Length: > 0 } t ? " " + t : "";
        Flash($"HD{p + 1}/{progs.Count}{type}{(eng.HdTooWeak ? " WEAK" : "")}", 2.5);
    }

    // ------------------------------------------------------------------ the display

    private void DrawDisplay(Graphics g)
    {
        var lit = Lit;
        var glass = new RectangleF(272, 56, 640, 142);
        using (var p = Rounded(glass, 7))
        {
            using (var b = new LinearGradientBrush(glass, Color.FromArgb(0x07, 0x0C, 0x0F), Color.FromArgb(0x02, 0x04, 0x05), 90f)) g.FillPath(b, p);
            using var pen = new Pen(Color.FromArgb(0x22, 0x28, 0x2F), 1.5f);
            g.DrawPath(pen, p);
        }
        var ghost = Color.FromArgb(20, lit);
        var eng = _c.Engine;
        var hd = eng?.Hd;
        var rds = eng?.Receiver.Rds;
        bool synced = hd?.Synced == true, playingHd = eng?.Blender.PlayingHd == true;
        long freq = _c.Frequency;
        string mhz = (freq / 1e6).ToString("0.0");

        // main line
        string text = MainText(eng, hd, rds, synced, mhz, out bool alert);
        if (text != _mainText)
        {
            _mainText = text;
            _mainCols = DotMatrix.Columns(text);
            _scrollChars = 0; _scrollTicks = 0;
        }
        DotMatrix.Draw(g, _mainCols, _scrollChars * DotMatrix.CellCols, 292, 76, 6.3f, MainCells, alert ? Alert : lit, ghost);
        if (eng == null && !_c.Starting) _hits.Add(new Hit(new RectangleF(284, 64, 500, 64), "start", () => _ = _c.StartAsync()));

        // line 2: a full-width small dot-matrix line: the big line's partner in the DISP combination (station "KLLC 97.3",
        // the song, the genre, ...), and the clock on the right
        // the info cells (ghost dots where empty), then a seven-segment clock at the right end, like a VFD's
        DotMatrix.Draw(g, DotMatrix.Columns(""), 0, 292, 136, 3.1f, Line2Cells - 6, lit, ghost, glow: false);
        string clockText = DateTime.Now.ToString("H:mm").PadLeft(5);   // " 9:41": a blank first digit shows as a ghost 8
        const float clockH = 24.5f;
        float clockW = 4 * SevenSegment.DigitWidth(clockH) + 3 * clockH * 0.16f + clockH * 0.24f;
        SevenSegment.Draw(g, clockText, 776 - clockW, 136.5f, clockH, lit, ghost);

        _subCells = Line2Cells - 6;   // up to the clock
        string sub = SubText(eng, hd, rds, synced, mhz);
        if (sub != _subText)
        {
            _subText = sub;
            _subCols = DotMatrix.Columns(sub);
            _subScroll = _subTicks = 0;
        }
        DotMatrix.Draw(g, _subCols, _subScroll * DotMatrix.CellCols, 292, 136, 3.1f, _subCells, lit, ghost, glow: false);

        // line 3: the status lights, fixed legends printed on the glass like a real head unit's display: each is either
        // lit or dark, and nothing moves. HD WEAK · stereo RDS · SEEK · WX TRAFFIC, signal bars at the right end.
        float x = 292;
        x = Indicator(g, x, 172, "HD", synced, playingHd, lit);   // outlined: found; filled: playing (digital audio)
        // WEAK (red), right after HD so it reads "HD WEAK": HD found (name, programs) but the signal is too poor to play
        // its audio, or it's holding off after dropouts until the signal is steady again
        bool weak = eng != null && !_c.Settings.ForceAnalog && (eng.HdTooWeak || synced && !playingHd && eng.Blender.RetryIn > 0.5);
        x = Indicator(g, x, 172, "WEAK", weak, weak, lit, Alert) + 10;
        bool stereo = eng != null && (playingHd || eng.Receiver.Stereo.PilotLocked && eng.Receiver.Stereo.Blend > 0.5f);
        x = StereoIcon(g, x, 172, stereo, lit);
        bool rdsOn = rds?.Synced == true;
        x = Indicator(g, x, 172, "RDS", rdsOn, rdsOn, lit) + 10;
        x = Indicator(g, x, 172, "SEEK", _c.Seeking, _c.Seeking, lit) + 10;
        bool wx = hd?.WeatherMap != null, trf = hd != null && hd.TrafficTiles.Any(t => t != null);
        float wxEnd = Indicator(g, x, 172, "WX", wx, false, lit);
        if (wx) _hits.Add(new Hit(new RectangleF(x - 2, 168, wxEnd - x, 22), "wx", () => MapRequested?.Invoke("weather")));
        x = wxEnd;
        float trfEnd = Indicator(g, x, 172, "TRAFFIC", trf, false, lit);
        if (trf) _hits.Add(new Hit(new RectangleF(x - 2, 168, trfEnd - x, 22), "trf", () => MapRequested?.Invoke("traffic")));

        // a mini dot matrix after the lights, always there (unlit without HD), cycling through pages: the programs on air
        // ("HD 1 2 3 4"), the format of the one you're hearing ("HD1 ADULT HITS", scrolling if it's long), the programs
        // again, the next program's format, and so on round. The one you're hearing is lit, the one you've chosen
        // blinks while it locks in, the rest are dimmer (all dimmer when HD is too weak to play: WEAK says why).
        // Click it for the next program.
        {
            const float pitch = 2.2f, cellW = pitch * DotMatrix.CellCols;
            float mx = trfEnd + 14, my = 171;
            DotMatrix.Draw(g, DotMatrix.Columns(""), 0, mx, my, pitch, HdCells, lit, ghost, glow: false);
            if (hd != null && synced && hd.Programs.Count > 0 && eng != null)
            {
                var dim = Color.FromArgb(110, lit);
                bool tooWeak = eng.HdTooWeak, blinkOn = DateTime.UtcNow.Millisecond < 500;
                Color ColorOf(uint p) => p != eng.Program ? dim
                    : playingHd ? lit : !tooWeak && !_c.Settings.ForceAnalog && blinkOn ? lit : dim;
                var progs = hd.Programs.Keys.ToList();
                // the formats to show: the program you're hearing first, then the others in order after it
                int at = Math.Max(0, progs.IndexOf(eng.Program));
                var withFormat = Enumerable.Range(0, progs.Count).Select(k => progs[(at + k) % progs.Count])
                    .Where(p => !string.IsNullOrWhiteSpace(hd.Programs[p])).ToList();
                string key = string.Join(",", progs) + "|" + eng.Program + "|" + string.Join(",", withFormat.Select(p => hd.Programs[p]));
                if (key != _hdKey) { _hdKey = key; _hdPage = _hdTicks = _hdScroll = 0; }   // new list or program: start over
                _hdPageCount = withFormat.Count == 0 ? 1 : 2 * withFormat.Count;            // list, format, list, format...
                if (_hdPage >= _hdPageCount) _hdPage = 0;

                var chars = new List<(char ch, Color c)>();
                if (_hdPage % 2 == 0)
                {
                    bool spaced = progs.Count <= 4;
                    chars.Add(('H', playingHd ? lit : dim));
                    chars.Add(('D', playingHd ? lit : dim));
                    foreach (uint p in progs)
                    {
                        if (spaced || chars.Count == 2) chars.Add((' ', dim));
                        chars.Add(((char)('0' + (p + 1) % 10), ColorOf(p)));
                    }
                }
                else
                {
                    uint p = withFormat[_hdPage / 2];
                    chars.AddRange($"HD{p + 1} {hd.Programs[p]!.ToUpperInvariant()}".Select(ch => (ch, ColorOf(p))));
                }
                _hdPageLen = chars.Count;
                for (int i = 0; i < HdCells; i++)
                {
                    int k = i + _hdScroll;
                    if (k >= chars.Count) break;
                    DotMatrix.Draw(g, DotMatrix.Columns(chars[k].ch.ToString()), 0, mx + i * cellW, my, pitch, 1, chars[k].c, ghost, glow: false);
                }
                _hits.Add(new Hit(new RectangleF(mx - 3, 166, HdCells * cellW + 6, 26), "hdlist", NextProgram));
            }
            else _hdPageCount = 0;
        }

        // art square: album art / station logo (click to enlarge), else the audio spectrum analyzer; while muted, a big
        // blinking MUTE (the speaker key's little cross is easy to miss)
        var sq = AnalyzerArea;
        Image? art = synced && _c.Settings.ShowAlbumArt ? Decode(hd?.AlbumArt ?? hd?.StationLogo) : null;
        if (_c.Settings.Muted)
        {
            const float pitch = 4.3f;   // 4 characters of 6 dot columns across the 104-wide square
            bool on = DateTime.UtcNow.Millisecond < 700;
            float mh = pitch * DotMatrix.Rows, mw = pitch * (4 * DotMatrix.CellCols - 1);
            DotMatrix.Draw(g, DotMatrix.Columns("MUTE"), 0, sq.X + (sq.Width - mw) / 2, sq.Y + (sq.Height - mh) / 2, pitch, 4,
                on ? Alert : Color.FromArgb(40, Alert), Color.FromArgb(20, lit));
            _hits.Add(new Hit(sq, "unmute", _c.ToggleMute));   // click it to unmute
            _analyzerShown = false;
            return;
        }
        if (art != null)
        {
            var st = g.Save();
            using (var clip = Rounded(sq, 5)) g.SetClip(clip);
            float s = Math.Max(sq.Width / art.Width, sq.Height / art.Height);
            g.DrawImage(art, sq.X + (sq.Width - art.Width * s) / 2, sq.Y + (sq.Height - art.Height * s) / 2, art.Width * s, art.Height * s);
            g.Restore(st);
            if (_hover == "art")
                using (var pen = new Pen(Color.FromArgb(160, lit), 1.5f))
                using (var p = Rounded(sq, 5)) g.DrawPath(pen, p);
            _hits.Add(new Hit(sq, "art", () => ArtRequested?.Invoke(art, PointToScreen(DesignToClient(sq)))));
        }
        else DrawAnalyzer(g, lit);
        _analyzerShown = art == null;
    }

    /// <summary>The album art / logo was clicked: show it bigger, popping out of this screen rectangle.</summary>
    public event Action<Image, Rectangle>? ArtRequested;

    /// <summary>A design-coordinate rectangle in client pixels.</summary>
    private Rectangle DesignToClient(RectangleF r) => Rectangle.FromLTRB(
        (int)Math.Round(_ox + r.Left * _scale), (int)Math.Round(_oy + r.Top * _scale),
        (int)Math.Round(_ox + r.Right * _scale), (int)Math.Round(_oy + r.Bottom * _scale));

    private Rectangle PointToScreen(Rectangle r) => new(PointToScreen(r.Location), r.Size);

    /// <summary>The art square on screen (where the art pops out from).</summary>
    public Rectangle ArtSquareOnScreen => PointToScreen(DesignToClient(AnalyzerArea));

    /// <summary>The display's art square: album art / station logo when HD has one, else the audio spectrum analyzer.</summary>
    private static readonly RectangleF AnalyzerArea = new(800, 64, 104, 104);
    private static readonly RectangleF Glass = new(272, 56, 640, 142);
    private bool _analyzerShown;

    private void DrawAnalyzer(Graphics g, Color lit)
    {
        var sq = AnalyzerArea;
        var ghost = Color.FromArgb(20, lit);
        float bw = sq.Width / Bars;
        int segs = 13;
        float sh = sq.Height / segs;
        // one color, the illumination, brightening toward the top for depth; the held peaks brightest
        var rows = new SolidBrush[segs];
        for (int s = 0; s < segs; s++) rows[s] = new SolidBrush(Color.FromArgb(120 + 120 * s / (segs - 1), lit));
        using var peak = new SolidBrush(Color.FromArgb(255, Math.Min(255, lit.R + 70), Math.Min(255, lit.G + 70), Math.Min(255, lit.B + 70)));
        using var off = new SolidBrush(ghost);
        for (int b = 0; b < Bars; b++)
        {
            int litSegs = _specValid ? (int)Math.Round(_bars[b] * segs) : 0;
            int peakSeg = _specValid ? (int)Math.Round(_peaks[b] * segs) - 1 : -1;   // the held peak, above the bar
            for (int s = 0; s < segs; s++)
                g.FillRectangle(s < litSegs ? rows[s] : s == peakSeg ? peak : off, sq.X + b * bw + 1, sq.Bottom - (s + 1) * sh + 1, bw - 2, sh - 2);
        }
        foreach (var r in rows) r.Dispose();
    }

    /// <summary>A frame that only moves the analyzer: repaint the glass under it and the bars, nothing else.</summary>
    private void PaintAnalyzerOnly(Graphics g)
    {
        var r = RectangleF.Inflate(AnalyzerArea, 1, 1);
        using (var b = new LinearGradientBrush(Glass, Color.FromArgb(0x07, 0x0C, 0x0F), Color.FromArgb(0x02, 0x04, 0x05), 90f))
            g.FillRectangle(b, r);
        DrawAnalyzer(g, Lit);
    }

    private Rectangle AnalyzerPixels => Rectangle.FromLTRB(
        (int)Math.Floor(_ox + (AnalyzerArea.Left - 1) * _scale), (int)Math.Floor(_oy + (AnalyzerArea.Top - 1) * _scale),
        (int)Math.Ceiling(_ox + (AnalyzerArea.Right + 1) * _scale), (int)Math.Ceiling(_oy + (AnalyzerArea.Bottom + 1) * _scale));

    private string MainText(RadioEngine? eng, HdStatus? hd, RdsStatus? rds, bool synced, string mhz, out bool alert)
    {
        alert = false;
        if (_flash != null && DateTime.UtcNow < _flashUntil) return _flash;
        _flash = null;
        if (_c.Starting) return "STARTING";
        if (eng == null) return (_c.Error ?? "CLICK TO START").ToUpperInvariant();
        if (_c.Seeking) return $"SEEK  {mhz}";
        if (!string.IsNullOrEmpty(hd?.Alert)) { alert = true; return "ALERT  " + hd.Alert.ToUpperInvariant(); }

        return Item(DisplayModes[Mode].Top, eng, hd, rds, synced, mhz, null).ToUpperInvariant();
    }

    // ---- what the two dot-matrix lines show: DISP steps through these (big line, small line) combinations ----

    internal enum InfoItem { Song, Station, Genre, Artist, Title, Frequency }

    public static readonly (string Name, InfoItem Top, InfoItem Bottom)[] DisplayModes =
    {
        ("SONG / STATION", InfoItem.Song, InfoItem.Station),
        ("STATION / SONG", InfoItem.Station, InfoItem.Song),
        ("STATION / GENRE", InfoItem.Station, InfoItem.Genre),
        ("ARTIST / TITLE", InfoItem.Artist, InfoItem.Title),
        ("FREQUENCY / STATION", InfoItem.Frequency, InfoItem.Station),
    };

    private int Mode => Math.Clamp(_c.Settings.DisplayMode, 0, DisplayModes.Length - 1);

    /// <summary>The small line's info (the big line's partner in the current DISP combination).</summary>
    private string SubText(RadioEngine? eng, HdStatus? hd, RdsStatus? rds, bool synced, string mhz)
    {
        if (eng == null) return $"FM {mhz}";
        var (_, top, bottom) = DisplayModes[Mode];
        return Item(bottom, eng, hd, rds, synced, mhz, Item(top, eng, hd, rds, synced, mhz, null)).ToUpperInvariant();
    }

    /// <summary>
    /// One piece of info for a display line. Missing info falls back to the next most useful thing (no song: the
    /// station; no genre: the frequency); <paramref name="avoid"/> is the other line's text, so they don't repeat.
    /// </summary>
    private string Item(InfoItem item, RadioEngine? eng, HdStatus? hd, RdsStatus? rds, bool synced, string mhz, string? avoid)
    {
        string freq = $"FM {mhz}";
        string? name = synced ? hd?.StationName : null;
        name ??= rds?.ProgramService?.Trim() is { Length: > 0 } ps ? ps : rds?.CallSign;
        string station = string.IsNullOrWhiteSpace(name) ? freq : name.Contains(mhz) ? name : $"{name} {mhz}";
        string? title = synced && !string.IsNullOrWhiteSpace(hd?.Title) ? hd!.Title : null;
        string? artist = synced && !string.IsNullOrWhiteSpace(hd?.Artist) ? hd!.Artist : null;
        string? song = title != null ? (artist != null ? $"{title} - {artist}" : title) : rds?.RadioText is { Length: > 0 } rt ? rt : null;
        string? genre = eng != null && synced && hd != null && hd.Programs.TryGetValue(eng.Program, out var type) && !string.IsNullOrWhiteSpace(type)
            ? type : rds?.PtyName is { Length: > 0 } pty ? pty : null;

        IEnumerable<string?> choices = item switch
        {
            InfoItem.Song => new[] { song, station, genre, freq },
            InfoItem.Station => new[] { station, genre, freq },
            InfoItem.Genre => new[] { genre, freq, station },
            InfoItem.Artist => new[] { artist, song, station, freq },
            InfoItem.Title => new[] { title, station, genre, freq },
            _ => new[] { freq, station },
        };
        foreach (var c in choices)
            if (!string.IsNullOrWhiteSpace(c) && !string.Equals(c, avoid, StringComparison.OrdinalIgnoreCase)) return c;
        return avoid == null ? freq : "";
    }

    /// <summary>A small segment-style indicator: outlined when on, filled when active, ghosted when off.</summary>
    /// <summary>The classic stereo indicator: two interlocking rings, lit in stereo.</summary>
    private static float StereoIcon(Graphics g, float x, float y, bool on, Color lit)
    {
        using var pen = new Pen(on ? lit : Color.FromArgb(28, lit), 1.7f);
        const float d = 13, overlap = 5;
        g.DrawEllipse(pen, x + 1, y + 1.5f, d, d);
        g.DrawEllipse(pen, x + 1 + d - overlap, y + 1.5f, d, d);
        return x + 2 + 2 * d - overlap + 8;
    }

    /// <param name="onColor">Color when lit, if not the illumination (e.g. red for WEAK); unlit legends are always a
    /// faint trace of the illumination, like the rest of the glass.</param>
    private float Indicator(Graphics g, float x, float y, string text, bool on, bool active, Color lit, Color? onColor = null)
    {
        using var f = new Font("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        float w = g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width + 10;
        var r = new RectangleF(x, y, w, 16);
        var c = on ? onColor ?? lit : Color.FromArgb(28, lit);
        if (active) { using var b = new SolidBrush(c); using var p = Rounded(r, 3); g.FillPath(b, p); }
        else { using var pen = new Pen(c, 1.1f); using var p = Rounded(r, 3); g.DrawPath(pen, p); }
        using var tb = new SolidBrush(active ? Color.Black : c);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, f, tb, new RectangleF(r.X, r.Y + 0.5f, r.Width, r.Height), fmt);
        return r.Right + 6;
    }

    private Image? Decode(byte[]? bytes)
    {
        if (ReferenceEquals(bytes, _artKey)) return _art;
        _artKey = bytes;
        _art?.Dispose();
        _art = null;
        if (bytes == null) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            _art = new Bitmap(img);
        }
        catch { _art = null; }
        return _art;
    }

    // ------------------------------------------------------------------ keys under the display

    private void DrawKeyStrip(Graphics g)
    {
        var lit = Lit;
        float y = 214, h = 30;
        var down = new RectangleF(272, y, 62, h);
        var up = new RectangleF(338, y, 62, h);
        Key(g, down, "seekdown", () => _c.Seek(-1), lit: _c.Seeking);
        SeekIcon(g, down, -1, lit);
        Key(g, up, "seekup", () => _c.Seek(1), lit: _c.Seeking);
        SeekIcon(g, up, 1, lit);
        float x0 = 408, gap = 4, w = (912 - x0 - gap * 5) / 6;
        int currentPreset = _c.CurrentPreset;   // one key lit, even if several presets hold this station
        for (int i = 0; i < AppSettings.PresetCount; i++)
        {
            var r = new RectangleF(x0 + i * (w + gap), y, w, h);
            int idx = i;
            var p = _c.Settings.Presets[i];
            // the preset you're on is outlined in the illumination color, its frequency lit
            bool current = i == currentPreset;
            Key(g, r, "preset" + i, () =>
            {
                if (_c.Settings.Presets[idx] == null) { Flash($"HOLD {idx + 1} TO SAVE"); return; }
                _c.RecallPreset(idx);
            }, lit: current, hold: () => { StorePreset(idx); Flash($"P{idx + 1} SAVED"); });
            Label(g, (i + 1).ToString(), 15, lit, new RectangleF(r.X + 8, r.Y, 20, h), StringAlignment.Near, bold: true);
            // the frequency on a little seven-segment window: lit on the preset you're on, dimmer on the others, all
            // ghost 8s on an empty one
            var win = new RectangleF(r.Right - 52, r.Y + 6, 46, h - 12);
            using (var wp = Rounded(win, 3))
            using (var wb = new SolidBrush(Color.FromArgb(0x04, 0x06, 0x08))) g.FillPath(wb, wp);
            const float segH = 12;
            string digits = p == null ? "    " : p.Mhz.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5);
            float digitsW = 4 * SevenSegment.DigitWidth(segH) + 3 * segH * 0.16f;
            SevenSegment.Draw(g, digits, win.Right - 5 - digitsW, win.Y + (win.Height - segH) / 2, segH,
                current ? lit : Color.FromArgb(150, lit), Color.FromArgb(22, lit));
        }
    }

    private void StorePreset(int i)
    {
        var eng = _c.Engine;
        string? name = null;
        if (eng != null)
        {
            name = eng.Hd.Synced ? eng.Hd.StationName : null;
            name ??= eng.Receiver.Rds.CallSign ?? eng.Receiver.Rds.ProgramService;
        }
        _c.StorePreset(i, name);
    }

    private void DrawRightSide(Graphics g)
    {
        var lit = Lit;
        // illuminated slot: the signal meter, the reception quality of what you're hearing: HD MER while HD plays, else
        // the FM stereo pilot's SNR (channel power for a mono station). It turns red with OVL when the dongle overloads.
        var slot = new RectangleF(928, 58, 34, 116);
        var eng = _c.Engine;
        bool overload = eng?.GainOptimizer.Overload == true;   // the ADC is clipping: the slot turns red
        using (var p = Rounded(slot, 8))
        {
            using (var b = new SolidBrush(Color.FromArgb(0x06, 0x07, 0x09))) g.FillPath(b, p);
            using var pen = new Pen(overload ? Alert : lit, overload ? 2.4f : 1.6f);
            g.DrawPath(pen, p);
        }
        if (overload) Label(g, "OVL", 9, Alert, new RectangleF(slot.X, slot.Y + 2, slot.Width, 12), StringAlignment.Center, bold: true);
        double q = 0;
        if (eng != null)
        {
            var hd = eng.Hd;
            var st = eng.Receiver.Stereo;
            // HD MER 3..18 dB; pilot SNR 10..45 dB (strong stations 35-50, weak ~15); power -52..-12 dBFS
            q = eng.Blender.PlayingHd ? Math.Clamp(((hd.MerLower + hd.MerUpper) / 2 - 3) / 15, 0, 1)
              : st.PilotLocked ? Math.Clamp((st.PilotSnrDb - 10) / 35, 0, 1)
              : Math.Clamp((eng.Receiver.ChannelPowerDb + 52) / 40, 0, 1);
        }
        int segs = 10;
        for (int s = 0; s < segs; s++)
        {
            bool on = s < Math.Round(q * segs);
            using var b = new SolidBrush(on ? lit : Color.FromArgb(22, lit));
            g.FillRectangle(b, slot.X + 9, slot.Bottom - 10 - (s + 1) * 9.4f, slot.Width - 18, 6.5f);
        }
        // under the slot, its legend: an antenna with radio waves (the signal meter)
        using (var pen = new Pen(Color.FromArgb(overload ? 255 : 200, overload ? Alert : lit), 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float ax = slot.X + slot.Width / 2, top = slot.Bottom + 6;
            g.DrawLine(pen, ax, top + 3, ax, top + 15);                   // mast
            g.DrawLine(pen, ax - 3.5f, top + 15, ax + 3.5f, top + 15);    // base
            using (var tip = new SolidBrush(pen.Color)) g.FillEllipse(tip, ax - 1.6f, top + 1.4f, 3.2f, 3.2f);
            foreach (float r in new[] { 5f, 9f })                         // waves either side
            {
                g.DrawArc(pen, ax - r, top + 3 - r, 2 * r, 2 * r, 150, 60);
                g.DrawArc(pen, ax - r, top + 3 - r, 2 * r, 2 * r, -30, 60);
            }
        }
        DrawTuneKnob(g, lit);
    }

    // the tuning knob, under the signal meter: mirrors the volume knob on the left, like the classic two-knob head units
    private const float TuneCx = 945, TuneCy = 233, TuneR = 21, DetentDeg = 15;   // one channel per 15 degrees

    private void DrawTuneKnob(Graphics g, Color lit)
    {
        // the knob turns with the frequency: one detent per 200 kHz channel
        long ch = (_c.Frequency - Radio808.Core.Radio.RadioEngine.FirstChannel) / Radio808.Core.Radio.RadioEngine.ChannelStep;
        double turn = (ch * DetentDeg + _tuneDragDeg) * Math.PI / 180;
        var kr = new RectangleF(TuneCx - TuneR, TuneCy - TuneR, 2 * TuneR, 2 * TuneR);
        bool hot = _hover == "tuneknob" || _tuneDrag;
        using (var b = new LinearGradientBrush(kr, Color.FromArgb(0x3A, 0x3F, 0x47), Color.FromArgb(0x0B, 0x0C, 0x0E), 70f)) g.FillEllipse(b, kr);
        // knurled edge: notches around the rim, turning with the knob
        using (var notch = new Pen(Color.FromArgb(0x10, 0x12, 0x15), 1.6f))
            for (int i = 0; i < 24; i++)
            {
                double a = turn + i * Math.PI / 12;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                g.DrawLine(notch, TuneCx + c * (TuneR - 4), TuneCy + s * (TuneR - 4), TuneCx + c * (TuneR - 0.5f), TuneCy + s * (TuneR - 0.5f));
            }
        using (var rim = new Pen(hot ? Color.FromArgb(160, lit) : Color.FromArgb(0x5A, 0x61, 0x6B), hot ? 1.8f : 1.5f)) g.DrawEllipse(rim, kr);
        var cap = RectangleF.Inflate(kr, -6, -6);
        using (var b = new LinearGradientBrush(cap, Color.FromArgb(0x23, 0x27, 0x2D), Color.FromArgb(0x14, 0x16, 0x1A), 250f)) g.FillEllipse(b, cap);
        float px = TuneCx + (float)Math.Cos(turn - Math.PI / 2) * (TuneR - 10), py = TuneCy + (float)Math.Sin(turn - Math.PI / 2) * (TuneR - 10);
        using (var dot = new SolidBrush(lit)) g.FillEllipse(dot, px - 2.5f, py - 2.5f, 5, 5);
        Label(g, "TUNE", 8.5f, lit, new RectangleF(TuneCx - 20, TuneCy + TuneR + 2, 40, 11), StringAlignment.Center, bold: true);
        _hits.Add(new Hit(RectangleF.Inflate(kr, 6, 6), "tuneknob", () => _c.Seek(1)));   // a click (no turn) seeks up
    }

    private bool _tuneDrag, _tuneMoved;
    private double _tuneStartDeg, _tuneDragDeg;   // where the drag started; how far it's turned past the last detent
    private int _tuneApplied;                     // channels stepped so far in this drag

    private static double AngleDeg(PointF p) => Math.Atan2(p.Y - TuneCy, p.X - TuneCx) * 180 / Math.PI;

    /// <summary>Turning the tuning knob: steps a channel per detent, flashing the frequency.</summary>
    private void TuneDragTo(PointF p)
    {
        if (Math.Abs(p.X - TuneCx) + Math.Abs(p.Y - TuneCy) < 7) return;   // too close to the center to read an angle
        double d = AngleDeg(p) - _tuneStartDeg;
        d = (d + 540) % 360 - 180;   // the short way round
        _tuneStartDeg = AngleDeg(p);
        _tuneDragDeg += d;
        if (Math.Abs(_tuneDragDeg) > 4) _tuneMoved = true;
        while (Math.Abs(_tuneDragDeg) >= DetentDeg)
        {
            int dir = Math.Sign(_tuneDragDeg);
            _tuneDragDeg -= dir * DetentDeg;
            _c.Step(dir);
            _tuneApplied += dir;
        }
        if (_tuneMoved) Flash($"FM {_c.Frequency / 1e6:0.0}", 0.9);
        Invalidate();
    }

    // ------------------------------------------------------------------ widgets

    private void Key(Graphics g, RectangleF r, string id, Action click, bool pattern = false, bool lit = false, Action? hold = null)
    {
        bool hot = _hover == id, down = _pressed == id;
        using (var p = Rounded(r, 6))
        {
            using (var b = new LinearGradientBrush(r, down ? Key2 : hot ? Color.FromArgb(0x36, 0x3B, 0x43) : Key1, down ? Key1 : Key2, 90f)) g.FillPath(b, p);
            if (pattern)
            {
                var st = g.Save();
                g.SetClip(p);
                using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(60, Lit), Color.Transparent);
                g.FillPath(hatch, p);
                g.Restore(st);
            }
            using var pen = new Pen(lit ? Lit : Color.FromArgb(0x05, 0x05, 0x06), lit ? 1.5f : 1.2f);
            g.DrawPath(pen, p);
        }
        if (!down)
            using (var hl = new Pen(Color.FromArgb(30, Color.White), 1)) g.DrawLine(hl, r.X + 6, r.Y + 1.5f, r.Right - 6, r.Y + 1.5f);
        _hits.Add(new Hit(r, id, click, hold));
    }

    private static void Label(Graphics g, string s, float px, Color c, RectangleF r, StringAlignment align, bool bold = false)
    {
        using var f = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        float lh = f.GetHeight(g);
        if (r.Height < lh + 2) r = new RectangleF(r.X, r.Y + r.Height / 2 - lh / 2 - 1, r.Width, lh + 2);
        g.DrawString(s, f, b, r, fmt);
    }

    private static void SeekIcon(Graphics g, RectangleF r, int dir, Color c)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = 6;
        using var b = new SolidBrush(c);
        for (int k = 0; k < 2; k++)
        {
            float bx = cx + dir * (k * s - s);
            g.FillPolygon(b, new[] { new PointF(bx, cy - s), new PointF(bx + dir * s, cy), new PointF(bx, cy + s) });
        }
        g.FillRectangle(b, dir > 0 ? cx + s : cx - s - 2, cy - s, 2, 2 * s);
    }

    private static void SpeakerIcon(Graphics g, RectangleF r, bool muted, Color c)
    {
        float cx = r.X + r.Width / 2 - 5, cy = r.Y + r.Height / 2;
        using var b = new SolidBrush(c);
        g.FillPolygon(b, new[] { new PointF(cx - 9, cy - 5), new PointF(cx - 3, cy - 5), new PointF(cx + 4, cy - 12), new PointF(cx + 4, cy + 12), new PointF(cx - 3, cy + 5), new PointF(cx - 9, cy + 5) });
        using var pen = new Pen(muted ? Alert : c, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (muted) { g.DrawLine(pen, cx + 10, cy - 6, cx + 20, cy + 6); g.DrawLine(pen, cx + 20, cy - 6, cx + 10, cy + 6); }
        else { g.DrawArc(pen, cx - 1, cy - 7, 14, 14, -50, 100); g.DrawArc(pen, cx - 6, cy - 13, 26, 26, -50, 100); }
    }

    private static void PowerIcon(Graphics g, float cx, float cy, float r, Color c)
    {
        using var pen = new Pen(c, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -60, 300);
        g.DrawLine(pen, cx, cy - r - 2, cx, cy);
    }

    private static void BulbIcon(Graphics g, float cx, float cy, Color c)
    {
        using var pen = new Pen(c, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, cx - 9, cy - 15, 18, 18);
        g.DrawLine(pen, cx - 5, cy + 7, cx + 5, cy + 7);
        g.DrawLine(pen, cx - 4, cy + 11, cx + 4, cy + 11);
        using var b = new SolidBrush(Color.FromArgb(90, c));
        g.FillEllipse(b, cx - 6, cy - 12, 12, 12);
    }

    internal static GraphicsPath Rounded(RectangleF r, float radius)
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

    // ------------------------------------------------------------------ mouse

    private PointF ToDesign(Point p) => new((p.X - _ox) / _scale, (p.Y - _oy) / _scale);
    private Hit? HitAt(PointF p) => _hits.LastOrDefault(h => h.R.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = ToDesign(e.Location);
        if (_tuneDrag) { TuneDragTo(p); return; }
        if (_knobDrag)
        {
            float dy = _knobStart.Y - p.Y;
            if (Math.Abs(dy) > 3) _knobMoved = true;
            if (_knobMoved) SetVolume(_knobStartVol + dy / 150f);
            return;
        }
        var h = HitAt(p)?.Id;
        if (_hoverHiddenAt is Point hp && Math.Abs(e.X - hp.X) + Math.Abs(e.Y - hp.Y) < 4) return;   // not moved since the click
        _hoverHiddenAt = null;
        float? hx = h == "tune" ? p.X : null;
        if (hx != _nerd.HoverX) { _nerd.HoverX = hx; if (PanelLive) InvalidateDesign(_nerd.FastRects); else Invalidate(); }
        if (h != _hover)
        {
            _hover = h;
            Cursor = h == "tune" ? Cursors.Cross : h != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private void SetVolume(float v)
    {
        _c.SetVolume(v);
        Flash($"VOL {Math.Round(_c.Settings.Volume * 40):0}", 1.0);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = null; _nerd.HoverX = null; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var p = ToDesign(e.Location);
        var h = HitAt(p);
        if (e.Button == MouseButtons.Right)
        {
            if (h?.Hold != null) { h.Hold(); Invalidate(); return; }
            MenuRequested?.Invoke(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (h == null) { DragRequested?.Invoke(); return; }   // bare faceplate: move the window
        _pressed = h.Id;
        _holdFired = false;
        if (h.Id == "knob") { _knobDrag = true; _knobMoved = false; _knobStart = p; _knobStartVol = _c.Settings.Volume; }
        else if (h.Id == "tuneknob") { _tuneDrag = true; _tuneMoved = false; _tuneStartDeg = AngleDeg(p); _tuneDragDeg = 0; _tuneApplied = 0; }
        else if (h.Hold != null) { _holdAction = h.Hold; _hold.Start(); }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _hold.Stop();
        var p = ToDesign(e.Location);
        if (_knobDrag)
        {
            _knobDrag = false;
            if (!_knobMoved) { _c.ToggleMute(); Flash(_c.Settings.Muted ? "MUTE" : "MUTE OFF", 1.0); }
        }
        else if (_tuneDrag)
        {
            _tuneDrag = false;
            _tuneDragDeg = 0;   // settle into the detent
            if (!_tuneMoved && HitAt(p)?.Id == "tuneknob") _c.Seek(1);   // a click without turning seeks up
        }
        else if (e.Button == MouseButtons.Left && !_holdFired)
        {
            var h = HitAt(p);
            if (h != null && h.Id == _pressed)
            {
                if (h.Id == "tune")
                {
                    long hz = _nerd.FrequencyAt(p.X);
                    _nerd.BeginTune(hz);
                    _c.Tune(hz);
                    // the highlight now travels with the station; hide the hover until the mouse moves
                    _nerd.HoverX = null;
                    _hoverHiddenAt = e.Location;
                    _fastPaint.Start();
                }
                else h.Click?.Invoke();
            }
        }
        _pressed = null;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var p = ToDesign(e.Location);
        if (_open) { _c.Step(Math.Sign(e.Delta)); Invalidate(); return; }   // panel open: the wheel tunes
        if (p.X < 268) SetVolume(_c.Settings.Volume + Math.Sign(e.Delta) * 0.025f);   // over the knob side: volume
        else _c.Step(Math.Sign(e.Delta));                                              // over the display: tune
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _hold.Dispose(); _art?.Dispose(); _pacer.Dispose(); _chassisBg?.Dispose(); }
        base.Dispose(disposing);
    }
}
