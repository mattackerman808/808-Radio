using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Radio808.Avalonia.Drawing;
using Radio808.Core.Dsp;
using Radio808.Core.Radio;
using Radio808.Shared;
using static Radio808.Avalonia.Drawing.G;

namespace Radio808.Avalonia;

/// <summary>
/// The "behind the faceplate" instrument panel: baseband spectrum + waterfall, FM MPX spectrum, live stats, history
/// sparklines, the multipath equalizer's taps, and audio meters. Drawn in design coordinates through
/// <see cref="IPanelCanvas"/>. A port of the Windows app's NerdPanel: the sliding spectrum and waterfall are drawn by
/// <see cref="DrawSpectrumContent"/> into a clipped child visual; everything else by <see cref="Draw"/>.
/// </summary>
internal sealed class NerdPanel
{
    private const int SpecW = 592, WaterH = 150;

    private readonly RadioController _c;
    private readonly float[] _spec = new float[SpecW];
    private bool _specValid;
    private readonly WaterfallImage _water = new(SpecW, WaterH);
    private int _newest;
    private float _wfFloor = -60, _wfTop = -20;

    private readonly Fft _mpxFft = new(4096);
    private readonly float[] _mpxIn = new float[4096], _mpxIq = new float[8192], _re = new float[4096], _im = new float[4096], _mpxDb = new float[4096];
    private readonly float[] _mpx = new float[SpecW];
    private bool _mpxValid;

    private readonly float[] _taps = new float[64];
    private const int HistLen = 120;
    private readonly Queue<(float mer, float pilot, float power)> _hist = new();
    private DateTime _nextHist = DateTime.MinValue;
    private long _lastGroups = -1;
    private DateTime _lastGroupsAt;
    private double _groupsPerSec;

    private static readonly Color Orange = Rgb(0xF7, 0x94, 0x1D);
    private static readonly Color MeterRed = Rgb(0xFF, 0x4A, 0x4A), MeterYellow = Rgb(0xFF, 0xC8, 0x30);

    public NerdPanel(RadioController c) => _c = c;

    // ------------------------------------------------------------------ data in

    private const double RowSeconds = 0.05;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private double _specAt, _mpxAt, _lastRowAt;

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
        if (DateTime.UtcNow < _ignoreRowsUntil) return;
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
        Array.Copy(row, _sorted, SpecW);
        Array.Sort(_sorted);
        float floor = _sorted[SpecW / 5] - 3, top = Math.Max(_sorted[^1], floor + 25);
        float rate = _wfScaled ? 0.1f : 1f;
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
            _mpxFft.PowerDb(_mpxIq, _re, _im, _mpxDb);
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
        _specValid = _mpxValid = false;
        _hist.Clear();
        _lastGroups = -1;
    }

    // ------------------------------------------------------------------ layout

    private Rect _area;
    private double _lx, _rx, _rw;
    /// <summary>The spectrum + waterfall + axis, in design coordinates: the clipped child visual's rectangle.</summary>
    public Rect SpectrumClip { get; private set; }
    public Rect TuneArea { get; private set; }
    public Rect SpanToggle { get; private set; }
    private Rect _sr, _wr;

    public void Layout(Rect area)
    {
        _area = area;
        _lx = area.X + 12; _rx = area.X + 632; _rw = area.Right - _rx - 12;
        double y = area.Y + 8;
        _sr = new Rect(_lx, y + 18, SpecW, 100);
        _wr = new Rect(_lx, _sr.Bottom + 1, SpecW, WaterH);
        SpectrumClip = new Rect(_lx, _sr.Y, SpecW, _sr.Height + 1 + WaterH + 16);
        TuneArea = new Rect(_lx, area.Y + 26, SpecW, 100 + 1 + WaterH);
        SpanToggle = new Rect(_lx + SpecW - 140, area.Y + 6, 140, 16);
    }

    // ------------------------------------------------------------------ drawing

    private static void Title(IPanelCanvas cv, double x, double y, string s, Color lit) => cv.Text(s, new Rect(x, y, 900, 16), TextKind.Title, lit);
    private static void Small(IPanelCanvas cv, string s, double x, double y, double w, Color c, Align align) => cv.Text(s, new Rect(x, y, w, 14), TextKind.Small, c, align);
    private static void Frame(IPanelCanvas cv, Rect r, bool fill = true)
    {
        if (fill) cv.Fill(r, Rgb(0x03, 0x05, 0x06));
        cv.Stroke(r, Rgb(0x1E, 0x24, 0x2B));
    }

    /// <summary>Everything except the clipped spectrum content: titles, frames, multiplex, meters, stats, taps, history.</summary>
    public void Draw(IPanelCanvas cv, Rect area, Color lit)
    {
        Layout(area);
        var eng = _c.Engine;
        CheckRetune();
        double x = _lx, y = area.Y + 8;
        long f = _c.Frequency;
        Title(cv, x, y, $"SPECTRUM  ·  {f / 1e6:0.0} MHz  ·  click a station to tune, wheel to step", lit);
        double rEdge = x + SpecW;
        var dim = lit.With(90);
        Small(cv, "SPAN", rEdge - 140, y + 1, 34, dim, Align.Near);
        Small(cv, "1.5 MHz", rEdge - 104, y + 1, 44, Wide ? lit : dim, Align.Near);
        Small(cv, "|", rEdge - 56, y + 1, 8, dim, Align.Near);
        Small(cv, "744 kHz", rEdge - 44, y + 1, 44, Wide ? dim : lit, Align.Near);
        Frame(cv, _sr);
        DrawMpx(cv, x, area.Y + 296, lit);
        DrawMeters(cv, x, area.Y + 428, lit, eng);
        DrawStats(cv, _rx, area.Y + 8, _rw, lit, eng);
        DrawTaps(cv, _rx, area.Y + 316, _rw, lit, eng);
        DrawHistory(cv, _rx, area.Y + 388, _rw, lit);
    }

    /// <summary>The spectrum, waterfall and axis (design coordinates; the caller clips to <see cref="SpectrumClip"/>).</summary>
    public void DrawSpectrumContent(IPanelCanvas cv, Color lit)
    {
        long f = _c.Frequency;
        double half = HalfSpanKhz;
        var eng = _c.Engine;
        bool hdSynced = eng?.Hd.Synced == true, hdPlaying = eng?.Blender.PlayingHd == true;
        var sr = _sr; var wr = _wr;
        cv.Fill(sr, Rgb(0x03, 0x05, 0x06));
        double pan = Pan;
        cv.PushTranslate(pan, 0);
        double Px(double khz) => sr.X + (khz + half) / (2 * half) * SpecW;

        if (hdSynced)
        {
            var hdC = Orange.With(hdPlaying ? 48 : 24);
            cv.Fill(new Rect(Px(-198), sr.Y, Px(-129) - Px(-198), sr.Height), hdC);
            cv.Fill(new Rect(Px(129), sr.Y, Px(198) - Px(129), sr.Height), hdC);
        }
        var channels = new List<(long hz, double px)>();
        long firstCh = Snap(f - (long)(2 * half * 1000));
        for (long ch = firstCh; ch <= f + 2 * half * 1000; ch += RadioEngine.ChannelStep)
            channels.Add((ch, Px((ch - f) / 1000.0)));
        var gridC = lit.With(30);
        foreach (var (_, px) in channels) cv.Line(px, sr.Y, px, sr.Bottom, gridC, 1, Dash.Dot);
        if (_specValid)
        {
            float lo = _wfFloor, hi = _wfTop + 5;
            var pts = new Point[SpecW];
            for (int c = 0; c < SpecW; c++)
                pts[c] = new Point(sr.X + c, sr.Bottom - Math.Clamp((_spec[c] - lo) / (hi - lo), 0, 1) * (sr.Height - 4));
            cv.Area(pts, sr.Bottom, sr, lit.With(110), lit.With(10));
            cv.Trace(pts, lit, 1.2);
        }
        // waterfall, newest row at the top, gliding between rows
        double glide = _specValid ? Math.Clamp((Clock.Elapsed.TotalSeconds - _lastRowAt) / RowSeconds, 0, 1) : 0;
        int first = WaterH - _newest;
        cv.Waterfall(_water, _newest, first, new Rect(wr.X, wr.Y + glide, SpecW, first));
        cv.Waterfall(_water, 0, _newest, new Rect(wr.X, wr.Y + glide + first, SpecW, _newest));
        if (glide > 0) cv.Waterfall(_water, _newest, 1, new Rect(wr.X, wr.Y, SpecW, glide));
        // (the row that glided past the bottom is hidden by the axis strip)
        cv.Fill(new Rect(wr.X - 1, wr.Bottom, SpecW + 2, 17), Rgb(0x05, 0x08, 0x0A));
        foreach (var (hz, px) in channels)
            Small(cv, (hz / 1e6).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), px - 18, wr.Bottom + 1, 36, hz == Snap(f) ? Orange : lit.With(140), Align.Center);
        if (hdSynced)
        {
            Small(cv, "HD", Px(-163) - 10, sr.Y + 2, 20, Orange, Align.Center);
            Small(cv, "HD", Px(163) - 10, sr.Y + 2, 20, Orange, Align.Center);
        }
        cv.Line(Px(0), sr.Y, Px(0), wr.Bottom, Orange.With(170), 1.4);
        if (_pending is long pend)
        {
            double a = Px((pend - f) / 1000.0 - 100), b = Px((pend - f) / 1000.0 + 100);
            cv.Fill(new Rect(a, sr.Y, b - a, sr.Height + 1 + WaterH), lit.With(55));
        }
        cv.PopTranslate();
        Frame(cv, wr, fill: false);

        if (_pending == null && HoverX is double hx && hx >= sr.X && hx <= sr.Right)
        {
            long ch = FrequencyAt(hx);
            double a = Px((ch - f) / 1000.0 - 100), b = Px((ch - f) / 1000.0 + 100);
            double ca = Math.Max(a, sr.X), cb = Math.Min(b, sr.Right);
            if (cb > ca) cv.Fill(new Rect(ca, sr.Y, cb - ca, sr.Height + 1 + WaterH), lit.With(40));
            cv.Line(hx, sr.Y, hx, wr.Bottom, lit.With(200));
            string label = ch == Snap(f) ? $"{ch / 1e6:0.0} MHz (tuned)" : $"{ch / 1e6:0.0} MHz  ·  click to tune";
            double w = 150, lx = Math.Clamp(hx + 6, sr.X + 2, sr.Right - w - 2);
            cv.Fill(new Rect(lx - 2, sr.Y + 16, w, 15), Argb(225, 0x03, 0x05, 0x06));
            Small(cv, label, lx, sr.Y + 17, w - 4, lit, Align.Near);
        }
        if (DateTime.UtcNow < _bannerUntil)
        {
            string msg = $"TUNED  {f / 1e6:0.0} MHz";
            double bw = cv.MeasureText(msg, TextKind.Banner) + 24;
            var br = new Rect(sr.X + (SpecW - bw) / 2, sr.Y + 6, bw, 22);
            double age = (_bannerUntil - DateTime.UtcNow).TotalSeconds;
            int alpha = (int)(255 * Math.Clamp(age / 0.4, 0, 1));
            cv.Fill(br, Argb((byte)(alpha * 230 / 255), 0x03, 0x05, 0x06));
            cv.Stroke(br, Orange.With(alpha), 1.2);
            cv.Text(msg, br, TextKind.Banner, Orange.With(alpha), Align.Center);
        }
    }

    // ---- tuning from the spectrum ----

    public bool Wide
    {
        get => _c.Settings.PanelWideSpan;
        set { _c.Settings.PanelWideSpan = value; ClearWaterfall(); }
    }

    private double HalfSpanKhz => Wide ? FmReceiver.DeviceRate / 2000 : FmReceiver.HdRate / 2000;

    /// <summary>Mouse x over the tune area (design coordinates), or null.</summary>
    public double? HoverX { get; set; }

    public long FrequencyAt(double x)
    {
        double khz = (x - Pan - _lx) / SpecW * 2 * HalfSpanKhz - HalfSpanKhz;
        return Snap(_c.Frequency + (long)(khz * 1000));
    }

    private static long Snap(long hz)
    {
        long k = (long)Math.Round((hz - RadioEngine.FirstChannel) / (double)RadioEngine.ChannelStep);
        return Math.Clamp(RadioEngine.FirstChannel + k * RadioEngine.ChannelStep, RadioEngine.FirstChannel, RadioEngine.LastChannel);
    }

    private const double PanSeconds = 0.45;
    private double _panFrom;
    private DateTime _panStart = DateTime.MinValue, _ignoreRowsUntil = DateTime.MinValue, _bannerUntil = DateTime.MinValue;
    private long? _pending;

    private double Pan
    {
        get
        {
            double t = (DateTime.UtcNow - _panStart).TotalSeconds / PanSeconds;
            if (t >= 1) { _pending = null; return 0; }
            double e = 1 - Math.Pow(1 - t, 3);
            return _panFrom * (1 - e);
        }
    }

    public bool Animating => (DateTime.UtcNow - _panStart).TotalSeconds < PanSeconds || DateTime.UtcNow < _bannerUntil;

    public void BeginTune(long hz) => _pending = hz;

    private void CheckRetune()
    {
        long f = _c.Frequency;
        if (f == _rowCenter) return;
        long delta = f - _rowCenter;
        bool first = _rowCenter == 0;
        _rowCenter = f;
        if (first) return;
        double px = delta / (2 * HalfSpanKhz * 1000) * SpecW;
        if (Math.Abs(px) >= SpecW) ClearWaterfall();
        else
        {
            int dx = -(int)Math.Round(px);
            ShiftWaterfall(dx);
            ShiftSpectrum(dx);
            _panFrom = px;
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

    private void DrawMpx(IPanelCanvas cv, double x, double y, Color lit)
    {
        Title(cv, x, y, "FM MULTIPLEX  0–60 kHz", lit);
        var r = new Rect(x, y + 18, SpecW, 92);
        Frame(cv, r);
        double Px(double khz) => r.X + khz / 60 * SpecW;
        void Zone(double a, double b, string label)
        {
            cv.Fill(new Rect(Px(a), r.Y, Px(b) - Px(a), r.Height), lit.With(14));
            Small(cv, label, Px(a), r.Y + 2, Px(b) - Px(a), lit.With(150), Align.Center);
        }
        Zone(0.03, 15, "L+R");
        Zone(23, 53, "L−R (stereo)");
        Zone(54.6, 59.4, "RDS");
        cv.Line(Px(19), r.Y, Px(19), r.Bottom, Orange.With(120), 1, Dash.DashLine);
        Small(cv, "19k pilot", Px(19) + 2, r.Bottom - 14, 60, Orange.With(200), Align.Near);
        if (_mpxValid)
        {
            float top = _mpx.Max(), lo = top - 70;
            var pts = new Point[SpecW];
            for (int c = 0; c < SpecW; c++) pts[c] = new Point(r.X + c, r.Bottom - Math.Clamp((_mpx[c] - lo) / (top - lo), 0, 1) * (r.Height - 6));
            cv.Trace(pts, lit, 1.1);
        }
        foreach (int k in new[] { 0, 15, 19, 38, 53, 57 })
            Small(cv, $"{k}k", Px(k) - 14, r.Bottom + 1, 28, lit.With(150), Align.Center);
    }

    private void DrawMeters(IPanelCanvas cv, double x, double y, Color lit, RadioEngine? eng)
    {
        var lv = eng?.Levels ?? (0, 0, 0, 0);
        bool hd = eng?.Blender.PlayingHd == true;
        Title(cv, x, y, $"AUDIO  ·  source {(eng == null ? "—" : hd ? "HD (digital)" : "FM (analog)")}  ·  RMS bar, peak tick, dBFS", lit);
        const float Range = 40;
        int segs = 40;
        for (int ch = 0; ch < 2; ch++)
        {
            float rms = ch == 0 ? lv.Item1 : lv.Item2, peak = ch == 0 ? lv.Item3 : lv.Item4;
            float Frac(float v) => Math.Clamp((20 * MathF.Log10(Math.Max(v, 1e-5f)) + Range) / Range, 0, 1);
            var bar = new Rect(x + 18, y + 20 + ch * 14, SpecW - 18, 9);
            Small(cv, ch == 0 ? "L" : "R", x, bar.Y - 3, 14, lit, Align.Near);
            double sw = bar.Width / segs;
            int litSegs = (int)Math.Round(Frac(rms) * segs), peakSeg = (int)Math.Round(Frac(peak) * segs) - 1;
            for (int s = 0; s < segs; s++)
            {
                var c = s >= segs - 3 ? MeterRed : s >= segs - 9 ? MeterYellow : lit;
                bool on = s < litSegs;
                var fill = on ? c : s == peakSeg ? c.With(220) : lit.With(22);
                if (s == peakSeg && !on) cv.Fill(new Rect(bar.X + s * sw + sw / 2 - 2, bar.Y, 3, bar.Height), fill);
                else cv.Fill(new Rect(bar.X + s * sw, bar.Y, sw - 2, bar.Height), fill);
            }
        }
        foreach (int dbMark in new[] { -40, -30, -20, -10, -3, 0 })
            Small(cv, dbMark.ToString(), x + 18 + (dbMark + Range) / Range * (SpecW - 18) - 14, y + 46, 28, lit.With(150), Align.Center);
    }

    private void DrawStats(IPanelCanvas cv, double x, double y, double w, Color lit, RadioEngine? eng)
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
            if (eng.Device.LinkStatus is string link) L("Source", link);
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
        double yy = y;
        foreach (var (k, v, head) in lines)
        {
            if (head)
            {
                if (yy > y) yy += 2;
                cv.Text(k, new Rect(x, yy, w, 14), TextKind.Heading, lit);
                cv.Line(x + cv.MeasureText(k, TextKind.Heading) + 6, yy + 7, x + w, yy + 7, lit.With(40));
                yy += 14;
                continue;
            }
            cv.Text(k, new Rect(x, yy, 74, 14), TextKind.Mono, lit.With(150));
            cv.Text(v, new Rect(x + 74, yy, w - 74, 14), TextKind.Mono, lit);
            yy += 12;
        }
    }

    private void DrawTaps(IPanelCanvas cv, double x, double y, double w, Color lit, RadioEngine? eng)
    {
        int n = eng?.Receiver.Equalizer.TapCount ?? 16;
        Title(cv, x, y, "MULTIPATH EQUALIZER TAPS", lit);
        var r = new Rect(x, y + 18, w, 40);
        Frame(cv, r);
        if (eng == null) return;
        double bw = r.Width / n;
        float max = Math.Max(1e-3f, _taps.Take(n).Max());
        for (int k = 0; k < n; k++)
        {
            double v = _taps[k] / max;
            double bh = v * (r.Height - 6);
            cv.Fill(new Rect(r.X + k * bw + 2, r.Bottom - 3 - bh, bw - 4, bh), k == n / 4 ? lit : lit.With(170));
        }
        Small(cv, $"{1e6 / FmReceiver.ChannelRate * n:0} µs span · echoes show as side taps", x, r.Bottom + 1, w, lit.With(150), Align.Near);
    }

    private void DrawHistory(IPanelCanvas cv, double x, double y, double w, Color lit)
    {
        Title(cv, x, y, "LAST 60 s", lit);
        var data = _hist.ToArray();
        Spark(cv, new Rect(x, y + 18, w, 20), "MER", data.Select(d => d.mer).ToArray(), 0, 20, "dB", lit);
        Spark(cv, new Rect(x, y + 40, w, 20), "Pilot", data.Select(d => d.pilot).ToArray(), 10, 50, "dB", lit);
        Spark(cv, new Rect(x, y + 62, w, 20), "Power", data.Select(d => d.power).ToArray(), -60, 0, "dBFS", lit);
    }

    private static void Spark(IPanelCanvas cv, Rect r, string label, float[] v, float lo, float hi, string unit, Color lit)
    {
        Frame(cv, r);
        Small(cv, label, r.X + 4, r.Y + 3, 40, lit.With(150), Align.Near);
        Small(cv, v.Length > 0 && !float.IsNaN(v[^1]) ? $"{v[^1]:0.0} {unit}" : "—", r.X + 40, r.Y + 3, 64, lit, Align.Near);
        r = new Rect(r.X + 108, r.Y, r.Width - 108, r.Height);
        if (v.Length < 2) return;
        double dx = r.Width / (HistLen - 1);
        var seg = new List<Point>();
        for (int i = 0; i < v.Length; i++)
        {
            if (float.IsNaN(v[i])) { if (seg.Count > 1) cv.Trace(seg.ToArray(), lit, 1.3); seg.Clear(); continue; }
            double px = r.Right - (v.Length - 1 - i) * dx;
            double py = r.Bottom - 2 - Math.Clamp((v[i] - lo) / (hi - lo), 0, 1) * (r.Height - 4);
            seg.Add(new Point(px, py));
        }
        if (seg.Count > 1) cv.Trace(seg.ToArray(), lit, 1.3);
    }
}
