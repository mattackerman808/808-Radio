using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using Radio808.Core.Dsp;
using Radio808.Core.Radio;
using Radio808.Shared;

namespace Radio808.App;

/// <summary>
/// The "behind the faceplate" instrument panel: baseband spectrum + waterfall, FM MPX spectrum, live stats,
/// history sparklines, the multipath equalizer's taps, and audio meters. Drawn in design coordinates.
///
/// The live sections (spectrum + waterfall, multiplex, meters) draw through <see cref="IPanelCanvas"/>: on the GPU from
/// the <see cref="GpuPanel"/>'s render thread while the panel is open, or with GDI+ (during the flip, or if there's no
/// GPU). Their state is guarded by <see cref="Sync"/>. The slow sections (stats, taps, history) are GDI+ on the UI thread.
/// </summary>
internal sealed class NerdPanel : IDisposable
{
    private const int SpecW = 592, WaterH = 150;

    /// <summary>Guards the live sections' state (render thread vs. UI thread).</summary>
    public readonly object Sync = new();

    private readonly RadioController _c;
    private readonly float[] _spec = new float[SpecW];
    private bool _specValid;
    private readonly WaterfallImage _water = new(SpecW, WaterH);
    private int _newest;   // waterfall row holding the newest line; older lines follow below it (circular)
    private float _wfFloor = -60, _wfTop = -20;

    private readonly Fft _mpxFft = new(4096);
    private readonly float[] _mpxIn = new float[4096], _mpxIq = new float[8192], _re = new float[4096], _im = new float[4096], _mpxDb = new float[4096];
    private readonly float[] _mpx = new float[SpecW];
    private bool _mpxValid;

    private readonly float[] _taps = new float[64];
    private const int HistLen = 120;   // 60 s at 2 per second
    private readonly Queue<(float mer, float pilot, float power)> _hist = new();
    private DateTime _nextHist = DateTime.MinValue;
    private long _lastGroups = -1;
    private DateTime _lastGroupsAt;
    private double _groupsPerSec;

    public NerdPanel(RadioController c) => _c = c;

    /// <summary>True while the GPU draws the live sections: GDI+ paints skip them.</summary>
    public bool LiveOnGpu { get; set; }

    // ------------------------------------------------------------------ data in

    /// <summary>A new baseband power spectrum (4096 bins, dB, -fs/2 .. +fs/2), used in the 744 kHz span.</summary>
    public void AddBaseband(float[] db)
    {
        lock (Sync) if (!Wide) AddRow(db);
    }

    private const double RowSeconds = 0.05;   // waterfall: 20 rows a second

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private double _specAt, _mpxAt, _lastRowAt;

    /// <summary>Exponential smoothing weight for an update now, with time constant <paramref name="tau"/> seconds.</summary>
    private static float Alpha(ref double lastAt, double tau)
    {
        double now = Clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - lastAt, 0.001, 1);
        lastAt = now;
        return (float)(1 - Math.Exp(-dt / tau));
    }

    private readonly float[] _rowAcc = new float[SpecW], _row = new float[SpecW], _sorted = new float[SpecW];
    private int _rowCount;
    private double _nextRowAt;

    private readonly Fft _devFft = new(4096), _bbFft = new(4096);
    private readonly float[] _devIq = new float[8192], _devDb = new float[4096], _bbIq = new float[8192], _bbDb = new float[4096];
    private long _rowCenter;

    private void AddRow(float[] db)
    {
        CheckRetune();
        if (DateTime.UtcNow < _ignoreRowsUntil) return;   // samples still in flight from the old frequency
        // Each column covers its exact share of the bins (4096 / 592 isn't an integer; truncating it squeezed the
        // spectrum toward the left and put the signal ~100 kHz off its markers).
        // The trace follows every spectrum (one per frame), lightly smoothed by time (~60 ms, so it looks the same at
        // any frame rate and doesn't lag); the waterfall gets a row every RowSeconds, the average of the spectra since
        // the last one (smoother than single spectra, and ~7 s of history in the 150 rows).
        float specAlpha = Alpha(ref _specAt, 0.06);
        var row = _row;
        for (int c = 0; c < SpecW; c++)
        {
            int k0 = (int)((long)c * db.Length / SpecW), k1 = Math.Max(k0 + 1, (int)((long)(c + 1) * db.Length / SpecW));
            float sum = 0;
            for (int k = k0; k < k1; k++) sum += MathF.Pow(10, db[k] / 10);
            float lin = sum / (k1 - k0);
            _rowAcc[c] += lin;
            row[c] = 10 * MathF.Log10(lin + 1e-20f);
            _spec[c] = _specValid ? _spec[c] + specAlpha * (row[c] - _spec[c]) : row[c];
        }
        _specValid = true;
        _rowCount++;
        double now = Clock.Elapsed.TotalSeconds;
        if (now < _nextRowAt) return;
        _nextRowAt = now - _nextRowAt > RowSeconds ? now + RowSeconds : _nextRowAt + RowSeconds;
        for (int c = 0; c < SpecW; c++)
        {
            row[c] = 10 * MathF.Log10(_rowAcc[c] / _rowCount + 1e-20f);
            _rowAcc[c] = 0;
        }
        _rowCount = 0;
        // waterfall scale tracks the noise floor and the peak slowly
        Array.Copy(row, _sorted, SpecW);
        Array.Sort(_sorted);
        float floor = _sorted[SpecW / 5] - 3, top = Math.Max(_sorted[^1], floor + 25);
        float rate = _wfScaled ? 0.1f : 1f;   // after a clear, snap the color scale to the first row
        _wfFloor += rate * (floor - _wfFloor);
        _wfTop += rate * (top - _wfTop);
        _wfScaled = true;
        WriteWaterfallRow(row);
        _lastRowAt = now;
    }

    private void WriteWaterfallRow(float[] row)
    {
        _newest = (_newest - 1 + WaterH) % WaterH;
        var px = _water.Pixels.AsSpan(_newest * SpecW, SpecW);
        for (int c = 0; c < SpecW; c++) px[c] = Heat((row[c] - _wfFloor) / (_wfTop - _wfFloor));
        _water.Version++;
    }

    private static readonly (float p, int r, int g, int b)[] HeatStops =
        { (0, 0, 0, 0), (0.2f, 0, 0, 110), (0.45f, 0, 170, 230), (0.65f, 240, 230, 40), (0.85f, 255, 60, 20), (1, 255, 255, 255) };

    /// <summary>Classic SDR waterfall palette: black, blue, cyan, yellow, red, white.</summary>
    private static int Heat(float v)
    {
        v = Math.Clamp(v, 0, 1);
        for (int i = 1; i < HeatStops.Length; i++)
            if (v <= HeatStops[i].p)
            {
                var a = HeatStops[i - 1]; var b = HeatStops[i];
                float t = (v - a.p) / (b.p - a.p);
                int r = (int)(a.r + (b.r - a.r) * t), g = (int)(a.g + (b.g - a.g) * t), bl = (int)(a.b + (b.b - a.b) * t);
                return (r << 16) | (g << 8) | bl;
            }
        return 0xFFFFFF;
    }

    /// <summary>Pulls the spectrum (wide or narrow) and the MPX spectrum. Call once per frame.</summary>
    public void FrameTick(RadioEngine? eng)
    {
        if (eng == null) return;
        lock (Sync)
        {
            if (Wide)
            {
                if (eng.TryGetDeviceSpectrumBlock(_devIq))
                {
                    _devFft.PowerDb(_devIq, _re, _im, _devDb);
                    AddRow(_devDb);
                }
            }
            else if (eng.TryGetSpectrumBlock(_bbIq))
            {
                _bbFft.PowerDb(_bbIq, _re, _im, _bbDb);
                AddRow(_bbDb);
            }
            if (eng.TryGetMpxBlock(_mpxIn))
            {
                for (int i = 0; i < 4096; i++) { _mpxIq[2 * i] = _mpxIn[i]; _mpxIq[2 * i + 1] = 0; }
                _mpxFft.PowerDb(_mpxIq, _re, _im, _mpxDb);   // index 2048 = DC
                float mpxAlpha = Alpha(ref _mpxAt, 0.12);
                double binHz = FmReceiver.MpxRate / 4096;
                for (int c = 0; c < SpecW; c++)
                {
                    double f0 = c * 60_000.0 / SpecW, f1 = (c + 1) * 60_000.0 / SpecW;
                    float m = -200;
                    for (int k = (int)(f0 / binHz); k <= (int)(f1 / binHz); k++) m = Math.Max(m, _mpxDb[2048 + k]);
                    _mpx[c] = _mpxValid ? _mpx[c] + mpxAlpha * (m - _mpx[c]) : m;
                }
                _mpxValid = true;
            }
        }
    }

    /// <summary>Equalizer taps and the history (call at ~10 Hz).</summary>
    public void Tick(RadioEngine? eng)
    {
        if (eng == null) return;
        eng.Receiver.Equalizer.CopyTapMagnitudes(_taps);

        var now = DateTime.UtcNow;
        if (now >= _nextHist)
        {
            _nextHist = now.AddSeconds(0.5);
            var hd = eng.Hd;
            _hist.Enqueue((hd.Synced ? (hd.MerLower + hd.MerUpper) / 2 : float.NaN,
                           eng.Receiver.Stereo.PilotLocked ? eng.Receiver.Stereo.PilotSnrDb : float.NaN,
                           (float)eng.Receiver.ChannelPowerDb));
            while (_hist.Count > HistLen) _hist.Dequeue();
            var rds = eng.Receiver.Rds;
            if (_lastGroups >= 0 && rds.Groups >= _lastGroups)
            {
                double dt = (now - _lastGroupsAt).TotalSeconds;
                if (dt > 0) _groupsPerSec += 0.5 * ((rds.Groups - _lastGroups) / dt - _groupsPerSec);
            }
            _lastGroups = rds.Groups; _lastGroupsAt = now;
        }
    }

    public void Reset()
    {
        lock (Sync) _specValid = _mpxValid = false;
        _hist.Clear();
        _lastGroups = -1;
    }

    // ------------------------------------------------------------------ layout

    /// <summary>Sections redrawn every frame (spectrum + waterfall, multiplex, meters), in design coordinates.</summary>
    public RectangleF[] FastRects { get; private set; } = Array.Empty<RectangleF>();
    /// <summary>Sections whose data changes a few times a second (stats, taps, history).</summary>
    public RectangleF[] SlowRects { get; private set; } = Array.Empty<RectangleF>();
    /// <summary>The live sections' column (what the GPU panel covers), in design coordinates.</summary>
    public RectangleF LiveArea { get; private set; }

    private RectangleF _baseband, _mpxR, _meters, _statsR, _tapsR, _histR;

    public void Layout(RectangleF area)
    {
        float lx = area.X + 12, rx = area.X + 632, rw = area.Right - rx - 12;
        _baseband = new(lx - 4, area.Y + 4, SpecW + 8, 292);
        _mpxR = new(lx - 4, area.Y + 292, SpecW + 8, 132);
        _meters = new(lx - 4, area.Y + 424, SpecW + 8, 66);
        _statsR = new(rx - 2, area.Y + 4, rw + 4, 308);
        _tapsR = new(rx - 2, area.Y + 312, rw + 4, 74);
        _histR = new(rx - 2, area.Y + 384, rw + 4, area.Bottom - area.Y - 384);
        FastRects = new[] { _baseband, _mpxR, _meters };
        SlowRects = new[] { _statsR, _tapsR, _histR };
        LiveArea = RectangleF.FromLTRB(_baseband.Left, _baseband.Top, _baseband.Right, _meters.Bottom);
        TuneArea = new RectangleF(lx, area.Y + 26, SpecW, 100 + 1 + WaterH);
        SpanToggle = new RectangleF(lx + SpecW - 140, area.Y + 6, 140, 16);
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>GDI+ paint (UI thread): the slow sections, and the live ones unless the GPU has them.</summary>
    public void Draw(Graphics g, RectangleF area, Color lit, PaintTiming timing)
    {
        var eng = _c.Engine;
        Layout(area);
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!LiveOnGpu && (g.IsVisible(_baseband) || g.IsVisible(_mpxR) || g.IsVisible(_meters)))
        {
            var cv = new GdiCanvas(g);
            lock (Sync) DrawLive(cv, area, lit, g.IsVisible(_baseband), g.IsVisible(_mpxR), g.IsVisible(_meters));
        }
        t = timing.Section("live (gdi)", t);
        float rx = area.X + 632, rw = area.Right - rx - 12;
        if (g.IsVisible(_statsR)) DrawStats(g, rx, area.Y + 8, rw, lit, eng);
        t = timing.Section("stats", t);
        if (g.IsVisible(_tapsR)) DrawTaps(g, rx, area.Y + 316, rw, lit, eng);
        t = timing.Section("taps", t);
        if (g.IsVisible(_histR)) DrawHistory(g, rx, area.Y + 388, rw, lit);
        t = timing.Section("history", t);
    }

    /// <summary>GPU paint (render thread): the live sections.</summary>
    public void DrawGpu(IPanelCanvas cv, RectangleF area, Color lit)
    {
        lock (Sync)
        {
            Layout(area);
            DrawLive(cv, area, lit, true, true, true);
        }
    }

    private void DrawLive(IPanelCanvas cv, RectangleF area, Color lit, bool baseband, bool mpx, bool meters)
    {
        float lx = area.X + 12;
        if (baseband) DrawBaseband(cv, lx, area.Y + 8, lit);
        if (mpx) DrawMpx(cv, lx, area.Y + 296, lit);
        if (meters) DrawMeters(cv, lx, area.Y + 428, lit, _c.Engine);
    }

    private static void Title(IPanelCanvas cv, float x, float y, string s, Color lit) =>
        cv.Text(s, new RectangleF(x, y, 900, 16), TextKind.Title, lit);

    private static void Small(IPanelCanvas cv, string s, float x, float y, float w, Color c, StringAlignment align) =>
        cv.Text(s, new RectangleF(x, y, w, 14), TextKind.Small, c, align);

    private static void Frame(IPanelCanvas cv, RectangleF r, bool fill = true)
    {
        if (fill) cv.Fill(r, Color.FromArgb(0x03, 0x05, 0x06));
        cv.Stroke(r, Color.FromArgb(0x1E, 0x24, 0x2B));
    }

    private void DrawBaseband(IPanelCanvas cv, float x, float y, Color lit)
    {
        CheckRetune();
        long f = _c.Frequency;
        double half = HalfSpanKhz;
        var eng = _c.Engine;
        bool hdSynced = eng?.Hd.Synced == true, hdPlaying = eng?.Blender.PlayingHd == true;
        var orange = Color.FromArgb(0xF7, 0x94, 0x1D);
        Title(cv, x, y, $"SPECTRUM  ·  {f / 1e6:0.0} MHz  ·  click a station to tune, wheel to step", lit);
        // span toggle, right-aligned on the title line
        float rEdge = x + SpecW;
        var dim = Color.FromArgb(90, lit);
        Small(cv, "SPAN", rEdge - 140, y + 1, 34, dim, StringAlignment.Near);
        Small(cv, "1.5 MHz", rEdge - 104, y + 1, 44, Wide ? lit : dim, StringAlignment.Near);
        Small(cv, "|", rEdge - 56, y + 1, 8, dim, StringAlignment.Near);
        Small(cv, "744 kHz", rEdge - 44, y + 1, 44, Wide ? dim : lit, StringAlignment.Near);

        var sr = new RectangleF(x, y + 18, SpecW, 100);
        var wr = new RectangleF(x, sr.Bottom + 1, SpecW, WaterH);
        Frame(cv, sr);
        _tuneX0 = x;

        // Everything inside the spectrum/waterfall (and the axis under it) is drawn in the new center's
        // coordinates, shifted by the slide offset, which eases to 0 after a retune.
        float pan = Pan;
        cv.PushClip(new RectangleF(x, sr.Y, SpecW, sr.Height + 1 + WaterH + 16));
        cv.PushTranslate(pan, 0);
        float Px(double khz) => (float)(sr.X + (khz + half) / (2 * half) * SpecW);

        // the current station's HD sidebands: only while HD is decoding, brighter while it's what you hear
        if (hdSynced)
        {
            var hdC = Color.FromArgb(hdPlaying ? 48 : 24, orange);
            cv.Fill(RectangleF.FromLTRB(Px(-198), sr.Y, Px(-129), sr.Bottom), hdC);
            cv.Fill(RectangleF.FromLTRB(Px(129), sr.Y, Px(198), sr.Bottom), hdC);
        }
        // channel grid (US: odd tenths, 200 kHz apart), one span beyond each edge so the slide never shows a gap
        var channels = new List<(long hz, float px)>();
        long firstCh = Snap(f - (long)(2 * half * 1000));
        for (long ch = firstCh; ch <= f + 2 * half * 1000; ch += RadioEngine.ChannelStep)
            channels.Add((ch, Px((ch - f) / 1000.0)));
        var gridC = Color.FromArgb(30, lit);
        foreach (var (_, px) in channels) cv.Line(px, sr.Y, px, sr.Bottom, gridC, 1, DashStyle.Dot);
        if (_specValid)
        {
            float lo = _wfFloor, hi = _wfTop + 5;
            var pts = new PointF[SpecW];
            for (int c = 0; c < SpecW; c++)
                pts[c] = new PointF(sr.X + c, sr.Bottom - Math.Clamp((_spec[c] - lo) / (hi - lo), 0, 1) * (sr.Height - 4));
            cv.Area(pts, sr.Bottom, sr, Color.FromArgb(110, lit), Color.FromArgb(10, lit));
            cv.Trace(pts, lit, 1.2f);
        }
        // waterfall, newest row at the top. Between rows the image glides down by the fraction of a row that's due,
        // with the newest row stretched into the gap, so it scrolls continuously instead of jumping a row at a time.
        cv.PushClip(wr);
        float glide = _specValid ? (float)Math.Clamp((Clock.Elapsed.TotalSeconds - _lastRowAt) / RowSeconds, 0, 1) : 0;
        int first = WaterH - _newest;   // rows from the newest to the bottom of the image, then from its top
        cv.Waterfall(_water, _newest, first, new RectangleF(wr.X, wr.Y + glide, SpecW, first));
        cv.Waterfall(_water, 0, _newest, new RectangleF(wr.X, wr.Y + glide + first, SpecW, _newest));
        if (glide > 0) cv.Waterfall(_water, _newest, 1, new RectangleF(wr.X, wr.Y, SpecW, glide));
        cv.PopClip();
        // axis: channel frequencies, the tuned one brighter
        foreach (var (hz, px) in channels)
            Small(cv, (hz / 1e6).ToString("0.0"), px - 18, wr.Bottom + 1, 36, hz == Snap(f) ? orange : Color.FromArgb(140, lit), StringAlignment.Center);
        if (hdSynced)
        {
            Small(cv, "HD", Px(-163) - 10, sr.Y + 2, 20, orange, StringAlignment.Center);
            Small(cv, "HD", Px(163) - 10, sr.Y + 2, 20, orange, StringAlignment.Center);
        }
        // tuned station marker
        cv.Line(Px(0), sr.Y, Px(0), wr.Bottom, Color.FromArgb(170, orange), 1.4f);
        // the clicked channel travels to the center with its highlight
        if (_pending is long pend)
        {
            float a = Px((pend - f) / 1000.0 - 100), b = Px((pend - f) / 1000.0 + 100);
            cv.Fill(new RectangleF(a, sr.Y, b - a, sr.Height + 1 + WaterH), Color.FromArgb(55, lit));
        }
        cv.PopTranslate();
        cv.PopClip();
        Frame(cv, wr, fill: false);

        // hover (only when not mid-slide): the channel under the mouse, shaded, with its frequency
        if (_pending == null && HoverX is float hx && hx >= sr.X && hx <= sr.Right)
        {
            long ch = FrequencyAt(hx);
            float a = Px((ch - f) / 1000.0 - 100), b = Px((ch - f) / 1000.0 + 100);
            cv.PushClip(TuneArea);
            cv.Fill(new RectangleF(a, sr.Y, b - a, sr.Height + 1 + WaterH), Color.FromArgb(40, lit));
            cv.PopClip();
            cv.Line(hx, sr.Y, hx, wr.Bottom, Color.FromArgb(200, lit));
            string label = ch == Snap(f) ? $"{ch / 1e6:0.0} MHz (tuned)" : $"{ch / 1e6:0.0} MHz  ·  click to tune";
            float w = 150, lx = Math.Clamp(hx + 6, sr.X + 2, sr.Right - w - 2);
            cv.Fill(new RectangleF(lx - 2, sr.Y + 16, w, 15), Color.FromArgb(225, 0x03, 0x05, 0x06));
            Small(cv, label, lx, sr.Y + 17, w - 4, lit, StringAlignment.Near);
        }

        // banner while tuning
        if (DateTime.UtcNow < _bannerUntil)
        {
            string msg = $"TUNED  {f / 1e6:0.0} MHz";
            float bw = cv.MeasureText(msg, TextKind.Banner) + 24;
            var br = new RectangleF(sr.X + (SpecW - bw) / 2, sr.Y + 6, bw, 22);
            double age = (_bannerUntil - DateTime.UtcNow).TotalSeconds;
            int alpha = (int)(255 * Math.Clamp(age / 0.4, 0, 1));   // fades out over the last 0.4 s
            cv.Fill(br, Color.FromArgb(alpha * 230 / 255, 0x03, 0x05, 0x06));
            cv.Stroke(br, Color.FromArgb(alpha, orange), 1.2f);
            cv.Text(msg, br, TextKind.Banner, Color.FromArgb(alpha, orange), StringAlignment.Center);
        }
    }

    // ---- tuning from the spectrum ----

    /// <summary>Show the dongle's full 1.49 MHz instead of the 744 kHz HD baseband.</summary>
    public bool Wide
    {
        get => _c.Settings.PanelWideSpan;
        set { lock (Sync) { _c.Settings.PanelWideSpan = value; ClearWaterfall(); } }
    }

    private double HalfSpanKhz => Wide ? FmReceiver.DeviceRate / 2000 : FmReceiver.HdRate / 2000;

    /// <summary>The spectrum + waterfall area (design coordinates) that tunes on click.</summary>
    public RectangleF TuneArea { get; private set; }
    /// <summary>The span toggle in the title line.</summary>
    public RectangleF SpanToggle { get; private set; }
    private float _hoverX = float.NaN;
    /// <summary>Mouse x over the tune area (design coordinates), or null.</summary>
    public float? HoverX
    {
        get { float h = _hoverX; return float.IsNaN(h) ? null : h; }
        set => _hoverX = value ?? float.NaN;
    }
    private float _tuneX0;

    /// <summary>The channel (US 200 kHz grid) at a design x position in the spectrum.</summary>
    public long FrequencyAt(float x)
    {
        double khz = (x - Pan - _tuneX0) / SpecW * 2 * HalfSpanKhz - HalfSpanKhz;
        return Snap(_c.Frequency + (long)(khz * 1000));
    }

    private static long Snap(long hz)
    {
        long k = (long)Math.Round((hz - RadioEngine.FirstChannel) / (double)RadioEngine.ChannelStep);
        return Math.Clamp(RadioEngine.FirstChannel + k * RadioEngine.ChannelStep, RadioEngine.FirstChannel, RadioEngine.LastChannel);
    }

    // ---- retune motion: the view slides so the new station glides into the center ----

    private const double PanSeconds = 0.45;
    private float _panFrom;               // design px the view starts offset by (eases to 0)
    private DateTime _panStart = DateTime.MinValue, _ignoreRowsUntil = DateTime.MinValue, _bannerUntil = DateTime.MinValue;
    private long? _pending;               // channel the user clicked; highlighted while it slides in

    /// <summary>Current slide offset in design px (content drawn this far right of its true position).</summary>
    private float Pan
    {
        get
        {
            double t = (DateTime.UtcNow - _panStart).TotalSeconds / PanSeconds;
            if (t >= 1) { _pending = null; return 0; }
            double e = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
            return (float)(_panFrom * (1 - e));
        }
    }

    public bool Animating => (DateTime.UtcNow - _panStart).TotalSeconds < PanSeconds || DateTime.UtcNow < _bannerUntil;

    /// <summary>The user clicked a channel: remember it so its highlight travels with the slide.</summary>
    public void BeginTune(long hz)
    {
        lock (Sync) _pending = hz;
    }

    /// <summary>Notices a frequency change and shifts the history so it lines up with the new center.</summary>
    private void CheckRetune()
    {
        long f = _c.Frequency;
        if (f == _rowCenter) return;
        long delta = f - _rowCenter;
        bool first = _rowCenter == 0;
        _rowCenter = f;
        if (first) return;
        float px = (float)(delta / (2 * HalfSpanKhz * 1000) * SpecW);
        if (Math.Abs(px) >= SpecW) ClearWaterfall();
        else
        {
            int dx = -(int)Math.Round(px);   // tuning up moves the content left
            ShiftWaterfall(dx);
            ShiftSpectrum(dx);
            _panFrom = px;                   // ...but start drawn where it was, then glide
            _panStart = DateTime.UtcNow;
        }
        _ignoreRowsUntil = DateTime.UtcNow.AddSeconds(0.35);
        _bannerUntil = DateTime.UtcNow.AddSeconds(1.4);
    }

    private void ShiftWaterfall(int dx)
    {
        var px = _water.Pixels;
        var shifted = new int[SpecW];
        for (int y = 0; y < WaterH; y++)
        {
            var row = px.AsSpan(y * SpecW, SpecW);
            for (int x = 0; x < SpecW; x++)
            {
                int sx = x - dx;
                shifted[x] = sx >= 0 && sx < SpecW ? row[sx] : 0;
            }
            shifted.CopyTo(row);
        }
        _water.Version++;
    }

    private void ShiftSpectrum(int dx)
    {
        var old = (float[])_spec.Clone();
        for (int x = 0; x < SpecW; x++)
        {
            int sx = x - dx;
            _spec[x] = sx >= 0 && sx < SpecW ? old[sx] : _wfFloor;
        }
    }

    private void ClearWaterfall()
    {
        Array.Clear(_water.Pixels);
        _water.Version++;
        _newest = 0;
        _specValid = false;
        _wfScaled = false;
    }

    private bool _wfScaled;

    private void DrawMpx(IPanelCanvas cv, float x, float y, Color lit)
    {
        Title(cv, x, y, "FM MULTIPLEX  0–60 kHz", lit);
        var r = new RectangleF(x, y + 18, SpecW, 92);
        Frame(cv, r);
        float Px(double khz) => (float)(r.X + khz / 60 * SpecW);
        // zones
        void Zone(double a, double b, string label)
        {
            cv.Fill(RectangleF.FromLTRB(Px(a), r.Y, Px(b), r.Bottom), Color.FromArgb(14, lit));
            Small(cv, label, Px(a), r.Y + 2, Px(b) - Px(a), Color.FromArgb(150, lit), StringAlignment.Center);
        }
        Zone(0.03, 15, "L+R");
        Zone(23, 53, "L−R (stereo)");
        Zone(54.6, 59.4, "RDS");
        cv.Line(Px(19), r.Y, Px(19), r.Bottom, Color.FromArgb(120, 0xF7, 0x94, 0x1D), 1, DashStyle.Dash);
        Small(cv, "19k pilot", Px(19) + 2, r.Bottom - 14, 60, Color.FromArgb(200, 0xF7, 0x94, 0x1D), StringAlignment.Near);
        if (_mpxValid)
        {
            float top = _mpx.Max(), lo = top - 70;
            var pts = new PointF[SpecW];
            for (int c = 0; c < SpecW; c++) pts[c] = new PointF(r.X + c, r.Bottom - Math.Clamp((_mpx[c] - lo) / (top - lo), 0, 1) * (r.Height - 6));
            cv.Trace(pts, lit, 1.1f);
        }
        foreach (int k in new[] { 0, 15, 19, 38, 53, 57 })
            Small(cv, $"{k}k", Px(k) - 14, r.Bottom + 1, 28, Color.FromArgb(150, lit), StringAlignment.Center);
    }

    private static readonly Color MeterRed = Color.FromArgb(0xFF, 0x4A, 0x4A), MeterYellow = Color.FromArgb(0xFF, 0xC8, 0x30);

    private void DrawMeters(IPanelCanvas cv, float x, float y, Color lit, RadioEngine? eng)
    {
        var lv = eng?.Levels ?? (0, 0, 0, 0);
        bool hd = eng?.Blender.PlayingHd == true;
        Title(cv, x, y, $"AUDIO  ·  source {(eng == null ? "—" : hd ? "HD (digital)" : "FM (analog)")}  ·  RMS bar, peak tick, dBFS", lit);
        // scale -40..0 dBFS, one segment per dB; yellow above -9, red above -3
        const float Range = 40;
        int segs = 40;
        for (int ch = 0; ch < 2; ch++)
        {
            float rms = ch == 0 ? lv.Item1 : lv.Item2, peak = ch == 0 ? lv.Item3 : lv.Item4;
            float Frac(float v) => Math.Clamp((20 * MathF.Log10(Math.Max(v, 1e-5f)) + Range) / Range, 0, 1);
            var bar = new RectangleF(x + 18, y + 20 + ch * 14, SpecW - 18, 9);
            Small(cv, ch == 0 ? "L" : "R", x, bar.Y - 3, 14, lit, StringAlignment.Near);
            float sw = bar.Width / segs;
            int litSegs = (int)Math.Round(Frac(rms) * segs), peakSeg = (int)Math.Round(Frac(peak) * segs) - 1;
            for (int s = 0; s < segs; s++)
            {
                var c = s >= segs - 3 ? MeterRed : s >= segs - 9 ? MeterYellow : lit;
                bool on = s < litSegs;
                var fill = on ? c : s == peakSeg ? Color.FromArgb(220, c) : Color.FromArgb(22, lit);
                if (s == peakSeg && !on) cv.Fill(new RectangleF(bar.X + s * sw + sw / 2 - 2, bar.Y, 3, bar.Height), fill);
                else cv.Fill(new RectangleF(bar.X + s * sw, bar.Y, sw - 2, bar.Height), fill);
            }
        }
        foreach (int dbMark in new[] { -40, -30, -20, -10, -3, 0 })
            Small(cv, dbMark.ToString(), x + 18 + (dbMark + Range) / Range * (SpecW - 18) - 14, y + 46, 28, Color.FromArgb(150, lit), StringAlignment.Center);
    }

    private void DrawStats(Graphics g, float x, float y, float w, Color lit, RadioEngine? eng)
    {
        var lines = new List<(string k, string v, bool head)>();
        void H(string s) => lines.Add((s, "", true));
        void L(string k, string v) => lines.Add((k, v, false));
        if (eng == null)
        {
            H("RADIO");
            L("State", _c.Starting ? "starting" : _c.Error ?? "stopped");
        }
        else
        {
            var rx = eng.Receiver; var st = rx.Stereo; var rds = rx.Rds; var hd = eng.Hd; var b = eng.Blender; var p = eng.Player;
            H("RF");
            // (the value column fits about 30 characters, and every line here is spoken for)
            if (eng.Device.LinkStatus is string link) L("Source", link);   // network dongle: host and data rate
            else L("Device", eng.Device.Name);
            var perr = eng.MeasuredPpmError;
            L("Tuning", $"{eng.Frequency / 1e6:0.000} MHz  {eng.Ppm:+0;-0;0} ppm" +
                (perr is double pe ? $" err {pe:+0.0;-0.0}" : "") + (eng.BiasTee ? "  BIAS-T" : ""));
            var opt = eng.GainOptimizer;
            L("Gain", $"{eng.CurrentGainDb:0.0} dB  " + (eng.AutoGain
                ? $"{opt.State.ToString().ToLowerInvariant()}{(opt.Metric != "" ? " · " + opt.Metric : "")}"
                : "fixed"));
            L("ADC", opt.Overload ? $"⚠ OVERLOAD  {opt.Clipping * 100:0.00}% clipped" : $"clipping {opt.Clipping * 100:0.000}%  ok");
            L("Signal", $"{rx.ChannelPowerDb:0.0} dBFS  ripple {rx.Equalizer.Ripple:0.000}{(rx.Equalizer.Enabled ? "" : "  EQ off")}");
            H("FM");
            L("Pilot", st.PilotLocked ? $"lock  {st.PilotLevel * 100:0.0}%  SNR {st.PilotSnrDb:0.0} dB" : "—");
            L("Stereo", $"blend {st.Blend:0.00}{(st.ForceMono ? "  (forced mono)" : "")}");
            H("RDS");
            L("Station", rds.Pi >= 0 ? $"PI {rds.Pi:X4}  {rds.CallSign}  \"{rds.ProgramService}\"" : "—");
            L("Type", rds.PtyName is { Length: > 0 } pty ? $"{pty}{(rds.TrafficProgram ? "  TP" : "")}" : "—");
            L("Groups", rds.Synced ? $"{_groupsPerSec:0.0}/s  BLER {rds.BlockErrorRate:P0}" : "no sync");
            H("HD RADIO");
            // "weak": locked on (name, programs) but too many bit errors for the audio
            string sync = !hd.Synced ? "no" : eng.HdTooWeak ? "weak" : "yes";
            L("Sync", !hd.Synced ? "no" : hd.MerLower <= 0 && hd.MerUpper <= 0 ? $"{sync}  MER measuring…" : $"{sync}  MER {hd.MerLower:0.0} / {hd.MerUpper:0.0} dB");
            L("BER", hd.Synced ? $"{hd.Ber:0.00000}" : "—");
            L("Programs", hd.Programs.Count == 0 ? "—" : string.Join(" ", hd.Programs.Select(kv => $"HD{kv.Key + 1}{(kv.Key == eng.Program ? "*" : "")}")));
            L("Blend", eng.HdTooWeak ? "analog · HD too weak to play"
                : (b.PlayingHd ? "HD" : "analog") + (b.Aligned ? $"  lead {b.HdLeadSeconds:0.000} s  score {b.AlignScore:0.00}" : "  not aligned"));
            L("Loudness", $"HD gain {b.HdGain:0.00}" + (b.RetryIn > 0.5 ? $"  retry in {b.RetryIn:0} s" : ""));
            L("Data", (hd.DataServices ?? "") + (hd.FilesReceived > 0 ? $"{(hd.DataServices != null ? " · " : "")}{hd.FilesReceived} files  {hd.LastFile}" : hd.DataServices == null ? "—" : ""));
            H("AUDIO / CPU");
            L("Output", $"buffer {p.BufferedMs:0} ms  drift {p.DriftPpm:+0;-0} ppm");
            L("Glitches", $"{p.Underruns} underruns  {eng.HdDecoder.DroppedBlocks} HD drops");
            L("DSP", $"{eng.DspLoad * 100:0.0}% of a core  HD queue {eng.HdDecoder.Pending}");
        }
        using var kf = new Font("Consolas", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var hf = new Font("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var kb = new SolidBrush(Color.FromArgb(150, lit));
        using var vb = new SolidBrush(lit);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        float yy = y;
        foreach (var (k, v, head) in lines)
        {
            if (head)
            {
                if (yy > y) yy += 2;
                g.DrawString(k, hf, vb, new RectangleF(x, yy, w, 14), fmt);
                using (var pen = new Pen(Color.FromArgb(40, lit))) g.DrawLine(pen, x + g.MeasureString(k, hf, PointF.Empty, fmt).Width + 6, yy + 7, x + w, yy + 7);
                yy += 14;
                continue;
            }
            g.DrawString(k, kf, kb, new RectangleF(x, yy, 74, 14), fmt);
            g.DrawString(v, kf, vb, new RectangleF(x + 74, yy, w - 74, 14), fmt);
            yy += 12f;
        }
    }

    private void DrawTaps(Graphics g, float x, float y, float w, Color lit, RadioEngine? eng)
    {
        int n = eng?.Receiver.Equalizer.TapCount ?? 16;
        Title(g, x, y, "MULTIPATH EQUALIZER TAPS", lit);
        var r = new RectangleF(x, y + 18, w, 40);
        Frame(g, r);
        if (eng == null) return;
        float bw = r.Width / n, max = Math.Max(1e-3f, _taps.Take(n).Max());
        for (int k = 0; k < n; k++)
        {
            float v = _taps[k] / max;
            float bh = v * (r.Height - 6);
            using var b = new SolidBrush(k == n / 4 ? lit : Color.FromArgb(170, lit));   // the main tap
            g.FillRectangle(b, r.X + k * bw + 2, r.Bottom - 3 - bh, bw - 4, bh);
        }
        Small(g, $"{1e6 / FmReceiver.ChannelRate * n:0} µs span · echoes show as side taps", x, r.Bottom + 1, w, Color.FromArgb(150, lit), StringAlignment.Near);
    }

    private void DrawHistory(Graphics g, float x, float y, float w, Color lit)
    {
        Title(g, x, y, "LAST 60 s", lit);
        var data = _hist.ToArray();
        Spark(g, new RectangleF(x, y + 18, w, 20), "MER", data.Select(d => d.mer).ToArray(), 0, 20, "dB", lit);
        Spark(g, new RectangleF(x, y + 40, w, 20), "Pilot", data.Select(d => d.pilot).ToArray(), 10, 50, "dB", lit);
        Spark(g, new RectangleF(x, y + 62, w, 20), "Power", data.Select(d => d.power).ToArray(), -60, 0, "dBFS", lit);
    }

    private static void Spark(Graphics g, RectangleF r, string label, float[] v, float lo, float hi, string unit, Color lit)
    {
        Frame(g, r);
        Small(g, label, r.X + 4, r.Y + 3, 40, Color.FromArgb(150, lit), StringAlignment.Near);
        Small(g, v.Length > 0 && !float.IsNaN(v[^1]) ? $"{v[^1]:0.0} {unit}" : "—", r.X + 40, r.Y + 3, 64, lit, StringAlignment.Near);
        r = new RectangleF(r.X + 108, r.Y, r.Width - 108, r.Height);   // the trace gets the rest
        if (v.Length < 2) return;
        float dx = r.Width / (HistLen - 1);
        var seg = new List<PointF>();
        using var pen = new Pen(lit, 1.3f);
        for (int i = 0; i < v.Length; i++)
        {
            if (float.IsNaN(v[i])) { if (seg.Count > 1) g.DrawLines(pen, seg.ToArray()); seg.Clear(); continue; }
            float px = r.Right - (v.Length - 1 - i) * dx;
            float py = r.Bottom - 2 - Math.Clamp((v[i] - lo) / (hi - lo), 0, 1) * (r.Height - 4);
            seg.Add(new PointF(px, py));
        }
        if (seg.Count > 1) g.DrawLines(pen, seg.ToArray());
    }

    // ------------------------------------------------------------------ helpers

    private static void Title(Graphics g, float x, float y, string s, Color lit)
    {
        using var f = new Font("Segoe UI Semibold", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var b = new SolidBrush(lit);
        g.DrawString(s, f, b, x, y, StringFormat.GenericTypographic);
    }

    private static void Small(Graphics g, string s, float x, float y, float w, Color c, StringAlignment align)
    {
        using var f = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = align, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(s, f, b, new RectangleF(x, y, w, 14), fmt);
    }

    private static void Frame(Graphics g, RectangleF r, bool fill = true)
    {
        using var b = new SolidBrush(Color.FromArgb(0x03, 0x05, 0x06));
        using var pen = new Pen(Color.FromArgb(0x1E, 0x24, 0x2B), 1);
        if (fill) g.FillRectangle(b, r);
        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
    }

    public void Dispose() => _water.Dispose();
}
