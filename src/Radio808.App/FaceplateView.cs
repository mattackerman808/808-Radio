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
        // marquee: hold 2 s at the start, then one character every 300 ms, a gap, and around again
        int len = _mainCols.Count / DotMatrix.CellCols;
        if (len > MainCells)
        {
            _scrollTicks++;
            int start = 20, every = 3;
            if (_scrollTicks > start && (_scrollTicks - start) % every == 0)
            {
                _scrollChars++;
                if (_scrollChars > len + 3) { _scrollChars = 0; _scrollTicks = 0; }
            }
        }
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

        // BAND / DISP
        Label(g, "BAND", 10, lit, new RectangleF(142, 196, 52, 14), StringAlignment.Center, bold: true);
        Label(g, "DISP", 10, lit, new RectangleF(200, 196, 52, 14), StringAlignment.Center, bold: true);
        Key(g, new RectangleF(142, 212, 52, 22), "band", NextProgram);
        Key(g, new RectangleF(200, 212, 52, 22), "disp", () =>
        {
            _c.Settings.DisplayMode = (_c.Settings.DisplayMode + 1) % 3;
            Flash(_c.Settings.DisplayMode switch { 0 => "NOW PLAYING", 1 => "STATION", _ => "FREQUENCY" });
        });
    }

    public void NextProgram()
    {
        var eng = _c.Engine;
        if (eng == null || eng.Hd.Programs.Count == 0) { Flash("NO HD"); return; }
        var progs = eng.Hd.Programs.Keys.ToList();
        int i = progs.IndexOf(eng.Program);
        var p = progs[(i + 1) % progs.Count];
        _c.SetProgram(p);
        Flash($"HD{p + 1}");
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

        // line 2: band + frequency, indicators, signal, clock
        string band = playingHd && eng != null ? $"HD{eng.Program + 1}" : "FM";
        var l2 = DotMatrix.Columns($"{band,-4}{mhz,5}");
        DotMatrix.Draw(g, l2, 0, 292, 140, 3.1f, 9, lit, ghost, glow: false);
        float x = 470;
        x = Indicator(g, x, 138, "HD", synced, playingHd, lit);
        // status lights are filled when on (HD alone also has an outlined "detected, not playing yet" state)
        x = Indicator(g, x, 138, "DGTL", playingHd, playingHd, lit);
        bool stereo = eng != null && (playingHd || eng.Receiver.Stereo.PilotLocked && eng.Receiver.Stereo.Blend > 0.5f);
        x = Indicator(g, x, 138, "ST", stereo, stereo, lit);
        bool rdsOn = rds?.Synced == true;
        x = Indicator(g, x, 138, "RDS", rdsOn, rdsOn, lit);
        int preset = _c.Settings.Presets.FindIndex(p => p != null && Math.Abs(p.Mhz * 1e6 - freq) < 50_000);
        if (preset >= 0) x = Indicator(g, x, 138, $"P{preset + 1}", true, true, lit);
        // signal bars
        int bars = eng == null ? 0 : Math.Clamp((int)Math.Round((eng.Receiver.ChannelPowerDb + 52) / 8), 0, 5);
        for (int i = 0; i < 5; i++)
        {
            float bh = 4 + i * 3;
            using var b = new SolidBrush(i < bars ? lit : ghost);
            g.FillRectangle(b, 664 + i * 6, 160 - bh, 4, bh);
        }
        var clock = DotMatrix.Columns(DateTime.Now.ToString("H:mm").PadLeft(5));
        DotMatrix.Draw(g, clock, 0, 700, 140, 3.1f, 5, lit, ghost, glow: false);   // ends at ~790, left of the art square

        // line 3: HD programs, maps, alerts
        x = 292;
        if (hd != null && synced)
            foreach (var p in hd.Programs.Keys)
            {
                uint prog = p;
                bool sel = eng != null && p == eng.Program;
                // lit only while HD is what you hear; found-but-not-playing (weak, analog only, retrying) stays dim
                float nx = Indicator(g, x, 172, $"HD{p + 1}", playingHd, sel && playingHd, lit);
                _hits.Add(new Hit(new RectangleF(x - 2, 168, nx - x, 22), "prog" + p, () => { _c.SetProgram(prog); Flash($"HD{prog + 1}"); }));
                x = nx;
            }
        if (hd?.WeatherMap != null)
        {
            float nx = Indicator(g, x + 8, 172, "WX", true, false, lit);
            _hits.Add(new Hit(new RectangleF(x + 6, 168, nx - x - 6, 22), "wx", () => MapRequested?.Invoke("weather")));
            x = nx;
        }
        if (hd != null && hd.TrafficTiles.Any(t => t != null))
        {
            float nx = Indicator(g, x + 8, 172, "TRF", true, false, lit);
            _hits.Add(new Hit(new RectangleF(x + 6, 168, nx - x - 6, 22), "trf", () => MapRequested?.Invoke("traffic")));
            x = nx;
        }
        if (_c.Seeking) Indicator(g, x + 8, 172, "SEEK", true, true, lit);
        else if (eng != null && !_c.Settings.ForceAnalog && eng.HdTooWeak)
            Indicator(g, x + 8, 172, "HD WEAK", true, false, lit);   // found (name, programs), but too weak to play
        else if (eng != null && synced && !playingHd && !_c.Settings.ForceAnalog && eng.Blender.RetryIn > 0.5)
            Indicator(g, x + 8, 172, $"HD IN {eng.Blender.RetryIn:0}S", true, false, lit);

        // art square: album art / station logo (click to enlarge), else the audio spectrum analyzer
        var sq = AnalyzerArea;
        Image? art = synced && _c.Settings.ShowAlbumArt ? Decode(hd?.AlbumArt ?? hd?.StationLogo) : null;
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
        using var on = new SolidBrush(Color.FromArgb(220, lit));
        using var peak = new SolidBrush(lit);
        using var off = new SolidBrush(ghost);
        for (int b = 0; b < Bars; b++)
        {
            int litSegs = _specValid ? (int)Math.Round(_bars[b] * segs) : 0;
            int peakSeg = _specValid ? (int)Math.Round(_peaks[b] * segs) - 1 : -1;   // the held peak, above the bar
            for (int s = 0; s < segs; s++)
                g.FillRectangle(s < litSegs ? on : s == peakSeg ? peak : off, sq.X + b * bw + 1, sq.Bottom - (s + 1) * sh + 1, bw - 2, sh - 2);
        }
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

        string? station = synced ? hd?.StationName : null;
        station ??= rds?.ProgramService ?? rds?.CallSign;
        string? song = null;
        if (synced && hd?.Title != null) song = hd.Artist != null ? $"{hd.Title} - {hd.Artist}" : hd.Title;
        song ??= rds?.RadioText;

        string t = _c.Settings.DisplayMode switch
        {
            0 => song ?? station ?? $"FM {mhz}",
            1 => station ?? $"FM {mhz}",
            _ => $"FM {mhz}",
        };
        return t.ToUpperInvariant();
    }

    /// <summary>A small segment-style indicator: outlined when on, filled when active, ghosted when off.</summary>
    private float Indicator(Graphics g, float x, float y, string text, bool on, bool active, Color lit)
    {
        using var f = new Font("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        float w = g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width + 10;
        var r = new RectangleF(x, y, w, 16);
        var c = on ? lit : Color.FromArgb(28, lit);
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
        for (int i = 0; i < AppSettings.PresetCount; i++)
        {
            var r = new RectangleF(x0 + i * (w + gap), y, w, h);
            int idx = i;
            var p = _c.Settings.Presets[i];
            Key(g, r, "preset" + i, () =>
            {
                if (_c.Settings.Presets[idx] == null) { Flash($"HOLD {idx + 1} TO SAVE"); return; }
                _c.RecallPreset(idx);
            }, hold: () => { StorePreset(idx); Flash($"P{idx + 1} SAVED"); });
            Label(g, (i + 1).ToString(), 15, lit, new RectangleF(r.X + 8, r.Y, 20, h), StringAlignment.Near, bold: true);
            if (p != null) Label(g, p.Mhz.ToString("0.0"), 10.5f, Grey, new RectangleF(r.X + 26, r.Y, r.Width - 32, h), StringAlignment.Far);
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
        // illuminated slot: vertical signal-quality meter (HD MER when synced, else FM channel power)
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
            q = hd.Synced ? Math.Clamp(((hd.MerLower + hd.MerUpper) / 2 - 3) / 15, 0, 1)
                          : Math.Clamp((eng.Receiver.ChannelPowerDb + 52) / 40, 0, 1);
        }
        int segs = 10;
        for (int s = 0; s < segs; s++)
        {
            bool on = s < Math.Round(q * segs);
            using var b = new SolidBrush(on ? lit : Color.FromArgb(22, lit));
            g.FillRectangle(b, slot.X + 9, slot.Bottom - 10 - (s + 1) * 9.4f, slot.Width - 18, 6.5f);
        }
        // jack
        using (var b = new SolidBrush(Color.FromArgb(0x04, 0x04, 0x05))) g.FillEllipse(b, 934, 200, 22, 22);
        using (var pen = new Pen(Color.FromArgb(0x50, 0x56, 0x5F), 2)) g.DrawEllipse(pen, 934, 200, 22, 22);
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
