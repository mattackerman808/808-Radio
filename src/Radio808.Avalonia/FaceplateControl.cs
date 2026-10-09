using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Radio808.Avalonia.Drawing;
using Radio808.Core.Dsp;
using Radio808.Core.Hd;
using Radio808.Core.Radio;
using Radio808.Shared;
using Radio808.Shared.Display;
using static Radio808.Avalonia.Drawing.G;

namespace Radio808.Avalonia;

/// <summary>
/// The radio as a single-DIN car stereo: glossy faceplate, illuminated keys, a volume knob with a light ring, and a
/// dot-matrix display. Drawn on a 1000 x 300 design canvas that scales to the window; everything clickable registers
/// a hit rectangle while painting. A port of the Windows app's FaceplateView (the instrument panel behind the
/// faceplate is not here yet).
/// </summary>
internal sealed class FaceplateControl : Control
{
    public const double W = 1000, ClosedH = 300, OpenH = 570;
    private double H => _open || _anim > 0 ? OpenH : ClosedH;

    // flip-down faceplate: _anim 0 = closed, 1 = fully open
    private bool _open;
    private double _anim;
    private readonly DispatcherTimer _animTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly NerdPanel _nerd;
    private readonly SpectrumView _spectrum;
    private readonly AnalyzerView _analyzerView;
    /// <summary>The window should start a resize drag from this edge (the pointer is on the faceplate's edge band).</summary>
    public event Action<WindowEdge, PointerPressedEventArgs>? ResizeRequested;

    /// <summary>Raised before opening (true) and after closing (false): the window should change height.</summary>
    public event Action<bool>? OpenLayout;
    public bool IsOpen => _open;
    /// <summary>Current design canvas height (the window keeps W:H).</summary>
    public double DesignHeight => H;
    /// <summary>The window's shape for the current state, in design coordinates.</summary>
    public Rect CurrentOutline => H > ClosedH ? new Rect(12, 14, 976, 546) : Outline;
    private static readonly Rect PanelArea = new(44, 26, 912, 490);
    private static readonly Color PanelBackground = Rgb(0x05, 0x08, 0x0A);
    private static readonly Color Background = Rgb(0x05, 0x06, 0x07);
    private const int MainCells = 13;
    private const int Line2Cells = 26;   // the small dot-matrix line, full width (to the art square)

    public static readonly (string Name, Color Color)[] Illuminations =
    {
        ("Cyan", Rgb(0x2B, 0xE4, 0xF2)), ("Amber", Rgb(0xFF, 0xA8, 0x26)),
        ("Green", Rgb(0x5C, 0xFF, 0x86)), ("Red", Rgb(0xFF, 0x45, 0x45)),
        ("Blue", Rgb(0x4A, 0x8C, 0xFF)), ("White", Rgb(0xE6, 0xF2, 0xFF)),
    };

    private static readonly Color Body1 = Rgb(0x30, 0x34, 0x3B), Body2 = Rgb(0x0D, 0x0F, 0x12);
    private static readonly Color Face1 = Rgb(0x1A, 0x1D, 0x22), Face2 = Rgb(0x08, 0x09, 0x0B);
    private static readonly Color Key1 = Rgb(0x2A, 0x2E, 0x35), Key2 = Rgb(0x0E, 0x10, 0x13);
    private static readonly Color Silver = Rgb(0xB8, 0xBE, 0xC6), Grey = Rgb(0x6E, 0x76, 0x80);
    private static readonly Color Alert = Rgb(0xFF, 0x4A, 0x4A);
    private static readonly Color White = Rgb(255, 255, 255), Black = Rgb(0, 0, 0);

    private readonly RadioController _c;
    private double _scale = 1, _ox, _oy;

    /// <param name="Hold">After the key is held 0.65 s.</param>
    /// <param name="HoldLong">After it's held 1.8 s longer still (presets: hold to save, keep holding to clear).</param>
    private sealed record Hit(Rect R, string Id, Action? Click, Action? Hold = null, Action? HoldLong = null);
    private readonly List<Hit> _hits = new();
    private string? _hover, _pressed;
    private bool _holdFired, _knobDrag, _knobMoved, _edgeDrag;
    private Point _knobStart;
    private float _knobStartVol;
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(650), HoldLongTime = TimeSpan.FromMilliseconds(1800);
    private readonly DispatcherTimer _hold = new() { Interval = HoldTime };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Action? _holdAction, _holdLongAction;

    // display state
    private string _mainText = "";
    private List<bool[]> _mainCols = new();
    private int _scrollChars, _scrollTicks;
    private string? _subText;
    private List<bool[]> _subCols = new();
    private int _subScroll, _subTicks, _subCells = 10;
    private const int HdCells = 12;
    private string? _hdKey;
    private int _hdPage, _hdPageCount, _hdPageLen, _hdTicks, _hdScroll;
    private string? _flash;
    private DateTime _flashUntil;

    private byte[]? _artKey;
    private Bitmap? _art;

    private const int Bars = 16;
    private readonly float[] _bars = new float[Bars];
    private bool _specValid;

    public event Action<string>? MapRequested;
    public event Action<PointerPressedEventArgs>? MenuRequested;
    /// <summary>The user pressed on bare faceplate: the window should start moving.</summary>
    public event Action<PointerPressedEventArgs>? DragRequested;
    public event Action? MinimizeRequested, CloseRequested;
    /// <summary>The album art / logo was clicked: show it bigger, popping out of this design rectangle.</summary>
    public event Action<Bitmap, Rect>? ArtRequested;

    /// <summary>The faceplate's outline in design coordinates (the window's shape).</summary>
    public static readonly Rect Outline = new(12, 14, 976, 272);
    public const double OutlineRadius = 26;

    public FaceplateControl(RadioController c)
    {
        _c = c;
        ClipToBounds = false;
        Focusable = true;
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            _holdFired = true;
            _holdAction?.Invoke();
            if (_holdLongAction != null)   // still held: the second stage comes after a longer wait
            {
                _holdAction = _holdLongAction;
                _holdLongAction = null;
                _hold.Interval = HoldLongTime;
                _hold.Start();
            }
            InvalidateVisual();
        };
        _tick.Tick += (_, _) => Tick();
        _frames.Tick += (_, _) => Frame();
        c.Changed += () => { if (_c.ServerMode && _open && !_animTimer.IsEnabled) ToggleOpen(); InvalidateVisual(); };
        c.Message += m => Flash(m, 2.5);
        _nerd = new NerdPanel(c);
        _spectrum = new SpectrumView(this) { IsHitTestVisible = false, ClipToBounds = true, IsVisible = false };
        VisualChildren.Add(_spectrum);
        LogicalChildren.Add(_spectrum);
        _analyzerView = new AnalyzerView(this) { IsHitTestVisible = false };
        VisualChildren.Add(_analyzerView);
        LogicalChildren.Add(_analyzerView);
        _animTimer.Tick += (_, _) =>
        {
            double step = 15.0 / 380;
            _anim = _open ? Math.Min(1, _anim + step) : Math.Max(0, _anim - step);
            _spectrum.IsVisible = _open && _anim > 0.6;
            if (_open && _anim >= 1 || !_open && _anim <= 0)
            {
                _animTimer.Stop();
                if (!_open) OpenLayout?.Invoke(false);   // fully folded back up: shrink the window
                StartFrames();
            }
            InvalidateVisual();
        };
    }

    public void Start() { _tick.Start(); StartFrames(); }
    public void Stop() { _tick.Stop(); _frames.Stop(); _hold.Stop(); _animTimer.Stop(); }

    /// <summary>Flips the faceplate down to reveal the instrument panel, or back up.</summary>
    public void ToggleOpen()
    {
        if (_animTimer.IsEnabled) return;
        if (_c.ServerMode && !_open) return;   // a server has no instrument panel
        _open = !_open;
        if (_open) OpenLayout?.Invoke(true);   // grow the window first, then fold the faceplate down
        _spectrum.IsVisible = false;
        _animTimer.Start();
    }

    /// <summary>(Re)starts the live frames at the frame rate setting (the panel's spectrum, or the faceplate's analyzer).</summary>
    public void StartFrames()
    {
        int fps = _c.Settings.PanelFps;   // 0 = every display refresh: 60 here
        _frames.Interval = TimeSpan.FromMilliseconds(fps > 0 ? 1000.0 / Math.Clamp(fps, 10, 120) : 16);
        _frames.Start();
    }

    private bool PanelLive => _open && _anim >= 1;

    private void Frame()
    {
        if (_open)
        {
            _nerd.FrameTick(_c.Engine);
            if (_spectrum.IsVisible) _spectrum.InvalidateVisual();
            InvalidateVisual();
        }
        else
        {
            // closed: only the analyzer square moves at the frame rate; the rest of the faceplate repaints on the tick
            PullAudio();
            if (_anim > 0) InvalidateVisual(); else _analyzerView.InvalidateVisual();
        }
    }

    /// <summary>The analyzer is drawn by its own visual while the faceplate is up and still (not while it folds).</summary>
    private bool AnalyzerLive => !_open && _anim <= 0 && _analyzerShown;
    private bool _analyzerShown;

    private Color Lit => Illuminations[Math.Clamp(_c.Settings.Illumination, 0, Illuminations.Length - 1)].Color;

    /// <summary>Shows a short message on the display for a moment (e.g. "VOL 18", "P3 SAVED").</summary>
    public void Flash(string text, double seconds = 1.6)
    {
        _flash = text.ToUpperInvariant();
        _flashUntil = DateTime.UtcNow.AddSeconds(seconds);
        InvalidateVisual();
    }

    /// <summary>Called by the 100 ms tick (the controller's PpmTick rides on it too).</summary>
    public event Action? Ticked;

    // ------------------------------------------------------------------ periodic update (100 ms)

    private void Tick()
    {
        if (_open) _nerd.Tick(_c.Engine);
        Marquee(_mainCols.Count / DotMatrix.CellCols, MainCells, ref _scrollChars, ref _scrollTicks);
        Marquee(_subCols.Count / DotMatrix.CellCols, _subCells, ref _subScroll, ref _subTicks);
        HdPages();
        Ticked?.Invoke();
        InvalidateVisual();
    }

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
        bool silent = _c.Settings.Muted;
        for (int b = 0; b < Bars; b++)
        {
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

    // The design canvas (1000 x 300, or 1000 x 570 open) is scaled to the window with a render transform, so
    // everything, children included, is drawn and hit in design coordinates. (A transform pushed on the drawing
    // context would work too, but a clip pushed there breaks later text on Avalonia 12 / Skia, so the clipped
    // spectrum is a child visual, and children need the transform on the visual.)
    protected override Size MeasureOverride(Size availableSize)
    {
        _spectrum.Measure(availableSize);
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : W, double.IsFinite(availableSize.Height) ? availableSize.Height : H);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _scale = Math.Max(1e-6, Math.Min(finalSize.Width / W, finalSize.Height / H));
        _ox = (finalSize.Width - W * _scale) / 2;
        _oy = (finalSize.Height - H * _scale) / 2;
        RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        RenderTransform = new MatrixTransform(Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_ox, _oy));
        _nerd.Layout(PanelArea);
        _spectrum.Arrange(_nerd.SpectrumClip);
        _analyzerView.Arrange(Inflate(AnalyzerArea, 1, 1));
        return finalSize;
    }

    public override void Render(DrawingContext g)
    {
        _hits.Clear();
        SetDotDevice();
        if (H > ClosedH)
        {
            DrawChassisBackground(g);
            DrawChassis(g);
            if (_anim < 1)
            {
                // the faceplate folding down: it slides to the hinge at the bottom and flattens toward edge-on
                double t = _anim * _anim * (3 - 2 * _anim);   // smoothstep
                double top = 524 * t, height = ClosedH * (1 - t) + 34 * t;
                using (g.PushTransform(Matrix.CreateScale(1, height / ClosedH) * Matrix.CreateTranslation(0, top)))
                {
                    DrawFaceplate(g);
                    g.FillRounded(Brush(Argb((byte)(170 * t), 0, 0, 0)), Outline, OutlineRadius);
                }
                _hits.Clear();   // nothing is clickable mid-flip
            }
        }
        else DrawFaceplate(g);
        // the analyzer's own visual stays on screen with its last frame unless it is hidden when it stops being live
        // (the faceplate folds down, art or MUTE takes the square): rendering it empty does not clear it. Visibility
        // can't change inside the render pass, so it changes right after.
        bool live = AnalyzerLive;
        if (_analyzerView.IsVisible != live) Dispatcher.UIThread.Post(() => _analyzerView.IsVisible = live);
    }

    /// <summary>Tells the dot matrix where device pixels are: the design scale under the window's render scaling.</summary>
    /// <summary>Development: the render scaling the snapshot renders at, in place of the window's.</summary>
    public static double? RenderScalingOverride;

    private void SetDotDevice()
    {
        double rs = RenderScalingOverride ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        DotMatrix.SetDevice(_scale * rs, _ox * rs, _oy * rs);
    }

    private void DrawFaceplate(DrawingContext g)
    {
        DrawBody(g);
        if (_c.ServerMode) { DrawServerPanel(g); return; }   // not a radio now: the dongle is being served
        DrawKeyBlock(g);
        DrawDisplay(g);
        DrawKeyStrip(g);
        DrawRightSide(g);
    }

    /// <summary>
    /// Server mode in place of the radio: one wide display saying what's being served to whom, and a key to stop.
    /// The tuning, presets and knobs are gone because the client does the tuning.
    /// </summary>
    private void DrawServerPanel(DrawingContext g)
    {
        var lit = Lit;
        var ghost = lit.With(20);
        var srv = _c.Server;
        var glass = R(40, 56, 920, 150);
        g.FillRounded(Gradient(glass, Rgb(0x07, 0x0C, 0x0F), Rgb(0x02, 0x04, 0x05), 90), glass, 7);
        g.DrawRounded(Pen(Rgb(0x22, 0x28, 0x2F), 1.5), glass, 7);

        // the big line: what's happening
        const int cells = 23;
        string main = srv == null ? (_c.Error ?? "SERVER MODE") : srv.Client != null ? "SERVING" : "WAITING FOR A RADIO";
        main = main.ToUpperInvariant();
        if (main.Length > cells) main = main[..cells];
        DotMatrix.Draw(g, DotMatrix.Columns(main), 0, 60, 70, 6.3, cells, srv == null && _c.Error != null ? Alert : lit, ghost);

        // the small line: the name this dongle is advertised under, and the port
        string sub = srv == null ? "" : $"{srv.ServiceName}   PORT {srv.Port}".ToUpperInvariant();
        DotMatrix.Draw(g, DotMatrix.Columns(sub), 0, 60, 128, 3.1, 46, lit, ghost, glow: false);

        // the lights and figures
        double x = 60, y = 168;
        bool bonjour = srv?.Advertised == true, serving = srv?.Client != null;
        x = Indicator(g, x, y, "BONJOUR", bonjour, bonjour, lit) + 8;
        x = Indicator(g, x, y, "CLIENT", serving, serving, lit) + 14;
        if (srv != null)
        {
            string figures = serving
                ? $"{srv.Client}     {srv.Frequency / 1e6:0.000} MHz     {srv.SampleRate / 1e6:0.00} MS/s     {srv.BytesPerSecond / 1e6:0.0} MB/s"
                : $"{srv.TunerType} dongle: {srv.Device}";
            g.Label(figures, 10, lit, R(x, y - 1, glass.Right - 16 - x, 16), Align.Near, bold: true);
        }

        // the keys: stop serving (back to a radio), and the usual color and window keys
        TextKey(g, R(40, 218, 150, 36), "stopserver", "STOP SERVING", lit, () => _c.SetServerMode(false), lit: true);
        TextKey(g, R(200, 218, 80, 36), "color", "COLOR", lit, CycleColor);
        g.Label("The dongle in this computer is shared on the network (rtl_tcp). Pick it on the other radio: Source, or the Apple TV's SETUP › SERVER.",
            9.5, Grey, R(300, 220, 650, 32), Align.Near);
    }

    /// <summary>The window's background with the faceplate open: chassis, screws, the panel's frame.</summary>
    private static void DrawChassisBackground(DrawingContext g)
    {
        var outer = new Rect(12, 14, 976, 546);
        g.FillRounded(Gradient(outer, Rgb(0x1C, 0x1F, 0x24), Rgb(0x0A, 0x0B, 0x0D), 90), outer, OutlineRadius);
        g.DrawRounded(Pen(Rgb(0x48, 0x4E, 0x57), 1.5), outer, OutlineRadius);
        foreach (var (sx, sy) in new[] { (30.0, 32.0), (970, 32), (30, 508), (970, 508) })
        {
            g.FillEllipse(Brush(Rgb(0x2C, 0x30, 0x36)), sx - 5, sy - 5, 10, 10);
            g.DrawLine(Pen(Rgb(0x0C, 0x0D, 0x0F), 1.4), sx - 3, sy, sx + 3, sy);
        }
        g.FillRounded(Brush(PanelBackground), PanelArea, 8);
        g.DrawRounded(Pen(Rgb(0x22, 0x28, 0x2F), 1.5), PanelArea, 8);
    }

    /// <summary>Open: the instrument panel and the folded faceplate's lip, over the chassis.</summary>
    private void DrawChassis(DrawingContext g)
    {
        var lit = Lit;
        if (_anim > 0.6) _nerd.Draw(new AvaloniaPanelCanvas(g), PanelArea, lit);
        if (_anim >= 1)
        {
            _hits.Add(new Hit(_nerd.TuneArea, "tune", null));
            _hits.Add(new Hit(_nerd.SpanToggle, "span", () => _nerd.Wide = !_nerd.Wide));
            var lip = new Rect(24, 524, 952, 30);
            bool hot = _hover == "closeface";
            g.FillRounded(Gradient(lip, hot ? Rgb(0x3A, 0x3F, 0x47) : Body1, Body2, 90), lip, 8);
            g.DrawRounded(Pen(Rgb(0x05, 0x05, 0x06), 1.2), lip, 8);
            g.DrawLine(Pen(White.With(40), 1), lip.X + 10, lip.Y + 1.5, lip.Right - 10, lip.Y + 1.5);
            g.Label("▲  CLOSE FACEPLATE", 11, lit, lip, Align.Center, bold: true);
            _hits.Add(new Hit(lip, "closeface", ToggleOpen));
            WindowKey(g, R(924, 531, 18, 16), "min", () => MinimizeRequested?.Invoke(), close: false);
            WindowKey(g, R(948, 531, 18, 16), "close", () => CloseRequested?.Invoke(), close: true);
        }
    }

    /// <summary>The faceplate's audio analyzer square: its own visual, so its 60 fps frames repaint nothing else.</summary>
    private sealed class AnalyzerView : Control
    {
        private readonly FaceplateControl _owner;
        public AnalyzerView(FaceplateControl owner) => _owner = owner;
        public override void Render(DrawingContext g)
        {
            if (!_owner.AnalyzerLive) return;
            _owner.SetDotDevice();
            using (g.PushTransform(Matrix.CreateTranslation(-Bounds.X, -Bounds.Y)))
            {
                // the glass under the square, then the bars (the parent leaves this square alone meanwhile)
                g.FillRectangle(Gradient(Glass, Rgb(0x07, 0x0C, 0x0F), Rgb(0x02, 0x04, 0x05), 90), Inflate(AnalyzerArea, 1, 1));
                _owner.DrawAnalyzer(g, _owner.Lit);
            }
        }
    }

    /// <summary>The sliding spectrum and waterfall: its own visual, clipped to its bounds.</summary>
    private sealed class SpectrumView : Control
    {
        private readonly FaceplateControl _owner;
        public SpectrumView(FaceplateControl owner)
        {
            _owner = owner;
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        }
        public override void Render(DrawingContext g)
        {
            // the content is in design coordinates; this visual's origin is the clip rectangle's corner
            var cv = new AvaloniaPanelCanvas(g, -Bounds.X, -Bounds.Y);
            _owner._nerd.DrawSpectrumContent(cv, _owner.Lit);
        }
    }

    private void DrawBody(DrawingContext g)
    {
        var outer = Outline;
        g.FillRounded(Gradient(outer, Body1, Body2, 90), outer, OutlineRadius);
        g.DrawRounded(Pen(Rgb(0x48, 0x4E, 0x57), 1.5), outer, OutlineRadius);
        // gloss highlight along the top edge
        g.DrawLine(Pen(White.With(40), 1.2), 40, 17, 960, 17);
        var face = R(24, 26, 952, 248);
        g.FillRounded(Gradient(face, Face1, Face2, 90), face, 20);
        g.DrawRounded(Pen(Rgb(0x05, 0x05, 0x06), 2), face, 20);
        // brand at the left, the obligatory 90s badge in italic chrome at the right before the window keys
        BrandMark.Draw(g, 40, 32, 20, Silver);
        var badge = new FormattedText("DIGITAL", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(G.Ui, FontStyle.Italic, FontWeight.Bold), 11.5, Brush(Silver));
        g.DrawText(badge, new Point(912 - badge.Width, 34));

        // window keys (the window has no frame): minimize and close, top right
        WindowKey(g, R(926, 34, 18, 16), "min", () => MinimizeRequested?.Invoke(), close: false);
        WindowKey(g, R(948, 34, 18, 16), "close", () => CloseRequested?.Invoke(), close: true);
    }

    private void WindowKey(DrawingContext g, Rect r, string id, Action click, bool close)
    {
        bool hot = _hover == id;
        g.FillRounded(Brush(hot ? (close ? Rgb(0xC4, 0x2B, 0x2B) : Rgb(0x3A, 0x40, 0x48)) : Rgb(0x16, 0x18, 0x1C)), r, 4);
        g.DrawRounded(Pen(Rgb(0x05, 0x05, 0x06), 1), r, 4);
        var pen2 = Pen(hot ? White : Grey, 1.6, round: true);
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        if (close) { g.DrawLine(pen2, cx - 3.5, cy - 3.5, cx + 3.5, cy + 3.5); g.DrawLine(pen2, cx + 3.5, cy - 3.5, cx - 3.5, cy + 3.5); }
        else g.DrawLine(pen2, cx - 4, cy + 1, cx + 4, cy + 1);
        _hits.Add(new Hit(r, id, click));
    }

    // the key block left of the display: nine keys of one size in three rows, grouped by what they do
    private const double KeyW = 70, KeyH = 56;
    private static Rect KeyAt(int col, int row) => R(36 + col * 78, 57 + row * 66, KeyW, KeyH);

    private void DrawKeyBlock(DrawingContext g)
    {
        var lit = Lit;
        // row 1: the panel, display and looks (OPEN and MUTE swap rows, so OPEN is top left)
        var mute = KeyAt(0, 1);
        Key(g, mute, "mute", _c.ToggleMute);
        SpeakerIcon(g, R(mute.X, mute.Y + 6, mute.Width, 28), _c.Settings.Muted, lit);
        g.Label("MUTE", 9.5, lit, R(mute.X, mute.Y + 39, mute.Width, 12), Align.Center, bold: true);
        TextKey(g, KeyAt(1, 0), "disp", "DISP", lit, NextDisplay);
        var col = KeyAt(2, 0);
        Key(g, col, "color", CycleColor);
        BulbIcon(g, col.X + col.Width / 2, col.Y + 23, lit);
        g.Label("COLOR", 9.5, lit, R(col.X, col.Y + 39, col.Width, 12), Align.Center, bold: true);
        // row 2: sound and the maps
        OpenKey(g, KeyAt(0, 0), lit);
        MapKey(g, KeyAt(1, 1), "wxkey", "WX", "weather");
        MapKey(g, KeyAt(2, 1), "trfkey", "TRAFFIC", "traffic");
        // row 3: HD, ending beside the seek keys
        var src = KeyAt(0, 2);
        Key(g, src, "src", () =>
        {
            _c.SetForceAnalog(!_c.Settings.ForceAnalog);
            Flash(_c.Settings.ForceAnalog ? "SOURCE FM" : "SOURCE HD");
        });
        PowerIcon(g, src.X + src.Width / 2, src.Y + 20, 8, lit);
        g.Label("SRC", 9.5, lit, R(src.X, src.Y + 39, src.Width, 12), Align.Center, bold: true);
        TwoLineKey(g, KeyAt(1, 2), "band", "HD", "CH", lit, NextProgram);
        HdSeekKey(g, KeyAt(2, 2), lit);
    }

    /// <summary>A key with one centered word.</summary>
    private void TextKey(DrawingContext g, Rect r, string id, string label, Color c, Action click, bool lit = false)
    {
        Key(g, r, id, click, lit: lit);
        g.Label(label, 11, c, R(r.X, r.Y + (r.Height - 16) / 2, r.Width, 16), Align.Center, bold: true);
    }

    /// <summary>A key with a big word over a small one (the HD keys).</summary>
    private void TwoLineKey(DrawingContext g, Rect r, string id, string top, string bottom, Color c, Action click, bool lit = false)
    {
        Key(g, r, id, click, lit: lit);
        g.Label(top, 13, c, R(r.X, r.Y + 10, r.Width, 16), Align.Center, bold: true);
        g.Label(bottom, 10, c, R(r.X, r.Y + 30, r.Width, 14), Align.Center, bold: true);
    }

    public void CycleColor()
    {
        _c.Settings.Illumination = (_c.Settings.Illumination + 1) % Illuminations.Length;
        _c.Settings.Save();
        Flash("COLOR " + Illuminations[_c.Settings.Illumination].Name);
    }

    /// <summary>A slow blink for things that are promised but not here yet: a short flash every 1.6 s.</summary>
    private static bool Blink => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond % 1600 < 400;

    /// <summary>OPEN: flips the faceplate down (where a head unit has its open/eject key).</summary>
    private void OpenKey(DrawingContext g, Rect r, Color lit)
    {
        Key(g, r, "open", ToggleOpen);
        var b = Brush(lit);
        double cx = r.X + r.Width / 2, cy = r.Y + 20;
        g.FillPolygon(b, new Point(cx - 8, cy + 2), new Point(cx + 8, cy + 2), new Point(cx, cy - 6));
        g.FillRect(b, cx - 8, cy + 5, 16, 2.2);
        g.Label(_open ? "CLOSE" : "OPEN", 9.5, lit, R(r.X, r.Y + 39, r.Width, 12), Align.Center, bold: true);
    }

    /// <summary>HD SEEK: seek stops only at HD stations while this is lit.</summary>
    private void HdSeekKey(DrawingContext g, Rect r, Color lit)
    {
        bool on = _c.Settings.SeekHd;
        TwoLineKey(g, r, "hdseek", "HD", "SEEK", on ? lit : Grey,
            () => { _c.SetSeekHd(!_c.Settings.SeekHd); Flash(_c.Settings.SeekHd ? "SEEK HD ONLY" : "SEEK ALL", 1.5); });
    }

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
            int end = _hdPageLen - HdCells;
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

    private static void Marquee(int len, int cells, ref int chars, ref int ticks) => DisplayText.Marquee(len, cells, ref chars, ref ticks);

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
        string type = hd.Programs[p] is { Length: > 0 } t ? " " + t : "";
        Flash($"HD{p + 1}/{progs.Count}{type}{(eng.HdTooWeak ? " WEAK" : "")}", 2.5);
    }

    // ------------------------------------------------------------------ the display

    private static readonly Rect AnalyzerArea = new(800, 64, 104, 104);
    private static readonly Rect Glass = new(272, 56, 640, 142);

    private void DrawDisplay(DrawingContext g)
    {
        var lit = Lit;
        var glass = Glass;
        g.FillRounded(Gradient(glass, Rgb(0x07, 0x0C, 0x0F), Rgb(0x02, 0x04, 0x05), 90), glass, 7);
        g.DrawRounded(Pen(Rgb(0x22, 0x28, 0x2F), 1.5), glass, 7);
        var ghost = lit.With(20);
        var eng = _c.Engine;
        var hd = eng?.Hd;
        var rds = eng?.Receiver.Rds;
        bool synced = hd?.Synced == true, playingHd = eng?.Blender.PlayingHd == true;
        long freq = _c.Frequency;
        string mhz = (freq / 1e6).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        // main line
        string text = MainText(eng, hd, rds, synced, mhz, out bool alert);
        if (text != _mainText)
        {
            _mainText = text;
            _mainCols = DotMatrix.Columns(text);
            _scrollChars = 0; _scrollTicks = 0;
        }
        DotMatrix.Draw(g, _mainCols, _scrollChars * DotMatrix.CellCols, 292, 76, 6.3, MainCells, alert ? Alert : lit, ghost);
        if (eng == null && !_c.Starting) _hits.Add(new Hit(R(284, 64, 500, 64), "start", () => _ = _c.StartAsync()));

        // line 2: the small dot-matrix line and a seven-segment clock at the right end, like a VFD's
        DotMatrix.Draw(g, DotMatrix.Columns(""), 0, 292, 136, 3.1, Line2Cells - 6, lit, ghost, glow: false);
        string clockText = DateTime.Now.ToString("H:mm").PadLeft(5);
        const double clockH = 24.5;
        double clockW = 4 * SevenSegment.DigitWidth(clockH) + 3 * clockH * 0.16 + clockH * 0.24;
        SevenSegment.Draw(g, clockText, 776 - clockW, 136.5, clockH, lit, ghost);

        _subCells = Line2Cells - 6;
        string sub = SubText(eng, hd, rds, synced, mhz);
        if (sub != _subText)
        {
            _subText = sub;
            _subCols = DotMatrix.Columns(sub);
            _subScroll = _subTicks = 0;
        }
        DotMatrix.Draw(g, _subCols, _subScroll * DotMatrix.CellCols, 292, 136, 3.1, _subCells, lit, ghost, glow: false);

        // line 3: the status lights
        double x = 292;
        x = Indicator(g, x, 172, "HD", synced, playingHd, lit);
        bool weak = eng != null && !_c.Settings.ForceAnalog && (eng.HdTooWeak || synced && !playingHd && eng.Blender.RetryIn > 0.5);
        x = Indicator(g, x, 172, "WEAK", weak, weak, lit, Alert) + 10;
        bool stereo = eng != null && (playingHd || eng.Receiver.Stereo.PilotLocked && eng.Receiver.Stereo.Blend > 0.5f);
        x = StereoIcon(g, x, 172, stereo, lit);
        bool rdsOn = rds?.Synced == true;
        x = Indicator(g, x, 172, "RDS", rdsOn, rdsOn, lit) + 10;
        // [HD][SEEK]: a fixed pair; both light during an HD-only seek, SEEK alone during a plain one
        bool hdSeeking = _c.Seeking && _c.Settings.SeekHd;
        x = Indicator(g, x, 172, "HD", hdSeeking, hdSeeking, lit) - 3;
        x = Indicator(g, x, 172, "SEEK", _c.Seeking, _c.Seeking, lit) + 10;
        // WX / TRAFFIC: blinking outline while the station's guide promises the images, filled once one is here
        int wxState = MapState("weather"), trfState = MapState("traffic");
        bool blink = Blink;
        double wxEnd = Indicator(g, x, 172, "WX", wxState == 2 || (wxState == 1 && blink), wxState == 2, lit);
        _hits.Add(new Hit(R(x - 2, 168, wxEnd - x, 22), "wx", () => OpenMap("weather")));
        x = wxEnd;
        double trfEnd = Indicator(g, x, 172, "TRAFFIC", trfState == 2 || (trfState == 1 && blink), trfState == 2, lit);
        _hits.Add(new Hit(R(x - 2, 168, trfEnd - x, 22), "trf", () => OpenMap("traffic")));

        // the HD programs mini matrix
        {
            const double pitch = 2.2, cellW = pitch * DotMatrix.CellCols;
            double mx = trfEnd + 14, my = 171;
            if (hd != null && synced && hd.Programs.Count > 0 && eng != null)
            {
                var dim = lit.With(110);
                bool tooWeak = eng.HdTooWeak, blinkOn = DateTime.UtcNow.Millisecond < 500;
                Color ColorOf(uint p) => p != eng.Program ? dim
                    : playingHd ? lit : !tooWeak && !_c.Settings.ForceAnalog && blinkOn ? lit : dim;
                var progs = hd.Programs.Keys.ToList();
                int at = Math.Max(0, progs.IndexOf(eng.Program));
                var withFormat = Enumerable.Range(0, progs.Count).Select(k => progs[(at + k) % progs.Count])
                    .Where(p => !string.IsNullOrWhiteSpace(hd.Programs[p])).ToList();
                string key = string.Join(",", progs) + "|" + eng.Program + "|" + string.Join(",", withFormat.Select(p => hd.Programs[p]));
                if (key != _hdKey) { _hdKey = key; _hdPage = _hdTicks = _hdScroll = 0; }
                _hdPageCount = withFormat.Count == 0 ? 1 : 2 * withFormat.Count;
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
                var hdText = new string(chars.Select(c => c.ch).ToArray());
                DotMatrix.Draw(g, DotMatrix.Columns(hdText), _hdScroll * DotMatrix.CellCols, mx, my, pitch, HdCells, lit, ghost, glow: false,
                    cellColors: chars.Select(c => c.c).ToList());
                _hits.Add(new Hit(R(mx - 3, 166, HdCells * cellW + 6, 26), "hdlist", NextProgram));
            }
            else
            {
                _hdPageCount = 0;
                DotMatrix.Draw(g, DotMatrix.Columns(""), 0, mx, my, pitch, HdCells, lit, ghost, glow: false);
            }
        }

        DrawSignalMeter(g, lit);

        // art square: album art / station logo (click to enlarge), else the audio spectrum analyzer; while muted, MUTE
        var sq = AnalyzerArea;
        var art = synced && _c.Settings.ShowAlbumArt ? Decode(hd?.AlbumArt ?? hd?.StationLogo) : null;
        if (_c.Settings.Muted)
        {
            const double pitch = 4.3;
            bool on = DateTime.UtcNow.Millisecond < 700;
            double mh = pitch * DotMatrix.Rows, mw = pitch * (4 * DotMatrix.CellCols - 1);
            DotMatrix.Draw(g, DotMatrix.Columns("MUTE"), 0, sq.X + (sq.Width - mw) / 2, sq.Y + (sq.Height - mh) / 2, pitch, 4,
                on ? Alert : Alert.With(40), lit.With(20));
            _hits.Add(new Hit(sq, "unmute", _c.ToggleMute));
            _analyzerShown = false;
            return;
        }
        if (art != null)
        {
            // the art fills the square, cropped to its shape (a source rectangle, no clip: see the class comment), and
            // the corners are rounded by painting the glass back over them
            double pw = art.PixelSize.Width, ph = art.PixelSize.Height;
            double k = Math.Min(pw / sq.Width, ph / sq.Height);
            double cw = sq.Width * k, chh = sq.Height * k;
            g.DrawImage(art, new Rect((pw - cw) / 2, (ph - chh) / 2, cw, chh), sq);
            g.DrawGeometry(Gradient(Glass, Rgb(0x07, 0x0C, 0x0F), Rgb(0x02, 0x04, 0x05), 90), null,
                new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(sq), RoundedGeometry(sq, 5)));
            if (_hover == "art") g.DrawRounded(Pen(lit.With(160), 1.5), sq, 5);
            var artRef = art;
            _hits.Add(new Hit(sq, "art", () => ArtRequested?.Invoke(artRef, sq)));
            _analyzerShown = false;
        }
        else
        {
            _analyzerShown = true;
            if (!AnalyzerLive) DrawAnalyzer(g, lit);   // mid-flip the parent draws it; otherwise the child does
        }
    }

    private void DrawAnalyzer(DrawingContext g, Color lit)
    {
        var sq = AnalyzerArea;
        var ghost = lit.With(20);
        double bw = sq.Width / Bars;
        int segs = 13;
        double sh = sq.Height / segs;
        var rows = new IBrush[segs];
        for (int s = 0; s < segs; s++) rows[s] = Brush(lit.With(120 + 120 * s / (segs - 1)));
        var peak = Brush(Rgb((byte)Math.Min(255, lit.R + 70), (byte)Math.Min(255, lit.G + 70), (byte)Math.Min(255, lit.B + 70)));
        var off = Brush(ghost);
        for (int b = 0; b < Bars; b++)
        {
            int litSegs = _specValid ? (int)Math.Round(_bars[b] * segs) : 0;
            int peakSeg = _specValid ? (int)Math.Round(_peaks[b] * segs) - 1 : -1;
            for (int s = 0; s < segs; s++)
                g.FillRect(s < litSegs ? rows[s] : s == peakSeg ? peak : off, sq.X + b * bw + 1, sq.Bottom - (s + 1) * sh + 1, bw - 2, sh - 2);
        }
    }

    private string MainText(RadioEngine? eng, HdStatus? hd, RdsStatus? rds, bool synced, string mhz, out bool alert)
    {
        alert = false;
        if (_flash != null && DateTime.UtcNow < _flashUntil) return _flash;
        _flash = null;
        if (_c.Starting) return "STARTING";
        if (_c.ServerMode) return _c.Server == null ? (_c.Error ?? "SERVER MODE").ToUpperInvariant() : _c.Server.Client != null ? "SERVING" : "SERVER MODE";
        if (eng == null) return (_c.Error ?? "CLICK TO START").ToUpperInvariant();
        if (_c.Seeking) return $"SEEK  {mhz}";
        if (!string.IsNullOrEmpty(hd?.Alert)) { alert = true; return "ALERT  " + hd.Alert.ToUpperInvariant(); }
        return Item(DisplayModes[Mode].Top, eng, hd, rds, synced, mhz, null).ToUpperInvariant();
    }

    public static readonly (string Name, InfoItem Top, InfoItem Bottom)[] DisplayModes = DisplayText.Modes;

    private int Mode => Math.Clamp(_c.Settings.DisplayMode, 0, DisplayModes.Length - 1);

    private string SubText(RadioEngine? eng, HdStatus? hd, RdsStatus? rds, bool synced, string mhz)
    {
        if (_c.ServerMode && _c.Server is { } srv)
            return srv.Client != null ? $"{srv.Client}  {srv.Frequency / 1e6:0.0} MHZ  {srv.BytesPerSecond / 1e6:0.0} MB/S" : $"{srv.ServiceName} :{srv.Port}  WAITING".ToUpperInvariant();
        if (eng == null) return $"FM {mhz}";
        var (_, top, bottom) = DisplayModes[Mode];
        return Item(bottom, eng, hd, rds, synced, mhz, Item(top, eng, hd, rds, synced, mhz, null)).ToUpperInvariant();
    }

    private static string Item(InfoItem item, RadioEngine? eng, HdStatus? hd, RdsStatus? rds, bool synced, string mhz, string? avoid)
        => DisplayText.Item(item, eng, hd, rds, synced, mhz, avoid);

    private static double StereoIcon(DrawingContext g, double x, double y, bool on, Color lit)
    {
        var pen = Pen(on ? lit : lit.With(28), 1.7);
        const double d = 13, overlap = 5;
        g.DrawEllipse(pen, x + 1, y + 1.5, d, d);
        g.DrawEllipse(pen, x + 1 + d - overlap, y + 1.5, d, d);
        return x + 2 + 2 * d - overlap + 8;
    }

    /// <summary>A map's state: 0 the station doesn't send it, 1 promised by its service guide but not here yet, 2 received.</summary>
    private int MapState(string which)
    {
        var hd = _c.Engine?.Hd;
        if (hd == null) return 0;
        bool have = which == "weather" ? hd.WeatherMap != null : hd.TrafficTiles.Any(t => t != null);
        return have ? 2 : hd.HereImages ? 1 : 0;
    }

    /// <summary>Opens a map, or says on the display why there is none.</summary>
    private void OpenMap(string which)
    {
        int state = MapState(which);
        string name = which == "weather" ? "WX" : "TRAFFIC";
        if (state == 2) MapRequested?.Invoke(which);
        else Flash(state == 1 ? $"{name} PENDING" : $"NO {name} HERE", 1.5);
    }

    /// <summary>A key for a map, lit like its indicator: dark text until the station promises the map, blinking until it's here, lit then.</summary>
    private void MapKey(DrawingContext g, Rect r, string id, string label, string which)
    {
        int state = MapState(which);
        Key(g, r, id, () => OpenMap(which));
        var c = state == 2 ? Lit : Grey;   // grey until a map is here (the display's light blinks while one is promised)
        g.Label(label, 11, c, R(r.X, r.Y + (r.Height - 16) / 2, r.Width, 16), Align.Center, bold: true);
    }

    private double Indicator(DrawingContext g, double x, double y, string text, bool on, bool active, Color lit, Color? onColor = null)
    {
        double w = TextWidth(text, 10.5, bold: true) + 10;
        var r = R(x, y, w, 16);
        var c = on ? onColor ?? lit : lit.With(28);
        if (active) g.FillRounded(Brush(c), r, 3);
        else g.DrawRounded(Pen(c, 1.1), r, 3);
        g.Label(text, 10.5, active ? Black : c, R(r.X, r.Y + 0.5, r.Width, r.Height), Align.Center, bold: true);
        return r.Right + 6;
    }

    private Bitmap? Decode(byte[]? bytes)
    {
        if (ReferenceEquals(bytes, _artKey)) return _art;
        _artKey = bytes;
        _art?.Dispose();
        _art = null;
        if (bytes == null) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            _art = new Bitmap(ms);
        }
        catch { _art = null; }
        return _art;
    }

    // ------------------------------------------------------------------ keys under the display

    private void DrawKeyStrip(DrawingContext g)
    {
        var lit = Lit;
        double y = 214, h = 30;
        var down = R(272, y, 62, h);
        var up = R(338, y, 62, h);
        Key(g, down, "seekdown", () => _c.Seek(-1), lit: _c.Seeking);
        SeekIcon(g, down, -1, lit);
        Key(g, up, "seekup", () => _c.Seek(1), lit: _c.Seeking);
        SeekIcon(g, up, 1, lit);
        double x0 = 408, gap = 4, w = (912 - x0 - gap * 5) / 6;
        int currentPreset = _c.CurrentPreset;
        for (int i = 0; i < AppSettings.PresetCount; i++)
        {
            var r = R(x0 + i * (w + gap), y, w, h);
            int idx = i;
            var p = _c.Settings.Presets[i];
            bool current = i == currentPreset;
            Key(g, r, "preset" + i, () =>
            {
                if (_c.Settings.Presets[idx] == null) { Flash($"HOLD {idx + 1} TO SAVE"); return; }
                _c.RecallPreset(idx);
            }, lit: current, hold: () => { StorePreset(idx); Flash($"P{idx + 1} SAVED", 2.5); },
               holdLong: () => { _c.ClearPreset(idx); Flash($"P{idx + 1} CLEARED", 2.0); });   // keep holding: clear it
            g.Label((i + 1).ToString(), 15, lit, R(r.X + 8, r.Y, 20, h), Align.Near, bold: true);
            // a little LCD window: the frequency in seven-segment digits, then a fixed HD legend with its own small
            // digit, every element always there: lit on the preset you're on, dimmer on the others, ghost 8s on an
            // empty one; the HD legend and digit are ghosts unless the preset was saved on HD2, HD3 ...
            var win = R(r.X + 22, r.Y + 6, r.Width - 27, h - 12);
            g.FillRounded(Brush(Rgb(0x04, 0x06, 0x08)), win, 3);
            const double segH = 10.5, progH = 7, hdPx = 6.5;
            var on = current ? lit : lit.With(150);
            var ghost = lit.With(22);
            double bottom = win.Y + (win.Height + segH) / 2;
            double progX = win.Right - 4 - SevenSegment.DigitWidth(progH);
            bool hasProg = p is { Program: > 0 };
            SevenSegment.Draw(g, hasProg ? (p!.Program + 1).ToString() : " ", progX, bottom - progH, progH, on, ghost);
            double hdW = G.TextWidth("HD", hdPx, bold: true);
            double hdX = progX - 1 - hdW;
            g.Label("HD", hdPx, hasProg ? on : ghost, R(hdX, bottom - progH - 1.5, hdW + 1, 9), Align.Near, bold: true);
            string digits = p == null ? "    " : p.Mhz.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5);
            double digitsW = 4 * SevenSegment.DigitWidth(segH) + 3 * segH * 0.16;
            SevenSegment.Draw(g, digits, hdX - 3 - digitsW, bottom - segH, segH, on, ghost);
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

    private void DrawRightSide(DrawingContext g)
    {
        var lit = Lit;
        DrawTuneKnob(g, lit);
        DrawVolumeKnob(g, lit);
    }

    /// <summary>
    /// The signal meter, inside the display under the art square: an antenna, five dots and an OVL light (fixed
    /// elements, like the rest of the LCD). The quality of what you're hearing: HD MER while HD plays, else the FM
    /// stereo pilot's SNR (channel power for a mono station); OVL lights red when the dongle's ADC clips.
    /// </summary>
    private void DrawSignalMeter(DrawingContext g, Color lit)
    {
        var eng = _c.Engine;
        bool overload = eng?.GainOptimizer.Overload == true;
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
        double x = 812, y = 180, ax = x + 9;
        var pen = Pen(lit, 1.3, round: true);
        g.DrawLine(pen, ax, y - 4, ax, y + 7);
        g.DrawLine(pen, ax - 3, y + 7, ax + 3, y + 7);
        g.FillEllipse(Brush(lit), ax - 1.4, y - 5.4, 2.8, 2.8);
        foreach (double r in new[] { 4.0, 7.0 })
        {
            g.DrawArc(pen, ax - r, y - 4 - r, 2 * r, 2 * r, 150, 60);
            g.DrawArc(pen, ax - r, y - 4 - r, 2 * r, 2 * r, -30, 60);
        }
        int on = (int)Math.Round(q * 5);
        for (int i = 0; i < 5; i++) g.FillEllipse(Brush(i < on ? lit : lit.With(28)), x + 24 + i * 9, y - 3, 6, 6);
        g.Label("OVL", 8.5, overload ? Alert : lit.With(28), R(x + 68, y - 7, 26, 14), Align.Center, bold: true);
    }

    // the volume knob, in the corner under the tuning knob: its light ring shows the level
    private const double VolCx = 945, VolCy = 239, VolR = 21;

    private void DrawVolumeKnob(DrawingContext g, Color lit)
    {
        float vol = _c.Settings.Volume;
        bool muted = _c.Settings.Muted;
        var ring = R(VolCx - VolR - 5, VolCy - VolR - 5, 2 * (VolR + 5), 2 * (VolR + 5));
        g.DrawArc(Pen(lit.With(40), 3), ring, 135, 270);
        if (vol > 0.001f)
        {
            var c = muted ? Grey : lit;
            g.DrawArc(Pen(c.With(60), 7, round: true), ring, 135, 270 * vol);
            g.DrawArc(Pen(c, 2.5, round: true), ring, 135, 270 * vol);
        }
        var kr = R(VolCx - VolR, VolCy - VolR, 2 * VolR, 2 * VolR);
        bool hot = _hover == "knob" || _knobDrag;
        g.FillEllipse(Gradient(kr, Rgb(0x3A, 0x3F, 0x47), Rgb(0x0B, 0x0C, 0x0E), 70), kr);
        g.DrawEllipse(Pen(hot ? lit.With(160) : Rgb(0x5A, 0x61, 0x6B), hot ? 1.8 : 1.5), kr);
        var cap = Inflate(kr, -6, -6);
        g.FillEllipse(Gradient(cap, Rgb(0x23, 0x27, 0x2D), Rgb(0x14, 0x16, 0x1A), 250), cap);
        double a = (135 + 270 * vol) * Math.PI / 180;
        g.FillEllipse(Brush(muted ? Grey : lit), VolCx + Math.Cos(a) * (VolR - 10) - 2.5, VolCy + Math.Sin(a) * (VolR - 10) - 2.5, 5, 5);
        g.Label("VOL", 8.5, lit, R(VolCx - 20, VolCy + VolR + 2, 40, 11), Align.Center, bold: true);
        _hits.Add(new Hit(R(VolCx - VolR - 8, VolCy - VolR - 8, 2 * VolR + 16, 2 * VolR + 16), "knob", null));
    }

    private const double TuneCx = 945, TuneCy = 145, TuneR = 21, DetentDeg = 15;

    private void DrawTuneKnob(DrawingContext g, Color lit)
    {
        long ch = (_c.Frequency - RadioEngine.FirstChannel) / RadioEngine.ChannelStep;
        double turn = (ch * DetentDeg + _tuneDragDeg) * Math.PI / 180;
        var kr = R(TuneCx - TuneR, TuneCy - TuneR, 2 * TuneR, 2 * TuneR);
        bool hot = _hover == "tuneknob" || _tuneDrag;
        g.FillEllipse(Gradient(kr, Rgb(0x3A, 0x3F, 0x47), Rgb(0x0B, 0x0C, 0x0E), 70), kr);
        var notch = Pen(Rgb(0x10, 0x12, 0x15), 1.6);
        for (int i = 0; i < 24; i++)
        {
            double a = turn + i * Math.PI / 12;
            double c = Math.Cos(a), s = Math.Sin(a);
            g.DrawLine(notch, TuneCx + c * (TuneR - 4), TuneCy + s * (TuneR - 4), TuneCx + c * (TuneR - 0.5), TuneCy + s * (TuneR - 0.5));
        }
        g.DrawEllipse(Pen(hot ? lit.With(160) : Rgb(0x5A, 0x61, 0x6B), hot ? 1.8 : 1.5), kr);
        var cap = Inflate(kr, -6, -6);
        g.FillEllipse(Gradient(cap, Rgb(0x23, 0x27, 0x2D), Rgb(0x14, 0x16, 0x1A), 250), cap);
        g.Label("TUNE", 8.5, lit, R(TuneCx - 20, TuneCy + TuneR + 2, 40, 11), Align.Center, bold: true);
        _hits.Add(new Hit(Inflate(kr, 6, 6), "tuneknob", () => _c.Seek(1)));
    }

    private bool _tuneDrag, _tuneMoved;
    private double _tuneStartDeg, _tuneDragDeg;

    private static double AngleDeg(Point p) => Math.Atan2(p.Y - TuneCy, p.X - TuneCx) * 180 / Math.PI;

    private void TuneDragTo(Point p)
    {
        if (Math.Abs(p.X - TuneCx) + Math.Abs(p.Y - TuneCy) < 7) return;
        double d = AngleDeg(p) - _tuneStartDeg;
        d = (d + 540) % 360 - 180;
        _tuneStartDeg = AngleDeg(p);
        _tuneDragDeg += d;
        if (Math.Abs(_tuneDragDeg) > 4) _tuneMoved = true;
        while (Math.Abs(_tuneDragDeg) >= DetentDeg)
        {
            int dir = Math.Sign(_tuneDragDeg);
            _tuneDragDeg -= dir * DetentDeg;
            _c.Step(dir);
        }
        if (_tuneMoved) Flash($"FM {_c.Frequency / 1e6:0.0}", 0.9);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ widgets

    private void Key(DrawingContext g, Rect r, string id, Action click, bool pattern = false, bool lit = false, Action? hold = null, Action? holdLong = null)
    {
        bool hot = _hover == id, down = _pressed == id;
        g.FillRounded(Gradient(r, down ? Key2 : hot ? Rgb(0x36, 0x3B, 0x43) : Key1, down ? Key1 : Key2, 90), r, 6);
        if (pattern) g.FillRounded(Hatch(Lit.With(60)), r, 6);   // a diagonal hatch, like an illuminated phone key
        g.DrawRounded(Pen(lit ? Lit : Rgb(0x05, 0x05, 0x06), lit ? 1.5 : 1.2), r, 6);
        if (!down) g.DrawLine(Pen(White.With(30), 1), r.X + 6, r.Y + 1.5, r.Right - 6, r.Y + 1.5);
        _hits.Add(new Hit(r, id, click, hold, holdLong));
    }

    /// <summary>A rounded rectangle as a geometry (for combining; drawing one uses RoundedRect directly).</summary>
    private static Geometry RoundedGeometry(Rect r, double radius)
    {
        var geo = new StreamGeometry();
        double d = radius;
        using var ctx = geo.Open();
        ctx.BeginFigure(new Point(r.X + d, r.Y), true);
        ctx.LineTo(new Point(r.Right - d, r.Y));
        ctx.ArcTo(new Point(r.Right, r.Y + d), new Size(d, d), 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(r.Right, r.Bottom - d));
        ctx.ArcTo(new Point(r.Right - d, r.Bottom), new Size(d, d), 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(r.X + d, r.Bottom));
        ctx.ArcTo(new Point(r.X, r.Bottom - d), new Size(d, d), 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(r.X, r.Y + d));
        ctx.ArcTo(new Point(r.X + d, r.Y), new Size(d, d), 0, false, SweepDirection.Clockwise);
        ctx.EndFigure(true);
        return geo;
    }

    private static IBrush? _hatch;
    private static Color _hatchColor;

    /// <summary>Diagonal lines every 7 design units (GDI+ WideUpwardDiagonal), as a tiled brush.</summary>
    private static IBrush Hatch(Color c)
    {
        if (_hatch != null && _hatchColor == c) return _hatch;
        _hatchColor = c;
        var line = new GeometryDrawing
        {
            Geometry = new LineGeometry(new Point(0, 7), new Point(7, 0)),
            Pen = new global::Avalonia.Media.Pen(new SolidColorBrush(c), 1.2),
        };
        return _hatch = new DrawingBrush
        {
            Drawing = line,
            TileMode = TileMode.Tile,
            SourceRect = new RelativeRect(0, 0, 7, 7, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, 7, 7, RelativeUnit.Absolute),
        };
    }

    private static void SeekIcon(DrawingContext g, Rect r, int dir, Color c)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = 6;
        var b = Brush(c);
        for (int k = 0; k < 2; k++)
        {
            double bx = cx + dir * (k * s - s);
            g.FillPolygon(b, new Point(bx, cy - s), new Point(bx + dir * s, cy), new Point(bx, cy + s));
        }
        g.FillRect(b, dir > 0 ? cx + s : cx - s - 2, cy - s, 2, 2 * s);
    }

    private static void SpeakerIcon(DrawingContext g, Rect r, bool muted, Color c)
    {
        double cx = r.X + r.Width / 2 - 5, cy = r.Y + r.Height / 2;
        g.FillPolygon(Brush(c), new Point(cx - 9, cy - 5), new Point(cx - 3, cy - 5), new Point(cx + 4, cy - 12), new Point(cx + 4, cy + 12), new Point(cx - 3, cy + 5), new Point(cx - 9, cy + 5));
        var pen = Pen(muted ? Alert : c, 2.2, round: true);
        if (muted) { g.DrawLine(pen, cx + 10, cy - 6, cx + 20, cy + 6); g.DrawLine(pen, cx + 20, cy - 6, cx + 10, cy + 6); }
        else { g.DrawArc(pen, cx - 1, cy - 7, 14, 14, -50, 100); g.DrawArc(pen, cx - 6, cy - 13, 26, 26, -50, 100); }
    }

    private static void PowerIcon(DrawingContext g, double cx, double cy, double r, Color c)
    {
        var pen = Pen(c, 2.2, round: true);
        g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -60, 300);
        g.DrawLine(pen, cx, cy - r - 2, cx, cy);
    }

    private static void BulbIcon(DrawingContext g, double cx, double cy, Color c)
    {
        var pen = Pen(c, 2.2, round: true);
        g.DrawEllipse(pen, cx - 9, cy - 15, 18, 18);
        g.DrawLine(pen, cx - 5, cy + 7, cx + 5, cy + 7);
        g.DrawLine(pen, cx - 4, cy + 11, cx + 4, cy + 11);
        g.FillEllipse(Brush(c.With(90)), cx - 6, cy - 12, 12, 12);
    }

    // ------------------------------------------------------------------ mouse

    private static Point ToDesign(Point p) => p;   // pointer positions come through the render transform

    /// <summary>The window edge under a design point on the faceplate's edge band, or null.</summary>
    private WindowEdge? EdgeAt(Point p)
    {
        const double band = 9;
        var o = CurrentOutline;
        if (!o.Contains(p)) return null;
        bool left = p.X < o.X + band, right = p.X > o.Right - band, top = p.Y < o.Y + band, bottom = p.Y > o.Bottom - band;
        if (top && left) return WindowEdge.NorthWest;
        if (top && right) return WindowEdge.NorthEast;
        if (bottom && left) return WindowEdge.SouthWest;
        if (bottom && right) return WindowEdge.SouthEast;
        if (left) return WindowEdge.West;
        if (right) return WindowEdge.East;
        if (top) return WindowEdge.North;
        if (bottom) return WindowEdge.South;
        return null;
    }

    private static Cursor EdgeCursor(WindowEdge e) => new(e switch
    {
        WindowEdge.NorthWest => StandardCursorType.TopLeftCorner,
        WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
        WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner,
        WindowEdge.SouthEast => StandardCursorType.BottomRightCorner,
        WindowEdge.West => StandardCursorType.LeftSide,
        WindowEdge.East => StandardCursorType.RightSide,
        WindowEdge.North => StandardCursorType.TopSide,
        _ => StandardCursorType.BottomSide,
    });
    private Hit? HitAt(Point p) => _hits.LastOrDefault(h => h.R.Contains(p));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = ToDesign(e.GetPosition(this));
        if (_edgeDrag)   // the window is resizing from an edge: the hover and cursor hold still until the button is up
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _edgeDrag = false;
        }
        if (_tuneDrag) { TuneDragTo(p); return; }
        if (_knobDrag)
        {
            double dy = _knobStart.Y - p.Y;
            if (Math.Abs(dy) > 3) _knobMoved = true;
            if (_knobMoved) SetVolume(_knobStartVol + (float)(dy / 150));
            return;
        }
        var h = HitAt(p)?.Id;
        var edge = h == null ? EdgeAt(p) : null;
        double? hx = h == "tune" ? p.X : null;
        if (hx != _nerd.HoverX) { _nerd.HoverX = hx; if (_spectrum.IsVisible) _spectrum.InvalidateVisual(); }
        if (h != _hover || edge != _edge)
        {
            _hover = h;
            _edge = edge;
            Cursor = edge is { } ed ? EdgeCursor(ed) : h == "tune" ? new Cursor(StandardCursorType.Cross) : h != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    private WindowEdge? _edge;

    private void SetVolume(float v)
    {
        _c.SetVolume(v);
        Flash($"VOL {Math.Round(_c.Settings.Volume * 40):0}", 1.0);
    }

    protected override void OnPointerExited(PointerEventArgs e) { _hover = null; _nerd.HoverX = null; InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var pos = e.GetPosition(this);
        var p = ToDesign(pos);
        var h = HitAt(p);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (h?.Hold != null) { h.Hold(); InvalidateVisual(); return; }
            MenuRequested?.Invoke(e);
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        if (h == null)
        {
            if (EdgeAt(p) is { } edge) { _edgeDrag = true; ResizeRequested?.Invoke(edge, e); }   // the edge band resizes the window
            else DragRequested?.Invoke(e);                                  // bare faceplate: move it
            return;
        }
        _pressed = h.Id;
        _holdFired = false;
        if (h.Id == "knob") { _knobDrag = true; _knobMoved = false; _knobStart = p; _knobStartVol = _c.Settings.Volume; }
        else if (h.Id == "tuneknob") { _tuneDrag = true; _tuneMoved = false; _tuneStartDeg = AngleDeg(p); _tuneDragDeg = 0; }
        else if (h.Hold != null) { _holdAction = h.Hold; _holdLongAction = h.HoldLong; _hold.Interval = HoldTime; _hold.Start(); }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _hold.Stop();
        _edgeDrag = false;
        var p = ToDesign(e.GetPosition(this));
        if (_knobDrag)
        {
            _knobDrag = false;
            if (!_knobMoved) { _c.ToggleMute(); Flash(_c.Settings.Muted ? "MUTE" : "MUTE OFF", 1.0); }
        }
        else if (_tuneDrag)
        {
            _tuneDrag = false;
            _tuneDragDeg = 0;
            if (!_tuneMoved && HitAt(p)?.Id == "tuneknob") _c.Seek(1);
        }
        else if (e.InitialPressMouseButton == MouseButton.Left && !_holdFired)
        {
            var h = HitAt(p);
            if (h != null && h.Id == _pressed)
            {
                if (h.Id == "tune")
                {
                    long hz = _nerd.FrequencyAt(p.X);
                    _nerd.BeginTune(hz);
                    _c.Tune(hz);
                    _nerd.HoverX = null;   // the highlight now travels with the station
                }
                else h.Click?.Invoke();
            }
        }
        _pressed = null;
        InvalidateVisual();
    }

    // A mouse wheel sends one unit per notch; a trackpad sends a stream of small fractions (and keeps sending while it
    // coasts). Tuning steps a channel per whole unit, at most a few times a second; volume follows the delta directly.
    private double _wheelTune;
    private DateTime _wheelAt, _wheelStepAt;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = ToDesign(e.GetPosition(this));
        double d = e.Delta.Y;
        if (d == 0) return;
        e.Handled = true;
        var now = DateTime.UtcNow;
        if (!_open && HitAt(p)?.Id == "knob")   // over the volume knob: volume (the TUNE knob and the display tune; panel open: the wheel tunes everywhere)
        {
            SetVolume(_c.Settings.Volume + (float)(d * 0.025));
            InvalidateVisual();
            return;
        }
        // over the display: tune
        if ((now - _wheelAt).TotalMilliseconds > 300) _wheelTune = 0;   // a new gesture starts from zero
        _wheelAt = now;
        _wheelTune += d;
        if (Math.Abs(_wheelTune) >= 1 && (now - _wheelStepAt).TotalMilliseconds >= 150)
        {
            _c.Step(Math.Sign(_wheelTune));
            _wheelTune = 0;
            _wheelStepAt = now;
            InvalidateVisual();
        }
        else if (Math.Abs(_wheelTune) > 3) _wheelTune = 3 * Math.Sign(_wheelTune);   // coasting doesn't bank up
    }
}
