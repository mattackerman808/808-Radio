namespace Radio808.TV;

/// <summary>
/// A faceplate key the Siri Remote can land on: a dark gradient key with a glyph in the illumination color and a
/// label under it. Focus lifts it and lights its edge; select presses it; a long select is the "hold" action.
/// </summary>
public class KeyButton : UIControl
{
    public string? Glyph { get; set; }
    public string Label { get; set; } = "";
    public UIColor Lit { get; set; } = Theme.Illuminations[0].Color;
    /// <summary>Off: the glyph is grey (a toggle that's off, a feature the station lacks).</summary>
    public bool Active { get; set; } = true;
    public Action? Pressed { get; set; }
    public Action? Held { get; set; }
    /// <summary>Keep holding (2.5 s): the second-stage hold action (a preset clears).</summary>
    public Action? HeldLong { get; set; }
    /// <summary>An SF Symbol drawn in the glyph's place.</summary>
    public UIImage? Icon { get; set; }
    private bool _held;
    /// <summary>Someone pressed a key: the screen saver's idle clock starts over.</summary>
    public static event Action? Activity;

    public KeyButton(CGRect frame) : base(frame)
    {
        BackgroundColor = UIColor.Clear;
        ContentMode = UIViewContentMode.Redraw;
        var hold = new UILongPressGestureRecognizer(g => { if (g.State == UIGestureRecognizerState.Began && Held != null) { _held = true; Held(); } })
        { AllowedPressTypes = new[] { NSNumber.FromNInt((nint)UIPressType.Select) }, MinimumPressDuration = 0.6 };
        AddGestureRecognizer(hold);
        var holdLong = new UILongPressGestureRecognizer(g => { if (g.State == UIGestureRecognizerState.Began && HeldLong != null) { _held = true; HeldLong(); } })
        { AllowedPressTypes = new[] { NSNumber.FromNInt((nint)UIPressType.Select) }, MinimumPressDuration = 2.5 };
        AddGestureRecognizer(holdLong);
    }

    public override bool CanBecomeFocused => true;

    public override void DidUpdateFocus(UIFocusUpdateContext context, UIFocusAnimationCoordinator coordinator)
    {
        base.DidUpdateFocus(context, coordinator);
        bool on = context.NextFocusedView == this;
        coordinator.AddCoordinatedAnimations(() => Transform = on ? CGAffineTransform.MakeScale(1.06f, 1.06f) : CGAffineTransform.MakeIdentity(), null);
        SetNeedsDisplay();
    }

    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        Activity?.Invoke();
        if (HasSelect(presses)) { _held = false; _down = true; SetNeedsDisplay(); return; }
        base.PressesBegan(presses, evt);
    }

    public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (HasSelect(presses))
        {
            // a tap on the remote is over in an instant: keep the key down long enough to see it travel
            NSTimer.CreateScheduledTimer(0.13, _ => { _down = false; SetNeedsDisplay(); });
            if (!_held) Pressed?.Invoke();
            return;
        }
        base.PressesEnded(presses, evt);
    }

    public override void PressesCancelled(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        _down = false; SetNeedsDisplay();
        base.PressesCancelled(presses, evt);
    }

    private bool _down;

    private static bool HasSelect(NSSet<UIPress> presses)
    {
        foreach (UIPress p in presses) if (p.Type == UIPressType.Select) return true;
        return false;
    }

    public override void Draw(CGRect rect)
    {
        var ctx = UIGraphics.GetCurrentContext();
        const float radius = 14;
        // the well the cap sits in, and the cap itself: pressed, it sinks by a few points
        var well = Bounds.Inset(4, 4);
        nfloat sink = _down ? 3 : 0;
        var cap = new CGRect(well.X + 2, well.Y + 2 + sink, well.Width - 4, well.Height - 4 - sink);
        var capPath = UIBezierPath.FromRoundedRect(cap, radius);

        // the glow of focus, under everything
        if (Focused)
        {
            ctx.SaveState();
            ctx.SetShadow(new CGSize(0, 0), 22, Lit.With(170).CGColor);
            Lit.With(60).SetFill(); capPath.Fill();
            ctx.RestoreState();
        }
        // the well: dark, with the cap's drop shadow inside it
        Theme.Rgb(0x07, 0x08, 0x0A).SetFill(); UIBezierPath.FromRoundedRect(well, radius + 2).Fill();
        ctx.SaveState();
        ctx.SetShadow(new CGSize(0, _down ? 1 : 5), _down ? 4 : 12, UIColor.Black.With(_down ? 120 : 200).CGColor);
        Theme.Key2.SetFill(); capPath.Fill();
        ctx.RestoreState();

        // the cap: a curved surface, brighter at the top, with a specular band
        bool hot = Focused && !_down;
        var top = hot ? Theme.Rgb(0x50, 0x57, 0x61) : _down ? Theme.Rgb(0x22, 0x26, 0x2C) : Theme.Rgb(0x42, 0x48, 0x51);
        var mid = hot ? Theme.Rgb(0x33, 0x38, 0x40) : _down ? Theme.Rgb(0x16, 0x19, 0x1D) : Theme.Rgb(0x2A, 0x2E, 0x35);
        var low = hot ? Theme.Rgb(0x20, 0x24, 0x2A) : _down ? Theme.Rgb(0x0E, 0x10, 0x13) : Theme.Rgb(0x15, 0x18, 0x1C);
        Theme.FillGradient(ctx, capPath, new[] { top, mid, low, Theme.Key2 }, new nfloat[] { 0, 0.12f, 0.75f, 1 });
        // gloss across the upper half, as the light catches the curve
        ctx.SaveState();
        capPath.AddClip();
        var gloss = UIBezierPath.FromRoundedRect(new CGRect(cap.X, cap.Y, cap.Width, cap.Height * 0.46f), radius);
        Theme.FillGradient(ctx, gloss, UIColor.White.With(_down ? 6 : 22), UIColor.White.With(0));
        ctx.RestoreState();
        // bevels: a bright line along the top edge, a dark one along the bottom
        if (!_down)
        {
            var hi = new UIBezierPath(); hi.MoveTo(new CGPoint(cap.X + radius, cap.Y + 1.5f)); hi.AddLineTo(new CGPoint(cap.Right - radius, cap.Y + 1.5f));
            Theme.Stroke(hi, UIColor.White.With(70), 1.5f);
        }
        var lo = new UIBezierPath(); lo.MoveTo(new CGPoint(cap.X + radius, cap.Bottom - 1.5f)); lo.AddLineTo(new CGPoint(cap.Right - radius, cap.Bottom - 1.5f));
        Theme.Stroke(lo, UIColor.Black.With(160), 2);
        // the rim
        Theme.Stroke(capPath, Focused ? Lit : Theme.Rgb(0x0B, 0x0D, 0x10), Focused ? 3 : 1.5f);
        if (!Focused) Theme.Stroke(UIBezierPath.FromRoundedRect(cap.Inset(1.5f, 1.5f), radius - 1.5f), UIColor.White.With(16), 1);

        // the legend, embossed
        ctx.SaveState();
        ctx.SetShadow(new CGSize(0, 2), 2, UIColor.Black.With(200).CGColor);
        DrawContent(ctx, cap);
        ctx.RestoreState();
    }

    protected virtual void DrawContent(CGContext ctx, CGRect b)
    {
        var glyphColor = Active ? Lit : Theme.Grey;
        if (Icon != null)
        {
            var icon = Icon.ApplyTintColor(glyphColor, UIImageRenderingMode.AlwaysOriginal);
            bool alone = Label.Length == 0;   // just the symbol, centered and bigger
            nfloat ih = b.Height * (alone ? 0.6f : 0.46f), iw = ih * icon.Size.Width / icon.Size.Height;
            icon.Draw(new CGRect(b.X + (b.Width - iw) / 2, alone ? b.Y + (b.Height - ih) / 2 : b.Y + 8, iw, ih));
            if (!alone) Theme.Label(Label, new CGRect(b.X, b.Y + b.Height * 0.58f, b.Width, b.Height * 0.36f), 20, Theme.Silver);
        }
        else if (Glyph != null)
        {
            Theme.Label(Glyph, new CGRect(b.X, b.Y + 6, b.Width, b.Height * 0.55f), 34, glyphColor);
            Theme.Label(Label, new CGRect(b.X, b.Y + b.Height * 0.58f, b.Width, b.Height * 0.36f), 20, Theme.Silver);
        }
        else Theme.Label(Label, b, 28, Active ? Theme.Silver : Theme.Grey);
    }
}

/// <summary>A preset key: its number, and the station it holds on a little seven-segment readout (a ghost 88.8 when empty).</summary>
public sealed class PresetKey : KeyButton
{
    public int Index { get; }
    public double? Mhz { get; set; }
    /// <summary>The HD program the preset was saved on (0 = HD1, shown as no program).</summary>
    public uint Program { get; set; }
    public bool Current { get; set; }

    public PresetKey(CGRect frame, int index) : base(frame) => Index = index;

    protected override void DrawContent(CGContext ctx, CGRect b)
    {
        Theme.Label((Index + 1).ToString(), new CGRect(b.X + 18, b.Y, 40, b.Height), 34, Current ? Lit : Theme.Silver, UIFontWeight.Bold, UITextAlignment.Left);
        // the readout window, recessed into the cap: a dark glass with a shadow falling in from its top edge
        var win = new CGRect(b.X + 70, b.Y + 16, b.Width - 90, b.Height - 32);
        var wp = UIBezierPath.FromRoundedRect(win, 6);
        ctx.SaveState();
        ctx.SetShadow(CGSize.Empty, 0, null);   // no emboss shadow on the glass
        Theme.Glass2.SetFill(); wp.Fill();
        ctx.SaveState();
        wp.AddClip();
        var shade = UIBezierPath.FromRect(new CGRect(win.X, win.Y, win.Width, win.Height * 0.5f));
        Theme.FillGradient(ctx, shade, UIColor.Black.With(170), UIColor.Black.With(0));
        ctx.RestoreState();
        var lip = new UIBezierPath(); lip.MoveTo(new CGPoint(win.X + 6, win.Bottom + 1)); lip.AddLineTo(new CGPoint(win.Right - 6, win.Bottom + 1));
        Theme.Stroke(lip, UIColor.White.With(40), 1);
        Theme.Stroke(wp, Theme.Rgb(0x05, 0x06, 0x08), 1.5f);
        ctx.RestoreState();
        // the frequency in seven-segment digits, then a fixed HD legend with its own small digit: every element
        // always there, lit on the preset you're on, dimmer on the others, ghost 8s on an empty one; the HD legend
        // and digit are ghosts unless the preset was saved on HD2, HD3 ...
        var on = Current ? Lit : Lit.With(170);
        var ghost = Lit.With(22);
        nfloat h = win.Height - 14, progH = h * 0.62f, hdPx = 15;
        nfloat bottom = win.Y + (win.Height + h) / 2;
        nfloat progX = win.Right - 10 - Segments.DigitWidth(progH);
        bool hasProg = Mhz != null && Program > 0;
        Segments.DrawSegments(ctx, hasProg ? (Program + 1).ToString() : " ", progX, bottom - progH, progH, on, ghost);
        nfloat hdW = Theme.Measure("HD", hdPx).Width;
        nfloat hdX = progX - 4 - hdW;
        Theme.Label("HD", new CGRect(hdX, bottom - progH - 3, hdW + 2, 18), hdPx, hasProg ? on : ghost, UIFontWeight.Bold, UITextAlignment.Left);
        string digits = Mhz is double m ? m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5) : "    ";
        nfloat w = 4 * Segments.DigitWidth(h) + 3 * h * 0.16f;
        Segments.DrawSegments(ctx, digits, hdX - 8 - w, bottom - h, h, on, ghost);
    }
}
