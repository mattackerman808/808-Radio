using System.Globalization;
using Radio808.Core.Audio;
using Radio808.Core.Dsp;
using Radio808.Core.Radio;
using Radio808.Shared;
using Radio808.Shared.Display;

namespace Radio808.TV;

/// <summary>
/// The display behind the glass: the big dot-matrix line (song, station, messages), the small one with the clock,
/// the status lights, the HD programs, the signal meter, and the spectrum analyzer or the album art. Redrawn every
/// tick (20 a second) like the faceplates on the other platforms.
/// </summary>
public sealed class DisplayView : UIView
{
    private const int MainCells = 13, SubCells = 18, Bars = 16;
    private readonly RadioController _c;
    private string? _mainText, _subText, _flash;
    private DateTime _flashUntil;
    private List<bool[]> _mainCols = new(), _subCols = new();
    private int _mainScroll, _mainTicks, _subScroll, _subTicks;
    private byte[]? _artBytes;
    private UIImage? _art;

    public DisplayView(RadioController c, CGRect frame) : base(frame)
    {
        _c = c;
        BackgroundColor = UIColor.Clear;
        ContentMode = UIViewContentMode.Redraw;
        Opaque = false;
    }

    private UIColor Lit => Theme.Lit(_c.Settings);
    private int Mode => Math.Clamp(_c.Settings.DisplayMode, 0, DisplayText.Modes.Length - 1);

    public void Flash(string text, double seconds = 1.6)
    {
        _flash = text.ToUpperInvariant();
        _flashUntil = DateTime.UtcNow.AddSeconds(seconds);
        SetNeedsDisplay();
    }

    public void NextDisplay()
    {
        _c.Settings.DisplayMode = (Mode + 1) % DisplayText.Modes.Length;
        _c.Settings.Save();
        _mainScroll = _mainTicks = _subScroll = _subTicks = 0;
        Flash(DisplayText.Modes[Mode].Name, 1.2);
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

    /// <summary>Once a tick: scroll the lines, pull audio for the analyzer, redraw.</summary>
    public void Tick()
    {
        DisplayText.Marquee(_mainCols.Count / DotFont.CellCols, MainCells, ref _mainScroll, ref _mainTicks);
        DisplayText.Marquee(_subCols.Count / DotFont.CellCols, SubCells, ref _subScroll, ref _subTicks);
        PullAudio();
        SetNeedsDisplay();
    }

    // ---- the analyzer's bars (the same motion as the Mac faceplate)

    private AudioAnalyzer? _analyzer;
    private readonly float[] _audioBlock = new float[AudioAnalyzer.BlockSize], _bandDb = new float[Bars];
    private readonly float[] _bars = new float[Bars], _peaks = new float[Bars];
    private readonly double[] _peakHoldUntil = new double[Bars];
    private readonly System.Diagnostics.Stopwatch _barClock = System.Diagnostics.Stopwatch.StartNew();
    private double _barsAt;
    private bool _specValid;

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

    // ---- what the lines say

    private string MainText(RadioEngine? eng, out bool alert)
    {
        alert = false;
        string mhz = (_c.Frequency / 1e6).ToString("0.0", CultureInfo.InvariantCulture);
        if (_flash != null && DateTime.UtcNow < _flashUntil) return _flash;
        _flash = null;
        if (_c.Starting) return "STARTING";
        if (eng == null) return (_c.Error ?? "PRESS TO START").ToUpperInvariant();
        if (_c.Seeking) return $"SEEK  {mhz}";
        var hd = eng.Hd;
        if (!string.IsNullOrEmpty(hd.Alert)) { alert = true; return "ALERT  " + hd.Alert.ToUpperInvariant(); }
        return DisplayText.Item(DisplayText.Modes[Mode].Top, eng, hd, eng.Receiver.Rds, hd.Synced, mhz, null).ToUpperInvariant();
    }

    private string SubText(RadioEngine? eng)
    {
        string mhz = (_c.Frequency / 1e6).ToString("0.0", CultureInfo.InvariantCulture);
        if (eng == null) return $"FM {mhz}";
        var hd = eng.Hd;
        var (_, top, bottom) = DisplayText.Modes[Mode];
        return DisplayText.Item(bottom, eng, hd, eng.Receiver.Rds, hd.Synced, mhz, DisplayText.Item(top, eng, hd, eng.Receiver.Rds, hd.Synced, mhz, null)).ToUpperInvariant();
    }

    // ---- drawing (local coordinates: the glass is 1760 x 440)

    private static readonly CGRect Square = new(1390, 40, 330, 330);   // analyzer or album art

    public override void Draw(CGRect rect)
    {
        var ctx = UIGraphics.GetCurrentContext();
        var lit = Lit;
        var ghost = lit.With(20);
        var eng = _c.Engine;
        var hd = eng?.Hd;
        var rds = eng?.Receiver.Rds;
        bool synced = hd?.Synced == true, playingHd = eng?.Blender.PlayingHd == true;

        // the main line
        string text = MainText(eng, out bool alert);
        if (text != _mainText) { _mainText = text; _mainCols = DotFont.Columns(text); _mainScroll = _mainTicks = 0; }
        Segments.DrawDots(ctx, _mainCols, _mainScroll * DotFont.CellCols, 44, 46, 17, MainCells, alert ? Theme.Alert : lit, ghost);

        // the small line, and a seven-segment clock at its right end
        string sub = SubText(eng);
        if (sub != _subText) { _subText = sub; _subCols = DotFont.Columns(sub); _subScroll = _subTicks = 0; }
        Segments.DrawDots(ctx, _subCols, _subScroll * DotFont.CellCols, 44, 212, 10, SubCells, lit, ghost, glow: false);
        const float clockH = 62;
        string clock = DateTime.Now.ToString("H:mm").PadLeft(5);
        nfloat clockW = 4 * Segments.DigitWidth(clockH) + 3 * clockH * 0.16f + clockH * 0.24f;
        Segments.DrawSegments(ctx, clock, 1362 - clockW, 220, clockH, lit, ghost);

        // the status lights
        nfloat x = 44, y = 336;
        x = Indicator(ctx, x, y, "HD", synced, playingHd, lit);
        bool weak = eng != null && !_c.Settings.ForceAnalog && (eng.HdTooWeak || synced && !playingHd && eng.Blender.RetryIn > 0.5);
        x = Indicator(ctx, x, y, "WEAK", weak, weak, lit, Theme.Alert) + 18;
        bool stereo = eng != null && (playingHd || eng.Receiver.Stereo.PilotLocked && eng.Receiver.Stereo.Blend > 0.5f);
        x = StereoIcon(ctx, x, y, stereo, lit);
        bool rdsOn = rds?.Synced == true;
        x = Indicator(ctx, x, y, "RDS", rdsOn, rdsOn, lit) + 18;
        bool hdSeeking = _c.Seeking && _c.Settings.SeekHd;
        x = Indicator(ctx, x, y, "HD", hdSeeking, hdSeeking, lit) - 6;
        x = Indicator(ctx, x, y, "SEEK", _c.Seeking, _c.Seeking, lit) + 18;
        bool airplay = eng != null && AvAudioEngineDevice.OutputPortType == "AirPlay";
        x = Indicator(ctx, x, y, "AIRPLAY", airplay, airplay, lit) + 18;
        bool muted = _c.Settings.Muted;
        x = Indicator(ctx, x, y, "MUTE", muted, muted, lit) + 18;

        // the HD programs: the one playing lit, the others the station has as ghosts
        HdPrograms(ctx, 880, y, eng, lit, ghost);

        // the signal meter
        SignalMeter(ctx, 1120, y, eng, lit);

        // the analyzer, or the album art when HD sends some
        byte[]? art = synced && _c.Settings.ShowAlbumArt ? hd!.AlbumArt ?? hd.StationLogo : null;
        if (!ReferenceEquals(art, _artBytes)) { _artBytes = art; _art = art != null ? UIImage.LoadFromData(NSData.FromArray(art)) : null; }
        if (_art != null)
        {
            ctx.SaveState();
            UIBezierPath.FromRoundedRect(Square, 10).AddClip();
            _art.Draw(Square);
            ctx.RestoreState();
        }
        else Analyzer(ctx, lit, ghost);
    }

    /// <summary>A status pill: lit (filled, or outlined when <paramref name="on"/> but not <paramref name="filled"/>), else a ghost outline.</summary>
    private static nfloat Indicator(CGContext ctx, nfloat x, nfloat y, string label, bool on, bool filled, UIColor lit, UIColor? onColor = null)
    {
        const float size = 24, h = 38, pad = 14;
        var c = onColor ?? lit;
        var sz = Theme.Measure(label, size);
        var r = new CGRect(x, y, sz.Width + 2 * pad, h);
        var path = UIBezierPath.FromRoundedRect(r, 7);
        if (on && filled)
        {
            c.SetFill(); path.Fill();
            Theme.Label(label, r, size, Theme.Glass2);
        }
        else
        {
            Theme.Stroke(path, on ? c : lit.With(45), on ? 2.5f : 2);
            Theme.Label(label, r, size, on ? c : lit.With(70));
        }
        return r.Right + 10;
    }

    private static nfloat StereoIcon(CGContext ctx, nfloat x, nfloat y, bool on, UIColor lit)
    {
        const float d = 30, overlap = 11;
        ctx.SetLineWidth(3);
        ctx.SetStrokeColor((on ? lit : lit.With(45)).CGColor);
        ctx.StrokeEllipseInRect(new CGRect(x + 2, y + 4, d, d));
        ctx.StrokeEllipseInRect(new CGRect(x + 2 + d - overlap, y + 4, d, d));
        return x + 4 + 2 * d - overlap + 18;
    }

    private static void HdPrograms(CGContext ctx, nfloat x, nfloat y, RadioEngine? eng, UIColor lit, UIColor ghost)
    {
        var hd = eng?.Hd;
        bool any = hd?.Synced == true && hd.Programs.Count > 0;
        Theme.Label("HD", new CGRect(x, y, 50, 38), 26, any ? lit : lit.With(45), UIFontWeight.Bold, UITextAlignment.Left);
        x += 56;
        for (uint p = 0; p < 4; p++)
        {
            bool has = any && hd!.Programs.ContainsKey(p);
            bool current = has && eng!.Program == p;
            Theme.Label((p + 1).ToString(), new CGRect(x, y, 30, 38), 26, current ? lit : has ? lit.With(150) : lit.With(40));
            x += 34;
        }
    }

    /// <summary>An antenna, five dots and an OVL light: HD MER while HD plays, else the FM pilot's SNR (channel power for mono).</summary>
    private static void SignalMeter(CGContext ctx, nfloat x, nfloat y, RadioEngine? eng, UIColor lit)
    {
        bool overload = eng?.GainOptimizer.Overload == true;
        double q = 0;
        if (eng != null)
        {
            var hd = eng.Hd;
            var st = eng.Receiver.Stereo;
            q = eng.Blender.PlayingHd ? Math.Clamp(((hd.MerLower + hd.MerUpper) / 2 - 3) / 15, 0, 1)
              : st.PilotLocked ? Math.Clamp((st.PilotSnrDb - 10) / 35, 0, 1)
              : Math.Clamp((eng.Receiver.ChannelPowerDb + 52) / 40, 0, 1);
        }
        nfloat ax = x + 16, cy = y + 19;
        ctx.SetLineCap(CGLineCap.Round);
        ctx.SetLineWidth(3);
        ctx.SetStrokeColor(lit.CGColor);
        ctx.MoveTo(ax, cy - 6); ctx.AddLineToPoint(ax, cy + 16); ctx.StrokePath();
        ctx.MoveTo(ax - 7, cy + 16); ctx.AddLineToPoint(ax + 7, cy + 16); ctx.StrokePath();
        ctx.SetFillColor(lit.CGColor);
        ctx.FillEllipseInRect(new CGRect(ax - 3.5f, cy - 9.5f, 7, 7));
        foreach (float r in new[] { 10f, 17f })
        {
            ctx.AddArc(ax, cy - 6, r, (nfloat)(150 * Math.PI / 180), (nfloat)(210 * Math.PI / 180), false); ctx.StrokePath();
            ctx.AddArc(ax, cy - 6, r, (nfloat)(-30 * Math.PI / 180), (nfloat)(30 * Math.PI / 180), false); ctx.StrokePath();
        }
        int on = (int)Math.Round(q * 5);
        for (int i = 0; i < 5; i++)
        {
            ctx.SetFillColor((i < on ? lit : lit.With(28)).CGColor);
            ctx.FillEllipseInRect(new CGRect(x + 50 + i * 22, cy - 7, 14, 14));
        }
        Theme.Label("OVL", new CGRect(x + 166, y, 60, 38), 20, overload ? Theme.Alert : lit.With(28));
    }

    private void Analyzer(CGContext ctx, UIColor lit, UIColor ghost)
    {
        var sq = Square;
        nfloat bw = sq.Width / Bars;
        const int segs = 13;
        nfloat sh = sq.Height / segs;
        for (int b = 0; b < Bars; b++)
        {
            int litSegs = _specValid ? (int)Math.Round(_bars[b] * segs) : 0;
            int peakSeg = _specValid ? (int)Math.Round(_peaks[b] * segs) - 1 : -1;
            for (int s = 0; s < segs; s++)
            {
                var c = s < litSegs ? lit.With(120 + 120 * s / (segs - 1)) : s == peakSeg ? UIColor.White.With(230) : ghost;
                ctx.SetFillColor(c.CGColor);
                ctx.FillRect(new CGRect(sq.X + b * bw + 3, sq.Bottom - (s + 1) * sh + 3, bw - 6, sh - 6));
            }
        }
    }
}
