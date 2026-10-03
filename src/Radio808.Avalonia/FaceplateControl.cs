using System;
using System.Collections.Generic;
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

    private sealed record Hit(Rect R, string Id, Action? Click, Action? Hold = null);
    private readonly List<Hit> _hits = new();
    private string? _hover, _pressed;
    private bool _holdFired, _knobDrag, _knobMoved, _edgeDrag;
    private Point _knobStart;
    private float _knobStartVol;
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Action? _holdAction;

    // display state
    private string _mainText = "";
    private List<bool[]> _mainCols = new();
    private int _scrollChars, _scrollTicks;
    private string? _subText;
    private List<bool[]> _subCols = new();
    private int _subScroll, _subTicks, _subCells = 10;
    private const int HdCells = 13;
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
            InvalidateVisual();
        };
        _tick.Tick += (_, _) => Tick();
        _frames.Tick += (_, _) => Frame();
        c.Changed += InvalidateVisual;
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
        DrawLeftKeys(g);
        DrawKnob(g);
        DrawDisplay(g);
        DrawKeyStrip(g);
        DrawRightSide(g);
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
        // brand above the display, model name at the right
        BrandMark.Draw(g, 538, 32, 20, Silver);
        g.Label("HD-808", 9.5, Grey, R(800, 32, 92, 20), Align.Far);

        // OPEN: flips the faceplate down (where a head unit has its open/eject key)
        var open = R(272, 34, 40, 16);
        bool hotOpen = _hover == "open";
        g.FillRounded(Brush(hotOpen ? Rgb(0x3A, 0x40, 0x48) : Rgb(0x16, 0x18, 0x1C)), open, 4);
        {
            var b = Brush(Lit);
            double cx = open.X + open.Width / 2, cy = open.Y + 7;
            g.FillPolygon(b, new Point(cx - 5, cy + 2), new Point(cx + 5, cy + 2), new Point(cx, cy - 3));
            g.FillRect(b, cx - 5, cy + 4, 10, 1.6);
        }
        _hits.Add(new Hit(open, "open", ToggleOpen));

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

    private void DrawLeftKeys(DrawingContext g)
    {
        var lit = Lit;
        var mute = R(36, 40, 86, 54);
        Key(g, mute, "mute", _c.ToggleMute, pattern: true);
        SpeakerIcon(g, mute, _c.Settings.Muted, lit);

        var src = R(36, 110, 62, 56);
        Key(g, src, "src", () =>
        {
            _c.SetForceAnalog(!_c.Settings.ForceAnalog);
            Flash(_c.Settings.ForceAnalog ? "SOURCE FM" : "SOURCE HD");
        });
        PowerIcon(g, src.X + src.Width / 2, src.Y + 20, 8, lit);
        g.Label("SRC", 12, lit, R(src.X, src.Y + 32, src.Width, 18), Align.Center, bold: true);

        var col = R(36, 182, 86, 54);
        Key(g, col, "color", CycleColor, pattern: true);
        BulbIcon(g, col.X + col.Width / 2, col.Y + col.Height / 2, lit);
    }

    public void CycleColor()
    {
        _c.Settings.Illumination = (_c.Settings.Illumination + 1) % Illuminations.Length;
        _c.Settings.Save();
        Flash("COLOR " + Illuminations[_c.Settings.Illumination].Name);
    }

    private void DrawKnob(DrawingContext g)
    {
        double cx = 196, cy = 124, r = 58;
        var lit = Lit;
        float vol = _c.Settings.Volume;
        bool muted = _c.Settings.Muted;
        var ring = R(cx - r - 9, cy - r - 9, 2 * (r + 9), 2 * (r + 9));
        g.DrawArc(Pen(lit.With(40), 6), ring, 135, 270);
        if (vol > 0.001f)
        {
            var c = muted ? Grey : lit;
            g.DrawArc(Pen(c.With(60), 14, round: true), ring, 135, 270 * vol);
            g.DrawArc(Pen(c, 5, round: true), ring, 135, 270 * vol);
        }
        var kr = R(cx - r, cy - r, 2 * r, 2 * r);
        g.FillEllipse(Gradient(kr, Rgb(0x3A, 0x3F, 0x47), Rgb(0x0B, 0x0C, 0x0E), 70), kr);
        g.DrawEllipse(Pen(Rgb(0x5A, 0x61, 0x6B), 2), kr);
        var inner = R(cx - r + 10, cy - r + 10, 2 * r - 20, 2 * r - 20);
        g.FillEllipse(Gradient(inner, Rgb(0x23, 0x27, 0x2D), Rgb(0x14, 0x16, 0x1A), 250), inner);
        double a = (135 + 270 * vol) * Math.PI / 180;
        double px = cx + Math.Cos(a) * (r - 16), py = cy + Math.Sin(a) * (r - 16);
        g.FillEllipse(Brush(muted ? Grey : lit), px - 3.5, py - 3.5, 7, 7);
        _hits.Add(new Hit(R(cx - r - 12, cy - r - 12, 2 * r + 24, 2 * r + 24), "knob", null));

        g.Label("HD CH", 10, lit, R(200, 196, 52, 14), Align.Center, bold: true);
        g.Label("DISP", 10, lit, R(142, 196, 52, 14), Align.Center, bold: true);
        Key(g, R(200, 212, 52, 22), "band", NextProgram);
        Key(g, R(142, 212, 52, 22), "disp", NextDisplay);
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

    private static void Marquee(int len, int cells, ref int chars, ref int ticks)
    {
        if (len <= cells) { chars = ticks = 0; return; }
        ticks++;
        const int start = 20, every = 3;
        if (ticks > start && (ticks - start) % every == 0 && ++chars > len + 3) chars = ticks = 0;
    }

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
        x = Indicator(g, x, 172, "SEEK", _c.Seeking, _c.Seeking, lit) + 10;
        bool wx = hd?.WeatherMap != null, trf = hd != null && hd.TrafficTiles.Any(t => t != null);
        double wxEnd = Indicator(g, x, 172, "WX", wx, false, lit);
        if (wx) _hits.Add(new Hit(R(x - 2, 168, wxEnd - x, 22), "wx", () => MapRequested?.Invoke("weather")));
        x = wxEnd;
        double trfEnd = Indicator(g, x, 172, "TRAFFIC", trf, false, lit);
        if (trf) _hits.Add(new Hit(R(x - 2, 168, trfEnd - x, 22), "trf", () => MapRequested?.Invoke("traffic")));

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
        if (eng == null) return (_c.Error ?? "CLICK TO START").ToUpperInvariant();
        if (_c.Seeking) return $"SEEK  {mhz}";
        if (!string.IsNullOrEmpty(hd?.Alert)) { alert = true; return "ALERT  " + hd.Alert.ToUpperInvariant(); }
        return Item(DisplayModes[Mode].Top, eng, hd, rds, synced, mhz, null).ToUpperInvariant();
    }

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

    private string SubText(RadioEngine? eng, HdStatus? hd, RdsStatus? rds, bool synced, string mhz)
    {
        if (eng == null) return $"FM {mhz}";
        var (_, top, bottom) = DisplayModes[Mode];
        return Item(bottom, eng, hd, rds, synced, mhz, Item(top, eng, hd, rds, synced, mhz, null)).ToUpperInvariant();
    }

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

    private static double StereoIcon(DrawingContext g, double x, double y, bool on, Color lit)
    {
        var pen = Pen(on ? lit : lit.With(28), 1.7);
        const double d = 13, overlap = 5;
        g.DrawEllipse(pen, x + 1, y + 1.5, d, d);
        g.DrawEllipse(pen, x + 1 + d - overlap, y + 1.5, d, d);
        return x + 2 + 2 * d - overlap + 8;
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
            }, lit: current, hold: () => { StorePreset(idx); Flash($"P{idx + 1} SAVED"); });
            g.Label((i + 1).ToString(), 15, lit, R(r.X + 8, r.Y, 20, h), Align.Near, bold: true);
            var win = R(r.Right - 52, r.Y + 6, 46, h - 12);
            g.FillRounded(Brush(Rgb(0x04, 0x06, 0x08)), win, 3);
            const double segH = 12;
            string digits = p == null ? "    " : p.Mhz.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5);
            double digitsW = 4 * SevenSegment.DigitWidth(segH) + 3 * segH * 0.16;
            SevenSegment.Draw(g, digits, win.Right - 5 - digitsW, win.Y + (win.Height - segH) / 2, segH,
                current ? lit : lit.With(150), lit.With(22));
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
        var slot = R(928, 58, 34, 116);
        var eng = _c.Engine;
        bool overload = eng?.GainOptimizer.Overload == true;
        g.FillRounded(Brush(Rgb(0x06, 0x07, 0x09)), slot, 8);
        g.DrawRounded(Pen(overload ? Alert : lit, overload ? 2.4 : 1.6), slot, 8);
        if (overload) g.Label("OVL", 9, Alert, R(slot.X, slot.Y + 2, slot.Width, 12), Align.Center, bold: true);
        double q = 0;
        if (eng != null)
        {
            var hd = eng.Hd;
            var st = eng.Receiver.Stereo;
            q = eng.Blender.PlayingHd ? Math.Clamp(((hd.MerLower + hd.MerUpper) / 2 - 3) / 15, 0, 1)
              : st.PilotLocked ? Math.Clamp((st.PilotSnrDb - 10) / 35, 0, 1)
              : Math.Clamp((eng.Receiver.ChannelPowerDb + 52) / 40, 0, 1);
        }
        int segs = 10;
        for (int s = 0; s < segs; s++)
        {
            bool on = s < Math.Round(q * segs);
            g.FillRect(Brush(on ? lit : lit.With(22)), slot.X + 9, slot.Bottom - 10 - (s + 1) * 9.4, slot.Width - 18, 6.5);
        }
        // the antenna legend under the slot
        {
            var c = (overload ? Alert : lit).With(overload ? 255 : 200);
            var pen = Pen(c, 1.4, round: true);
            double ax = slot.X + slot.Width / 2, top = slot.Bottom + 6;
            g.DrawLine(pen, ax, top + 3, ax, top + 15);
            g.DrawLine(pen, ax - 3.5, top + 15, ax + 3.5, top + 15);
            g.FillEllipse(Brush(c), ax - 1.6, top + 1.4, 3.2, 3.2);
            foreach (double r in new[] { 5.0, 9.0 })
            {
                g.DrawArc(pen, ax - r, top + 3 - r, 2 * r, 2 * r, 150, 60);
                g.DrawArc(pen, ax - r, top + 3 - r, 2 * r, 2 * r, -30, 60);
            }
        }
        DrawTuneKnob(g, lit);
    }

    private const double TuneCx = 945, TuneCy = 233, TuneR = 21, DetentDeg = 15;

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
        double px = TuneCx + Math.Cos(turn - Math.PI / 2) * (TuneR - 10), py = TuneCy + Math.Sin(turn - Math.PI / 2) * (TuneR - 10);
        g.FillEllipse(Brush(lit), px - 2.5, py - 2.5, 5, 5);
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

    private void Key(DrawingContext g, Rect r, string id, Action click, bool pattern = false, bool lit = false, Action? hold = null)
    {
        bool hot = _hover == id, down = _pressed == id;
        g.FillRounded(Gradient(r, down ? Key2 : hot ? Rgb(0x36, 0x3B, 0x43) : Key1, down ? Key1 : Key2, 90), r, 6);
        if (pattern) g.FillRounded(Hatch(Lit.With(60)), r, 6);   // a diagonal hatch, like an illuminated phone key
        g.DrawRounded(Pen(lit ? Lit : Rgb(0x05, 0x05, 0x06), lit ? 1.5 : 1.2), r, 6);
        if (!down) g.DrawLine(Pen(White.With(30), 1), r.X + 6, r.Y + 1.5, r.Right - 6, r.Y + 1.5);
        _hits.Add(new Hit(r, id, click, hold));
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
        else if (h.Hold != null) { _holdAction = h.Hold; _hold.Start(); }
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
        if (p.X < 268 && !_open)   // over the knob side: volume (panel open: the wheel tunes everywhere)
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
