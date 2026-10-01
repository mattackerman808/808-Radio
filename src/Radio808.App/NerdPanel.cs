using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using Radio808.Core.Dsp;
using Radio808.Core.Radio;

namespace Radio808.App;

/// <summary>
/// The "behind the faceplate" instrument panel: baseband spectrum + waterfall, FM MPX spectrum, live stats,
/// history sparklines, the multipath equalizer's taps, and audio meters. Drawn in design coordinates.
/// </summary>
internal sealed class NerdPanel : IDisposable
{
    private const int SpecW = 592, WaterH = 150;

    private readonly RadioController _c;
    private readonly float[] _spec = new float[SpecW];
    private bool _specValid;
    private readonly Bitmap _water = new(SpecW, WaterH, PixelFormat.Format32bppRgb);
    private int _waterRow;   // next row to write (circular)
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

    // ------------------------------------------------------------------ data in

    /// <summary>A new baseband power spectrum (4096 bins, dB, -fs/2 .. +fs/2).</summary>
    public void AddBaseband(float[] db)
    {
        int per = db.Length / SpecW;
        var row = new float[SpecW];
        for (int c = 0; c < SpecW; c++)
        {
            float sum = 0;
            for (int k = 0; k < per; k++) sum += MathF.Pow(10, db[c * per + k] / 10);
            row[c] = 10 * MathF.Log10(sum / per + 1e-20f);
            _spec[c] = _specValid ? _spec[c] + 0.4f * (row[c] - _spec[c]) : row[c];
        }
        _specValid = true;
        // waterfall scale tracks the noise floor and the peak slowly
        var sorted = (float[])row.Clone();
        Array.Sort(sorted);
        _wfFloor += 0.1f * (sorted[SpecW / 5] - 3 - _wfFloor);
        _wfTop += 0.1f * (Math.Max(sorted[^1], _wfFloor + 25) - _wfTop);
        WriteWaterfallRow(row);
    }

    private void WriteWaterfallRow(float[] row)
    {
        var data = _water.LockBits(new Rectangle(0, _waterRow, SpecW, 1), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        var px = new int[SpecW];
        for (int c = 0; c < SpecW; c++) px[c] = Heat((row[c] - _wfFloor) / (_wfTop - _wfFloor));
        Marshal.Copy(px, 0, data.Scan0, SpecW);
        _water.UnlockBits(data);
        _waterRow = (_waterRow + 1) % WaterH;
    }

    /// <summary>Classic SDR waterfall palette: black, blue, cyan, yellow, red, white.</summary>
    private static int Heat(float v)
    {
        v = Math.Clamp(v, 0, 1);
        (float p, int r, int g, int b)[] stops = { (0, 0, 0, 0), (0.2f, 0, 0, 110), (0.45f, 0, 170, 230), (0.65f, 240, 230, 40), (0.85f, 255, 60, 20), (1, 255, 255, 255) };
        for (int i = 1; i < stops.Length; i++)
            if (v <= stops[i].p)
            {
                var a = stops[i - 1]; var b = stops[i];
                float t = (v - a.p) / (b.p - a.p);
                int r = (int)(a.r + (b.r - a.r) * t), g = (int)(a.g + (b.g - a.g) * t), bl = (int)(a.b + (b.b - a.b) * t);
                return (r << 16) | (g << 8) | bl;
            }
        return 0xFFFFFF;
    }

    /// <summary>Pulls the MPX spectrum and samples the history (call at ~10 Hz).</summary>
    public void Tick(RadioEngine? eng)
    {
        if (eng == null) return;
        if (eng.TryGetMpxBlock(_mpxIn))
        {
            for (int i = 0; i < 4096; i++) { _mpxIq[2 * i] = _mpxIn[i]; _mpxIq[2 * i + 1] = 0; }
            _mpxFft.PowerDb(_mpxIq, _re, _im, _mpxDb);   // index 2048 = DC
            double binHz = FmReceiver.MpxRate / 4096;
            for (int c = 0; c < SpecW; c++)
            {
                double f0 = c * 60_000.0 / SpecW, f1 = (c + 1) * 60_000.0 / SpecW;
                float m = -200;
                for (int k = (int)(f0 / binHz); k <= (int)(f1 / binHz); k++) m = Math.Max(m, _mpxDb[2048 + k]);
                _mpx[c] = _mpxValid ? _mpx[c] + 0.35f * (m - _mpx[c]) : m;
            }
            _mpxValid = true;
        }
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

    // ------------------------------------------------------------------ drawing

    public void Draw(Graphics g, RectangleF area, Color lit)
    {
        var eng = _c.Engine;
        float lx = area.X + 12, rx = area.X + 632;
        DrawBaseband(g, lx, area.Y + 8, lit);
        DrawMpx(g, lx, area.Y + 296, lit);
        DrawMeters(g, lx, area.Y + 428, lit, eng);
        DrawStats(g, rx, area.Y + 8, area.Right - rx - 12, lit, eng);
        DrawTaps(g, rx, area.Y + 316, area.Right - rx - 12, lit, eng);
        DrawHistory(g, rx, area.Y + 388, area.Right - rx - 12, lit);
    }

    private void DrawBaseband(Graphics g, float x, float y, Color lit)
    {
        long f = _c.Frequency;
        Title(g, x, y, $"BASEBAND  ±372 kHz  ·  {f / 1e6:0.000} MHz", lit);
        var sr = new RectangleF(x, y + 18, SpecW, 100);
        Frame(g, sr);
        // markers: analog channel and HD sidebands
        float Px(double khz) => (float)(sr.X + (khz + 372.09) / 744.1875 * SpecW);
        using (var hdB = new SolidBrush(Color.FromArgb(26, 0xF7, 0x94, 0x1D)))
        {
            g.FillRectangle(hdB, Px(-198), sr.Y, Px(-129) - Px(-198), sr.Height + 1 + WaterH);
            g.FillRectangle(hdB, Px(129), sr.Y, Px(198) - Px(129), sr.Height + 1 + WaterH);
        }
        using (var grid = new Pen(Color.FromArgb(30, lit), 1) { DashStyle = DashStyle.Dot })
            for (int k = -300; k <= 300; k += 100)
                g.DrawLine(grid, Px(k), sr.Y, Px(k), sr.Bottom);
        if (_specValid)
        {
            float lo = _wfFloor, hi = _wfTop + 5;
            var pts = new PointF[SpecW];
            for (int c = 0; c < SpecW; c++)
                pts[c] = new PointF(sr.X + c, sr.Bottom - Math.Clamp((_spec[c] - lo) / (hi - lo), 0, 1) * (sr.Height - 4));
            var poly = new List<PointF>(pts) { new(sr.Right, sr.Bottom), new(sr.X, sr.Bottom) };
            using (var fill = new LinearGradientBrush(sr, Color.FromArgb(110, lit), Color.FromArgb(10, lit), 90f)) g.FillPolygon(fill, poly.ToArray());
            using (var pen = new Pen(lit, 1.2f)) g.DrawLines(pen, pts);
        }
        // waterfall: newest row at the top
        var wr = new RectangleF(x, sr.Bottom + 1, SpecW, WaterH);
        int newest = (_waterRow - 1 + WaterH) % WaterH;
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        // rows newest..0 then WaterH-1..newest+1, drawn top-down
        int topRows = newest + 1;
        DrawFlipped(g, new Rectangle(0, 0, SpecW, topRows), wr.X, wr.Y);
        if (topRows < WaterH) DrawFlipped(g, new Rectangle(0, topRows, SpecW, WaterH - topRows), wr.X, wr.Y + topRows);
        g.Restore(state);
        Frame(g, wr, fill: false);
        // axis labels
        foreach (int k in new[] { -300, -200, -100, 0, 100, 200, 300 })
            Small(g, k == 0 ? "0" : $"{k:+0;-0}k", Px(k) - 16, wr.Bottom + 1, 32, Color.FromArgb(150, lit), StringAlignment.Center);
        Small(g, "HD", Px(-163) - 10, sr.Y + 2, 20, Color.FromArgb(200, 0xF7, 0x94, 0x1D), StringAlignment.Center);
        Small(g, "HD", Px(163) - 10, sr.Y + 2, 20, Color.FromArgb(200, 0xF7, 0x94, 0x1D), StringAlignment.Center);
    }

    /// <summary>Draws a band of waterfall rows upside down (so newer rows end up above older ones).</summary>
    private void DrawFlipped(Graphics g, Rectangle src, float x, float y)
    {
        // rows in the bitmap grow downward with time; on screen the newest goes at the top
        var dest = new[] { new PointF(x, y + src.Height), new PointF(x + src.Width, y + src.Height), new PointF(x, y) };
        g.DrawImage(_water, dest, src, GraphicsUnit.Pixel);
    }

    private void DrawMpx(Graphics g, float x, float y, Color lit)
    {
        Title(g, x, y, "FM MULTIPLEX  0–60 kHz", lit);
        var r = new RectangleF(x, y + 18, SpecW, 92);
        Frame(g, r);
        float Px(double khz) => (float)(r.X + khz / 60 * SpecW);
        // zones
        void Zone(double a, double b, string label)
        {
            using var br = new SolidBrush(Color.FromArgb(14, lit));
            g.FillRectangle(br, Px(a), r.Y, Px(b) - Px(a), r.Height);
            Small(g, label, Px(a), r.Y + 2, Px(b) - Px(a), Color.FromArgb(150, lit), StringAlignment.Center);
        }
        Zone(0.03, 15, "L+R");
        Zone(23, 53, "L−R (stereo)");
        Zone(54.6, 59.4, "RDS");
        using (var p = new Pen(Color.FromArgb(120, 0xF7, 0x94, 0x1D), 1) { DashStyle = DashStyle.Dash })
            g.DrawLine(p, Px(19), r.Y, Px(19), r.Bottom);
        Small(g, "19k pilot", Px(19) + 2, r.Bottom - 14, 60, Color.FromArgb(200, 0xF7, 0x94, 0x1D), StringAlignment.Near);
        if (_mpxValid)
        {
            float top = _mpx.Max(), lo = top - 70;
            var pts = new PointF[SpecW];
            for (int c = 0; c < SpecW; c++) pts[c] = new PointF(r.X + c, r.Bottom - Math.Clamp((_mpx[c] - lo) / (top - lo), 0, 1) * (r.Height - 6));
            using var pen = new Pen(lit, 1.1f);
            g.DrawLines(pen, pts);
        }
        foreach (int k in new[] { 0, 15, 19, 38, 53, 57 })
            Small(g, $"{k}k", Px(k) - 14, r.Bottom + 1, 28, Color.FromArgb(150, lit), StringAlignment.Center);
    }

    private void DrawMeters(Graphics g, float x, float y, Color lit, RadioEngine? eng)
    {
        var lv = eng?.Levels ?? (0, 0, 0, 0);
        bool hd = eng?.Blender.PlayingHd == true;
        Title(g, x, y, $"AUDIO  ·  source {(eng == null ? "—" : hd ? "HD (digital)" : "FM (analog)")}  ·  RMS bar, peak tick, dBFS", lit);
        // scale -40..0 dBFS, one segment per dB; yellow above -9, red above -3
        const float Range = 40;
        int segs = 40;
        for (int ch = 0; ch < 2; ch++)
        {
            float rms = ch == 0 ? lv.Item1 : lv.Item2, peak = ch == 0 ? lv.Item3 : lv.Item4;
            float Frac(float v) => Math.Clamp((20 * MathF.Log10(Math.Max(v, 1e-5f)) + Range) / Range, 0, 1);
            var bar = new RectangleF(x + 18, y + 20 + ch * 14, SpecW - 18, 9);
            Small(g, ch == 0 ? "L" : "R", x, bar.Y - 3, 14, lit, StringAlignment.Near);
            float sw = bar.Width / segs;
            int litSegs = (int)Math.Round(Frac(rms) * segs), peakSeg = (int)Math.Round(Frac(peak) * segs) - 1;
            for (int s = 0; s < segs; s++)
            {
                var c = s >= segs - 3 ? Color.FromArgb(0xFF, 0x4A, 0x4A) : s >= segs - 9 ? Color.FromArgb(0xFF, 0xC8, 0x30) : lit;
                bool on = s < litSegs;
                using var b = new SolidBrush(on ? c : s == peakSeg ? Color.FromArgb(220, c) : Color.FromArgb(22, lit));
                if (s == peakSeg && !on) g.FillRectangle(b, bar.X + s * sw + sw / 2 - 2, bar.Y, 3, bar.Height);
                else g.FillRectangle(b, bar.X + s * sw, bar.Y, sw - 2, bar.Height);
            }
        }
        foreach (int dbMark in new[] { -40, -30, -20, -10, -3, 0 })
            Small(g, dbMark.ToString(), x + 18 + (dbMark + Range) / Range * (SpecW - 18) - 14, y + 46, 28, Color.FromArgb(150, lit), StringAlignment.Center);
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
            L("Device", eng.Device.Name);
            L("Tuning", $"{eng.Frequency / 1e6:0.000} MHz  @ {FmReceiver.DeviceRate / 1e6:0.000000} MS/s");
            L("Gain", eng.Device.Gain is double gdb ? $"{gdb:0.0} dB" : "auto");
            L("Channel", $"{rx.ChannelPowerDb:0.0} dBFS");
            L("Multipath", $"ripple {rx.Equalizer.Ripple:0.000}  EQ {(rx.Equalizer.Enabled ? "on" : "off")}");
            H("FM");
            L("Pilot", st.PilotLocked ? $"lock  {st.PilotLevel * 100:0.0}%  SNR {st.PilotSnrDb:0.0} dB" : "—");
            L("Stereo", $"blend {st.Blend:0.00}{(st.ForceMono ? "  (forced mono)" : "")}");
            H("RDS");
            L("Station", rds.Pi >= 0 ? $"PI {rds.Pi:X4}  {rds.CallSign}  \"{rds.ProgramService}\"" : "—");
            L("Type", rds.PtyName is { Length: > 0 } pty ? $"{pty}{(rds.TrafficProgram ? "  TP" : "")}" : "—");
            L("Groups", rds.Synced ? $"{_groupsPerSec:0.0}/s  BLER {rds.BlockErrorRate:P0}" : "no sync");
            H("HD RADIO");
            L("Sync", hd.Synced ? $"yes  MER {hd.MerLower:0.0} / {hd.MerUpper:0.0} dB" : "no");
            L("BER", hd.Synced ? $"{hd.Ber:0.00000}" : "—");
            L("Programs", hd.Programs.Count == 0 ? "—" : string.Join(" ", hd.Programs.Select(kv => $"HD{kv.Key + 1}{(kv.Key == eng.Program ? "*" : "")}")));
            L("Blend", (b.PlayingHd ? "HD" : "analog") + (b.Aligned ? $"  lead {b.HdLeadSeconds:0.000} s  score {b.AlignScore:0.00}" : "  not aligned"));
            L("Loudness", $"HD gain {b.HdGain:0.00}" + (b.RetryIn > 0.5 ? $"  retry in {b.RetryIn:0} s" : ""));
            L("Data", hd.FilesReceived > 0 ? $"{hd.FilesReceived} files  {hd.LastFile}" : "—");
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
