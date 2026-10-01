using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Radio808.Core.Dsp;
using Radio808.Core.Hd;
using Radio808.Core.Radio;

namespace Radio808.App;

/// <summary>
/// The whole radio face, custom painted on a 960 x 600 design canvas that scales to the window. Everything
/// clickable registers a hit rectangle while painting.
/// </summary>
internal sealed class RadioView : Control
{
    private const float W = 960, H = 600;

    private static readonly Color Bg1 = Color.FromArgb(0x0C, 0x0F, 0x14), Bg2 = Color.FromArgb(0x17, 0x1C, 0x25);
    private static readonly Color Panel = Color.FromArgb(0x1B, 0x21, 0x2B), PanelHi = Color.FromArgb(0x26, 0x2E, 0x3B);
    private static readonly Color Ink = Color.FromArgb(0xEC, 0xEF, 0xF3), Dim = Color.FromArgb(0x8E, 0x98, 0xA7);
    private static readonly Color Faint = Color.FromArgb(0x48, 0x51, 0x5F), Line = Color.FromArgb(0x2E, 0x36, 0x43);
    private static readonly Color Orange = BrandMark.Orange, Blue = Color.FromArgb(0x2D, 0x8C, 0xFF);
    private static readonly Color Red = Color.FromArgb(0xE5, 0x48, 0x4D), Green = Color.FromArgb(0x3D, 0xD6, 0x8C);

    private readonly RadioController _c;
    private float _scale = 1, _ox, _oy;

    private sealed record Hit(RectangleF R, string Id, Action? Click, Action? RightClick = null);
    private readonly List<Hit> _hits = new();
    private string? _hover, _pressed;
    private bool _dragDial, _dragVol, _longPressFired;
    private long _dragFreq;
    private RectangleF _dialRect, _volRect;
    private readonly Timer _longPress = new() { Interval = 650 };
    private Action? _longPressAction;

    private byte[]? _artKey, _logoKey;
    private Image? _art, _logo;

    private readonly Fft _fft = new(4096);
    private readonly float[] _specIq = new float[8192], _re = new float[4096], _im = new float[4096], _db = new float[4096];
    private const int SpecCols = 220;
    private readonly float[] _spec = new float[SpecCols];
    private bool _specValid;

    public RadioView(RadioController c)
    {
        _c = c;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _longPress.Tick += (_, _) =>
        {
            _longPress.Stop();
            _longPressFired = true;
            _longPressAction?.Invoke();
            Invalidate();
        };
    }

    // ------------------------------------------------------------------ data

    /// <summary>Pulls a spectrum block from the engine if one is ready (call from a UI timer).</summary>
    public void PullSpectrum()
    {
        var eng = _c.Engine;
        if (eng == null) { _specValid = false; return; }
        if (!eng.TryGetSpectrumBlock(_specIq)) return;
        _fft.PowerDb(_specIq, _re, _im, _db);
        int per = 4096 / SpecCols;
        for (int c = 0; c < SpecCols; c++)
        {
            float sum = 0;
            for (int k = 0; k < per; k++) sum += MathF.Pow(10, _db[c * per + k] / 10);
            float v = 10 * MathF.Log10(sum / per);
            _spec[c] = _specValid ? _spec[c] + 0.35f * (v - _spec[c]) : v;
        }
        _specValid = true;
    }

    private static Image? Decode(byte[]? bytes, ref byte[]? key, ref Image? cache)
    {
        if (ReferenceEquals(bytes, key)) return cache;
        key = bytes;
        cache?.Dispose();
        cache = null;
        if (bytes == null) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            cache = new Bitmap(img);
        }
        catch { cache = null; }
        return cache;
    }

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new LinearGradientBrush(ClientRectangle.Width > 0 && ClientRectangle.Height > 0 ? ClientRectangle : new Rectangle(0, 0, 1, 1), Bg2, Bg1, 90f))
            g.FillRectangle(bg, ClientRectangle);
        _scale = Math.Min(Width / W, Height / H);
        if (_scale <= 0) return;
        _ox = (Width - W * _scale) / 2;
        _oy = (Height - H * _scale) / 2;
        g.TranslateTransform(_ox, _oy);
        g.ScaleTransform(_scale, _scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        _hits.Clear();

        var s = Snapshot();
        DrawHeader(g, s);
        DrawArt(g, s);
        DrawInfo(g, s);
        DrawPrograms(g, s);
        DrawTuner(g, s);
        DrawPresets(g, s);
        DrawBottom(g, s);
    }

    private sealed class State
    {
        public RadioEngine? Eng;
        public HdStatus? Hd;
        public RdsStatus? Rds;
        public long Freq;
        public bool HdSynced, PlayingHd, Stereo;
        public double PowerDb = -100;
    }

    private State Snapshot()
    {
        var eng = _c.Engine;
        var s = new State { Eng = eng, Freq = _dragDial ? _dragFreq : _c.Frequency };
        if (eng != null)
        {
            s.Hd = eng.Hd;
            s.Rds = eng.Receiver.Rds;
            s.HdSynced = s.Hd.Synced;
            s.PlayingHd = eng.Blender.PlayingHd;
            s.Stereo = s.PlayingHd || (eng.Receiver.Stereo.PilotLocked && eng.Receiver.Stereo.Blend > 0.5f);
            s.PowerDb = eng.Receiver.ChannelPowerDb;
        }
        return s;
    }

    private void DrawHeader(Graphics g, State s)
    {
        BrandMark.Draw(g, 24, 16, 34, Ink);

        float x = 936;
        // HD / FM badge
        string badge = s.PlayingHd ? $"HD{(s.Eng?.Program ?? 0) + 1}" : s.HdSynced ? "HD" : "FM";
        var bw = Measure(g, badge, F(15, true)).Width + 22;
        var br = new RectangleF(x - bw, 20, bw, 26);
        if (s.PlayingHd) Fill(g, br, Orange, 7);
        else Outline(g, br, s.HdSynced ? Orange : Faint, 7);
        DrawText(g, badge, F(15, true), s.PlayingHd ? Color.White : s.HdSynced ? Orange : Dim, br, StringAlignment.Center);
        x = br.Left - 10;

        // STEREO
        var sr = new RectangleF(x - 72, 20, 72, 26);
        Outline(g, sr, s.Stereo ? Green : Faint, 7);
        DrawText(g, "STEREO", F(11.5f, true), s.Stereo ? Green : Faint, sr, StringAlignment.Center);
        x = sr.Left - 14;

        // signal bars from the FM channel power
        int bars = s.Eng == null ? 0 : Math.Clamp((int)Math.Round((s.PowerDb + 52) / 8), 0, 5);
        for (int i = 0; i < 5; i++)
        {
            float bh = 6 + i * 4.5f;
            var r = new RectangleF(x - (5 - i) * 8, 46 - bh, 5, bh);
            using var b = new SolidBrush(i < bars ? Ink : Faint);
            g.FillRectangle(b, r);
        }
        x -= 5 * 8 + 14;

        // weather / traffic chips, when the station sends maps
        var hd = s.Hd;
        if (hd != null)
        {
            if (hd.TrafficTiles.Any(t => t != null)) x = Chip(g, x, "Traffic", () => MapRequested?.Invoke("traffic"));
            if (hd.WeatherMap != null) x = Chip(g, x, "Weather", () => MapRequested?.Invoke("weather"));
        }

        // emergency alert banner
        if (!string.IsNullOrEmpty(hd?.Alert))
        {
            var ar = new RectangleF(250, 18, x - 260, 30);
            if (ar.Width > 80)
            {
                Fill(g, ar, Red, 8);
                DrawText(g, "ALERT  " + hd.Alert, F(13, true), Color.White, Inset(ar, 10, 0), StringAlignment.Near);
            }
        }
    }

    /// <summary>Raised when the user clicks the Weather or Traffic chip.</summary>
    public event Action<string>? MapRequested;

    private float Chip(Graphics g, float right, string label, Action click)
    {
        float w = Measure(g, label, F(12, true)).Width + 20;
        var r = new RectangleF(right - w, 21, w, 24);
        bool hot = _hover == "chip:" + label;
        Fill(g, r, hot ? PanelHi : Panel, 12);
        DrawText(g, label, F(12, true), Blue, r, StringAlignment.Center);
        _hits.Add(new Hit(r, "chip:" + label, click));
        return r.Left - 8;
    }

    private void DrawArt(Graphics g, State s)
    {
        var r = new RectangleF(24, 76, 240, 240);
        using var clip = Rounded(r, 18);
        var hd = s.Hd;
        var art = s.HdSynced ? Decode(hd?.AlbumArt, ref _artKey, ref _art) : null;
        var logo = s.HdSynced ? Decode(hd?.StationLogo, ref _logoKey, ref _logo) : null;
        var state = g.Save();
        g.SetClip(clip);
        if (art != null)
        {
            DrawCover(g, art, r);
        }
        else if (logo != null)
        {
            Fill(g, r, Panel, 0);
            DrawContain(g, logo, Inset(r, 28, 28));
        }
        else if (s.PlayingHd)
        {
            using (var b = new LinearGradientBrush(r, Color.FromArgb(0x1D, 0x5F, 0xC9), Color.FromArgb(0x0E, 0x2C, 0x66), 60f)) g.FillRectangle(b, r);
            DrawText(g, "HD", F(84, true), Color.White, new RectangleF(r.X, r.Y + 40, r.Width, 110), StringAlignment.Center);
            DrawText(g, hd?.StationName ?? "", F(18, true), Color.FromArgb(0xC8, 0xDC, 0xFF), new RectangleF(r.X, r.Y + 160, r.Width, 30), StringAlignment.Center);
        }
        else
        {
            using (var b = new LinearGradientBrush(r, Color.FromArgb(0x2A, 0x31, 0x3D), Color.FromArgb(0x15, 0x19, 0x20), 60f)) g.FillRectangle(b, r);
            // analog: call sign big (the frequency is already shown large next to the tile), genre below
            string? call = s.Rds?.CallSign;
            var cf = F(call != null ? 52 : 64, true);
            DrawText(g, call ?? "FM", cf, call != null ? Ink : Dim, new RectangleF(r.X, r.Y + 120 - cf.GetHeight(g) / 2 - 6, r.Width, cf.GetHeight(g) + 6), StringAlignment.Center);
            string? pty = s.Rds?.PtyName is { Length: > 0 } p && p != "None" ? p : call != null ? "FM" : null;
            if (pty != null) DrawText(g, pty, F(18, false), Dim, new RectangleF(r.X, r.Y + 158, r.Width, 28), StringAlignment.Center);
        }
        g.Restore(state);
        using var pen = new Pen(Line, 1);
        g.DrawPath(pen, clip);
    }

    private void DrawInfo(Graphics g, State s)
    {
        float x = 292, right = 936;
        // frequency
        string f = (s.Freq / 1e6).ToString("0.0");
        var big = F(92, false, "Segoe UI Light");
        var fs = Measure(g, f, big);
        // (the box must be taller than the font's line height, or GDI+ drops the line)
        float lineH = big.GetHeight(g);
        DrawText(g, f, big, _dragDial ? Orange : Ink, new RectangleF(x - 6, 116 - lineH / 2, fs.Width + 16, lineH + 4), StringAlignment.Near);
        DrawText(g, "MHz", F(20, false), Dim, new RectangleF(x + fs.Width + 8, 128, 80, 30), StringAlignment.Near);

        var hd = s.Hd; var rds = s.Rds;
        if (_c.Error != null || _c.Starting || s.Eng == null)
        {
            string msg = _c.Starting ? "Starting the radio…" : _c.Error ?? "Not running. Click to start.";
            var mr = new RectangleF(x, 186, right - x, 120);
            DrawText(g, msg, F(20, true), _c.Error != null ? Red : Dim, mr, StringAlignment.Near, wrap: true);
            if (!_c.Starting) _hits.Add(new Hit(mr, "start", () => _ = _c.StartAsync()));
            return;
        }

        // station line: HD name (or RDS call sign / PS), then slogan or program type
        string? name = s.HdSynced ? hd?.StationName : null;
        name ??= rds?.CallSign ?? rds?.ProgramService;
        string? sub = s.HdSynced ? hd?.Slogan : null;
        sub ??= rds?.PtyName is { Length: > 0 } p && p != "None" ? p : null;
        if (name != null)
        {
            var nf = F(24, true);
            var nw = Measure(g, name, nf).Width;
            DrawText(g, name, nf, Ink, new RectangleF(x, 176, right - x, 34), StringAlignment.Near);
            if (sub != null && x + nw + 20 < right)
                DrawText(g, "·  " + sub, F(18, false), Dim, new RectangleF(x + nw + 10, 181, right - x - nw - 10, 28), StringAlignment.Near);
        }

        // now playing: HD ID3, else RDS RadioText
        string? title = s.HdSynced ? hd?.Title : null, artist = s.HdSynced ? hd?.Artist : null;
        if (title != null)
        {
            DrawText(g, title, F(28, true), Ink, new RectangleF(x, 216, right - x, 40), StringAlignment.Near);
            if (artist != null) DrawText(g, artist, F(21, false), Dim, new RectangleF(x, 256, right - x, 32), StringAlignment.Near);
        }
        else if (rds?.RadioText != null)
        {
            DrawText(g, rds.RadioText, F(22, true), Ink, new RectangleF(x, 218, right - x, 66), StringAlignment.Near, wrap: true);
        }

        // status
        DrawText(g, StatusLine(s), F(14, false), Faint, new RectangleF(x, 292, right - x, 22), StringAlignment.Near);
    }

    private string StatusLine(State s)
    {
        var eng = s.Eng!;
        if (_c.Seeking) return "Seeking…";
        var b = eng.Blender; var hd = s.Hd!;
        if (s.PlayingHd)
        {
            string type = hd.Programs.TryGetValue(eng.Program, out var t) && t != null ? " · " + t : "";
            return $"HD{eng.Program + 1} digital audio · signal {(hd.MerLower + hd.MerUpper) / 2:0.0} dB{type}";
        }
        string analog = $"FM {(s.Stereo ? "stereo" : "mono")}{(s.Rds?.Synced == true ? " · RDS" : "")}";
        if (!s.HdSynced) return analog;
        if (eng.ForceAnalog) return analog + " · HD available";
        if (b.RetryIn > 0.5) return analog + $" · HD signal unstable, retrying in {b.RetryIn:0} s";
        return analog + " · HD found, syncing audio…";
    }

    private void DrawPrograms(Graphics g, State s)
    {
        float x = 292, y = 330;
        var hd = s.Hd;
        if (s.Eng != null && hd != null && s.HdSynced && hd.Programs.Count > 0)
        {
            foreach (var p in hd.Programs.Keys)
            {
                var r = new RectangleF(x, y, 58, 32);
                bool sel = p == s.Eng.Program, hot = _hover == "prog" + p;
                if (sel) Fill(g, r, Orange, 9);
                else Fill(g, r, hot ? PanelHi : Panel, 9);
                DrawText(g, $"HD{p + 1}", F(14, true), sel ? Color.White : Ink, r, StringAlignment.Center);
                uint prog = p;
                _hits.Add(new Hit(r, "prog" + p, () => _c.SetProgram(prog)));
                x += 64;
                if (x > 700) break;
            }
        }

        // Auto HD | Analog switch
        bool analog = _c.Settings.ForceAnalog;
        var whole = new RectangleF(756, y, 180, 32);
        Fill(g, whole, Panel, 9);
        var left = new RectangleF(whole.X + 3, y + 3, 87, 26);
        var right = new RectangleF(whole.X + 90, y + 3, 87, 26);
        Fill(g, analog ? right : left, PanelHi, 7);
        DrawText(g, "Auto HD", F(13, true), analog ? Dim : Ink, left, StringAlignment.Center);
        DrawText(g, "Analog", F(13, true), analog ? Ink : Dim, right, StringAlignment.Center);
        _hits.Add(new Hit(left, "autohd", () => _c.SetForceAnalog(false)));
        _hits.Add(new Hit(right, "analog", () => _c.SetForceAnalog(true)));
    }

    private void DrawTuner(Graphics g, State s)
    {
        float y = 392, h = 64;
        Button(g, new RectangleF(24, y, 72, h), "seekdown", () => _c.Seek(-1), (gg, r) => SeekIcon(gg, r, -1), _c.Seeking);
        Button(g, new RectangleF(104, y, 56, h), "stepdown", () => _c.Step(-1), (gg, r) => Chevron(gg, r, -1));
        Button(g, new RectangleF(800, y, 56, h), "stepup", () => _c.Step(1), (gg, r) => Chevron(gg, r, 1));
        Button(g, new RectangleF(864, y, 72, h), "seekup", () => _c.Seek(1), (gg, r) => SeekIcon(gg, r, 1), _c.Seeking);

        // band dial
        var d = _dialRect = new RectangleF(168, y, 624, h);
        Fill(g, d, Panel, 12);
        float x0 = d.X + 20, x1 = d.Right - 20;
        float Px(double mhz) => (float)(x0 + (mhz - 87.5) / (108 - 87.5) * (x1 - x0));
        using (var minor = new Pen(Faint, 1))
        using (var major = new Pen(Dim, 1.5f))
        {
            for (int k = 875; k <= 1080; k += 1)
            {
                if (k % 2 == 0 && k % 10 != 0) continue;   // ticks every 0.2 MHz on the odd channels, plus whole MHz
                double mhz = k / 10.0;
                float px = Px(mhz);
                bool isMajor = k % 10 == 0;
                g.DrawLine(isMajor ? major : minor, px, d.Bottom - 12, px, d.Bottom - (isMajor ? 26 : 18));
                if (isMajor && (k / 10) % 2 == 0)
                    DrawText(g, (k / 10).ToString(), F(12, false), Dim, new RectangleF(px - 20, d.Y + 8, 40, 18), StringAlignment.Center);
            }
        }
        // presets as small marks on the dial
        foreach (var p in _c.Settings.Presets)
            if (p != null)
            {
                float px = Px(p.Mhz);
                using var b = new SolidBrush(Color.FromArgb(160, Blue));
                g.FillEllipse(b, px - 3, d.Bottom - 8, 6, 6);
            }
        // needle
        float nx = Px(s.Freq / 1e6);
        using (var glow = new Pen(Color.FromArgb(70, Orange), 7)) g.DrawLine(glow, nx, d.Y + 6, nx, d.Bottom - 6);
        using (var needle = new Pen(Orange, 2.5f)) g.DrawLine(needle, nx, d.Y + 6, nx, d.Bottom - 6);
        _hits.Add(new Hit(d, "dial", null));
    }

    private void DrawPresets(Graphics g, State s)
    {
        float y = 472, h = 56, gap = 8, w = (912 - gap * (AppSettings.PresetCount - 1)) / AppSettings.PresetCount;
        long cur = (long)Math.Round(s.Freq / 1e5);
        for (int i = 0; i < AppSettings.PresetCount; i++)
        {
            var r = new RectangleF(24 + i * (w + gap), y, w, h);
            var p = _c.Settings.Presets[i];
            bool active = p != null && (long)Math.Round(p.Mhz * 10) == cur;
            string id = "preset" + i;
            bool hot = _hover == id, down = _pressed == id;
            Fill(g, r, down ? PanelHi : hot ? Color.FromArgb(0x21, 0x28, 0x33) : Panel, 10);
            if (active) Outline(g, r, Orange, 10, 1.5f);
            DrawText(g, (i + 1).ToString(), F(11, true), Faint, new RectangleF(r.X + 10, r.Y + 6, 20, 16), StringAlignment.Near);
            if (p != null)
            {
                DrawText(g, p.Mhz.ToString("0.0"), F(20, true), active ? Orange : Ink, new RectangleF(r.X, r.Y + 4, r.Width, 30), StringAlignment.Center);
                DrawText(g, p.Name ?? "", F(12, false), Dim, new RectangleF(r.X + 8, r.Y + 32, r.Width - 16, 20), StringAlignment.Center);
            }
            else
            {
                DrawText(g, "Hold to save", F(12, false), Faint, new RectangleF(r.X, r.Y + 18, r.Width, 20), StringAlignment.Center);
            }
            int idx = i;
            _hits.Add(new Hit(r, id, () => _c.RecallPreset(idx), () => StorePreset(idx)));
        }
    }

    private void StorePreset(int i)
    {
        var eng = _c.Engine;
        string? name = null;
        if (eng != null)
        {
            var hd = eng.Hd;
            var rds = eng.Receiver.Rds;
            name = hd.Synced ? hd.StationName : null;
            name ??= rds.CallSign ?? rds.ProgramService;
        }
        _c.StorePreset(i, name);
    }

    private void DrawBottom(Graphics g, State s)
    {
        // spectrum of the station's 744 kHz baseband: analog FM in the middle, HD sidebands either side
        var r = new RectangleF(24, 544, 680, 40);
        Fill(g, r, Panel, 8);
        float colW = r.Width / SpecCols;
        // shade the HD sideband regions (+-129..198 kHz of +-372 kHz)
        foreach (int sign in new[] { -1, 1 })
        {
            float a = r.X + r.Width * (0.5f + sign * 129f / 744.1875f), b = r.X + r.Width * (0.5f + sign * 198f / 744.1875f);
            using var hb = new SolidBrush(Color.FromArgb(s.HdSynced ? 28 : 12, Orange));
            g.FillRectangle(hb, Math.Min(a, b), r.Y, Math.Abs(b - a), r.Height);
        }
        if (_specValid && s.Eng != null)
        {
            var sorted = (float[])_spec.Clone();
            Array.Sort(sorted);
            float floor = sorted[SpecCols / 10], top = Math.Max(sorted[^1], floor + 30);
            var pts = new List<PointF> { new(r.X, r.Bottom) };
            for (int c = 0; c < SpecCols; c++)
            {
                float lv = Math.Clamp((_spec[c] - floor) / (top - floor), 0, 1);
                pts.Add(new PointF(r.X + (c + 0.5f) * colW, r.Bottom - 3 - lv * (r.Height - 8)));
            }
            pts.Add(new PointF(r.Right, r.Bottom));
            var state = g.Save();
            using (var clip = Rounded(r, 8)) g.SetClip(clip);
            using (var fill = new LinearGradientBrush(r, Color.FromArgb(150, Orange), Color.FromArgb(20, Orange), 90f))
                g.FillPolygon(fill, pts.ToArray());
            using (var pen = new Pen(Orange, 1.2f)) g.DrawLines(pen, pts.Skip(1).Take(SpecCols).ToArray());
            g.Restore(state);
        }

        // volume
        var spk = new RectangleF(724, 544, 40, 40);
        bool muted = _c.Settings.Muted;
        Button(g, spk, "mute", _c.ToggleMute, (gg, rr) => SpeakerIcon(gg, rr, muted));
        var v = _volRect = new RectangleF(776, 544, 160, 40);
        float ty = v.Y + v.Height / 2;
        using (var track = new Pen(PanelHi, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, v.X + 8, ty, v.Right - 8, ty);
        float vx = v.X + 8 + (v.Width - 16) * _c.Settings.Volume;
        using (var fillPen = new Pen(muted ? Faint : Orange, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(fillPen, v.X + 8, ty, vx, ty);
        using (var knob = new SolidBrush(Ink)) g.FillEllipse(knob, vx - 8, ty - 8, 16, 16);
        _hits.Add(new Hit(v, "vol", null));
    }

    // ------------------------------------------------------------------ widgets

    private void Button(Graphics g, RectangleF r, string id, Action click, Action<Graphics, RectangleF> icon, bool lit = false)
    {
        bool hot = _hover == id, down = _pressed == id;
        Fill(g, r, down ? PanelHi : hot ? Color.FromArgb(0x21, 0x28, 0x33) : Panel, 12);
        if (lit) Outline(g, r, Orange, 12, 1.5f);
        icon(g, r);
        _hits.Add(new Hit(r, id, click));
    }

    private static void Chevron(Graphics g, RectangleF r, int dir)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = 9;
        using var pen = new Pen(Ink, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - dir * s / 2, cy - s), new PointF(cx + dir * s / 2, cy), new PointF(cx - dir * s / 2, cy + s) });
    }

    private static void SeekIcon(Graphics g, RectangleF r, int dir)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = 10;
        using var b = new SolidBrush(Ink);
        for (int k = 0; k < 2; k++)
        {
            float bx = cx + dir * (k * s - s);
            g.FillPolygon(b, new[] { new PointF(bx, cy - s), new PointF(bx + dir * s, cy), new PointF(bx, cy + s) });
        }
        g.FillRectangle(b, dir > 0 ? cx + s : cx - s - 3, cy - s, 3, 2 * s);
    }

    private static void SpeakerIcon(Graphics g, RectangleF r, bool muted)
    {
        float cx = r.X + r.Width / 2 - 4, cy = r.Y + r.Height / 2;
        using var b = new SolidBrush(muted ? Dim : Ink);
        g.FillPolygon(b, new[] { new PointF(cx - 9, cy - 4), new PointF(cx - 4, cy - 4), new PointF(cx + 2, cy - 10), new PointF(cx + 2, cy + 10), new PointF(cx - 4, cy + 4), new PointF(cx - 9, cy + 4) });
        using var pen = new Pen(muted ? Red : Ink, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (muted) { g.DrawLine(pen, cx + 7, cy - 5, cx + 15, cy + 5); g.DrawLine(pen, cx + 15, cy - 5, cx + 7, cy + 5); }
        else { g.DrawArc(pen, cx - 2, cy - 6, 12, 12, -50, 100); g.DrawArc(pen, cx - 6, cy - 11, 22, 22, -50, 100); }
    }

    // ------------------------------------------------------------------ drawing helpers

    private readonly Dictionary<(float, bool, string), Font> _fonts = new();
    private Font F(float px, bool bold, string family = "Segoe UI")
    {
        var key = (px, bold, family);
        if (!_fonts.TryGetValue(key, out var f))
            _fonts[key] = f = new Font(bold && family == "Segoe UI" ? "Segoe UI Semibold" : family, px, FontStyle.Regular, GraphicsUnit.Pixel);
        return f;
    }

    private static SizeF Measure(Graphics g, string s, Font f) => g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic);

    private static void DrawText(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment align, bool wrap = false)
    {
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = align,
            LineAlignment = wrap ? StringAlignment.Near : StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = (wrap ? 0 : StringFormatFlags.NoWrap) | StringFormatFlags.LineLimit,
        };
        // GDI+ with LineLimit drops a line that doesn't fit vertically: grow single-line boxes around their center
        float lh = f.GetHeight(g);
        if (!wrap && r.Height < lh + 2) r = new RectangleF(r.X, r.Y + r.Height / 2 - lh / 2 - 1, r.Width, lh + 2);
        using var b = new SolidBrush(c);
        g.DrawString(s, f, b, r, fmt);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0) { p.AddRectangle(r); return p; }
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static void Fill(Graphics g, RectangleF r, Color c, float radius)
    {
        using var p = Rounded(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    private static void Outline(Graphics g, RectangleF r, Color c, float radius, float width = 1.2f)
    {
        using var p = Rounded(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, p);
    }

    private static RectangleF Inset(RectangleF r, float dx, float dy) => new(r.X + dx, r.Y + dy, r.Width - 2 * dx, r.Height - 2 * dy);

    private static void DrawCover(Graphics g, Image img, RectangleF r)
    {
        float s = Math.Max(r.Width / img.Width, r.Height / img.Height);
        float w = img.Width * s, h = img.Height * s;
        g.DrawImage(img, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
    }

    private static void DrawContain(Graphics g, Image img, RectangleF r)
    {
        float s = Math.Min(r.Width / img.Width, r.Height / img.Height);
        float w = img.Width * s, h = img.Height * s;
        g.DrawImage(img, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
    }

    // ------------------------------------------------------------------ mouse

    private PointF ToDesign(Point p) => new((p.X - _ox) / _scale, (p.Y - _oy) / _scale);
    private Hit? HitAt(PointF p) => _hits.LastOrDefault(h => h.R.Contains(p));

    private long DialFrequency(float x)
    {
        float x0 = _dialRect.X + 20, x1 = _dialRect.Right - 20;
        double mhz = 87.5 + (x - x0) / (x1 - x0) * (108 - 87.5);
        long ch = (long)Math.Round((mhz * 1e6 - RadioEngine.FirstChannel) / RadioEngine.ChannelStep);
        return Math.Clamp(RadioEngine.FirstChannel + ch * RadioEngine.ChannelStep, RadioEngine.FirstChannel, RadioEngine.LastChannel);
    }

    private void SetVolumeAt(float x) =>
        _c.SetVolume((x - _volRect.X - 8) / (_volRect.Width - 16));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = ToDesign(e.Location);
        if (_dragDial) { _dragFreq = DialFrequency(p.X); Invalidate(); return; }
        if (_dragVol) { SetVolumeAt(p.X); Invalidate(); return; }
        var h = HitAt(p)?.Id;
        if (h != _hover)
        {
            _hover = h;
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var p = ToDesign(e.Location);
        var h = HitAt(p);
        if (h == null) return;
        if (e.Button == MouseButtons.Right)
        {
            h.RightClick?.Invoke();
            Invalidate();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _pressed = h.Id;
        _longPressFired = false;
        if (h.Id == "dial") { _dragDial = true; _dragFreq = DialFrequency(p.X); }
        else if (h.Id == "vol") { _dragVol = true; SetVolumeAt(p.X); }
        else if (h.RightClick != null) { _longPressAction = h.RightClick; _longPress.Start(); }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _longPress.Stop();
        var p = ToDesign(e.Location);
        if (_dragDial)
        {
            _dragDial = false;
            _c.Tune(_dragFreq);
        }
        else if (_dragVol)
        {
            _dragVol = false;
        }
        else if (e.Button == MouseButtons.Left && !_longPressFired)
        {
            var h = HitAt(p);
            if (h != null && h.Id == _pressed) h.Click?.Invoke();
        }
        _pressed = null;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var p = ToDesign(e.Location);
        if (_volRect.Contains(p) || HitAt(p)?.Id == "mute") _c.SetVolume(_c.Settings.Volume + Math.Sign(e.Delta) * 0.05f);
        else _c.Step(Math.Sign(e.Delta));
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _longPress.Dispose();
            foreach (var f in _fonts.Values) f.Dispose();
            _art?.Dispose();
            _logo?.Dispose();
        }
        base.Dispose(disposing);
    }
}
