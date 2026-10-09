using Radio808.Core.Hd;
using Radio808.Shared;

namespace Radio808.TV;

/// <summary>
/// The setup screen: what the Mac keeps in its right-click menu, as a list of rows over the faceplate. Up and down
/// move between rows, select toggles a row or opens it, left and right step a value, the menu button closes it.
/// </summary>
public sealed class SetupView : UIView
{
    private readonly RadioController _c;
    private readonly List<SetupRow> _rows = new();
    public Action? Closed { get; set; }
    public Action? ServerRequested { get; set; }
    public UIColor Lit { get; set; } = Theme.Illuminations[0].Color;

    public static readonly int[] SaverChoices = { 0, 1, 3, 5, 10, 15 };
    private static readonly double[] Gains = { 8.7, 12.5, 16.6, 22.9, 29.7, 37.2, 44.5 };

    public SetupView(RadioController c, CGRect frame) : base(frame)
    {
        _c = c;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        ContentMode = UIViewContentMode.Redraw;

        var s = _c.Settings;
        nfloat y = Panel.Y + 110;
        void Row(string label, Func<string> value, Action? select = null, Action<int>? step = null)
        {
            var r = new SetupRow(new CGRect(Panel.X + 60, y, Panel.Width - 120, 64)) { Label = label, Value = value, Pressed = select, Step = step };
            _rows.Add(r);
            AddSubview(r);
            y += 72;
        }
        Row("HD RADIO", () => s.ForceAnalog ? "ANALOG ONLY" : "AUTO HD", () => _c.SetForceAnalog(!s.ForceAnalog));
        Row("MULTIPATH EQUALIZER", () => s.Equalizer ? "ON" : "OFF", () => _c.SetEqualizer(!s.Equalizer));
        Row("STEREO", () => s.ForceMono ? "FORCED MONO" : "AUTO", () => _c.SetForceMono(!s.ForceMono));
        Row("SEEK", () => s.SeekHd ? "HD STATIONS ONLY" : "ALL STATIONS", () => _c.SetSeekHd(!s.SeekHd));
        Row("TUNER GAIN", () => s.AutoGain ? $"AUTO  ({_c.Engine?.CurrentGainDb ?? 0:0.0} dB)" : $"FIXED  {s.GainDb:0.0} dB",
            () => _c.SetGain(s.AutoGain ? s.GainDb ?? Gains[2] : null), d => StepGain(d));
        Row("FREQUENCY CORRECTION", () => (s.AutoPpm ? "AUTO  " : "FIXED  ") + $"{s.Ppm:+0;-0;0} PPM",
            () => { if (s.AutoPpm) { s.AutoPpm = false; s.Save(); } else _c.CalibratePpmNow(); },
            d => { s.AutoPpm = false; _c.SetPpm(s.Ppm + d); });
        Row("ANTENNA POWER", () => s.BiasTee ? "ON" : "OFF", () => _c.SetBiasTee(!s.BiasTee));
        Row("DISPLAY", () => s.ShowAlbumArt ? "ALBUM ART" : "SPECTRUM ANALYZER", () => { s.ShowAlbumArt = !s.ShowAlbumArt; s.Save(); });
        Row("SCREEN SAVER", () => s.SaverMinutes <= 0 ? "OFF" : $"AFTER {s.SaverMinutes} MIN", () => StepSaver(+1), StepSaver);
        Row("SERVER", () => (s.RtlTcpAddress ?? "").ToUpperInvariant(), () => ServerRequested?.Invoke());
        Row("ABOUT", () => $"808 RADIO {Version}  ·  NRSC5 {Nrsc5}".ToUpperInvariant());
    }

    private static string Version => NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]?.ToString() ?? "";
    private static string Nrsc5 { get { try { return HdDecoder.LibraryVersion; } catch (Exception) { return "?"; } } }

    private void StepGain(int d)
    {
        var s = _c.Settings;
        // auto sits before the fixed gains: left from the lowest goes back to auto
        int i = s.AutoGain ? -1 : Array.FindIndex(Gains, g => Math.Abs(g - (s.GainDb ?? 0)) < 0.05);
        i = Math.Clamp(i + d, -1, Gains.Length - 1);
        _c.SetGain(i < 0 ? null : Gains[i]);
    }

    private void StepSaver(int d)
    {
        var s = _c.Settings;
        int i = Array.IndexOf(SaverChoices, s.SaverMinutes);
        if (i < 0) i = 2;
        i = (i + d + SaverChoices.Length) % SaverChoices.Length;
        s.SaverMinutes = SaverChoices[i];
        s.Save();
    }

    public SetupRow FirstRow => _rows[0];

    public void Refresh()
    {
        foreach (var r in _rows) { r.Lit = Lit; r.SetNeedsDisplay(); }
    }

    private static readonly CGRect Panel = new(300, 60, 1320, 960);

    public override void Draw(CGRect rect)
    {
        var ctx = UIGraphics.GetCurrentContext();
        // the faceplate dims behind the panel
        UIColor.Black.With(150).SetFill(); ctx.FillRect(Bounds);
        var panel = UIBezierPath.FromRoundedRect(Panel, 30);
        ctx.SaveState();
        ctx.SetShadow(new CGSize(0, 14), 36, UIColor.Black.With(230).CGColor);
        Theme.Face2.SetFill(); panel.Fill();
        ctx.RestoreState();
        Theme.FillGradient(ctx, panel, Theme.Face1, Theme.Face2);
        Theme.Stroke(panel, Theme.Rgb(0x48, 0x4E, 0x57), 2);
        Theme.Chrome(ctx, "SETUP", new CGRect(Panel.X + 60, Panel.Y + 30, 400, 56), 36, Theme.Rgb(0xF2, 0xF5, 0xF8), Theme.Rgb(0x80, 0x88, 0x92));
        Theme.Label("◀ ▶  CHANGE      SELECT  TOGGLE      MENU  BACK", new CGRect(Panel.X, Panel.Bottom - 56, Panel.Width, 30), 20, Theme.Grey);
    }

    // the menu button closes the setup rather than the app
    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        foreach (UIPress p in presses)
            if (p.Type == UIPressType.Menu) { Closed?.Invoke(); return; }
        base.PressesBegan(presses, evt);
    }
}

/// <summary>One setting: its name at the left, its value at the right in the illumination color.</summary>
public sealed class SetupRow : KeyButton
{
    public Func<string> Value { get; set; } = () => "";
    /// <summary>Left or right on the remote: step the value by -1 or +1.</summary>
    public Action<int>? Step { get; set; }

    public SetupRow(CGRect frame) : base(frame) { }

    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        foreach (UIPress p in presses)
        {
            if (p.Type == UIPressType.LeftArrow && Step != null) { Step(-1); SetNeedsDisplay(); return; }
            if (p.Type == UIPressType.RightArrow && Step != null) { Step(+1); SetNeedsDisplay(); return; }
        }
        base.PressesBegan(presses, evt);
    }

    public override void Draw(CGRect rect)
    {
        var ctx = UIGraphics.GetCurrentContext();
        var b = Bounds.Inset(2, 2);
        var path = UIBezierPath.FromRoundedRect(b, 12);
        if (Focused)
        {
            ctx.SaveState();
            ctx.SetShadow(new CGSize(0, 0), 16, Lit.With(140).CGColor);
            Lit.With(40).SetFill(); path.Fill();
            ctx.RestoreState();
        }
        Theme.FillGradient(ctx, path, Focused ? Theme.Rgb(0x2A, 0x2E, 0x35) : Theme.Rgb(0x14, 0x17, 0x1B), Theme.Rgb(0x0B, 0x0D, 0x10));
        Theme.Stroke(path, Focused ? Lit : Theme.Rgb(0x2A, 0x2F, 0x36), Focused ? 2.5f : 1.5f);
        Theme.Label(Label, new CGRect(b.X + 24, b.Y, b.Width / 2, b.Height), 26, Pressed == null && Step == null ? Theme.Grey : Theme.Silver, UIFontWeight.Semibold, UITextAlignment.Left);
        Theme.Label(Value(), new CGRect(b.X, b.Y, b.Width - 24, b.Height), 26, Lit, UIFontWeight.Bold, UITextAlignment.Right);
        if (Step != null && Focused)
        {
            var sz = Theme.Measure(Value(), 26);
            Theme.Label("◀", new CGRect(b.Right - 24 - sz.Width - 44, b.Y, 30, b.Height), 20, Lit.With(150));
            Theme.Label("▶", new CGRect(b.Right - 24 + 10, b.Y, 30, b.Height), 20, Lit.With(150));
        }
    }
}
