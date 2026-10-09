namespace Radio808.TV;

/// <summary>The faceplate's chrome, drawn once: the face screwed onto the wood, the brand, and the display's dark glass.</summary>
public sealed class BodyView : UIView
{
    public static readonly CGRect Glass = new(80, 140, 1760, 440);

    public BodyView(CGRect frame) : base(frame)
    {
        ContentMode = UIViewContentMode.Redraw;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
    }

    public override void Draw(CGRect rect)
    {
        var ctx = UIGraphics.GetCurrentContext();
        var b = Bounds;
        // the pocket routed into the wood that the face sits down in: its wall shades the top of the recess
        var faceRect = new CGRect(40, 36, b.Width - 80, b.Height - 72);
        var pocketRect = faceRect.Inset(-12, -12);
        var pocket = UIBezierPath.FromRoundedRect(pocketRect, 46);
        Theme.Rgb(0x12, 0x0B, 0x07).SetFill(); pocket.Fill();
        ctx.SaveState();
        pocket.AddClip();
        Theme.FillGradient(ctx, UIBezierPath.FromRect(new CGRect(pocketRect.X, pocketRect.Y, pocketRect.Width, 40)), UIColor.Black.With(220), UIColor.Black.With(0));
        ctx.RestoreState();
        Theme.Stroke(pocket, Theme.Rgb(0x0B, 0x07, 0x04), 2);
        // the edge of the wood around the pocket: a dark line where it drops away
        var rim = UIBezierPath.FromRoundedRect(pocketRect.Inset(-2, -2), 48);
        Theme.Stroke(rim, UIColor.Black.With(90), 2);

        // the face, down in the pocket, the wall's shadow falling across its top and sides
        var face = UIBezierPath.FromRoundedRect(faceRect, 36);
        Theme.FillGradient(ctx, face, Theme.Face1, Theme.Face2);
        ctx.SaveState();
        face.AddClip();
        Theme.FillGradient(ctx, UIBezierPath.FromRect(new CGRect(faceRect.X, faceRect.Y, faceRect.Width, 70)), UIColor.Black.With(170), UIColor.Black.With(0));
        using (var space = CGColorSpace.CreateDeviceRGB())
        using (var side = new CGGradient(space, new[] { UIColor.Black.With(110).CGColor, UIColor.Black.With(0).CGColor }, new nfloat[] { 0, 1 }))
        {
            ctx.DrawLinearGradient(side, new CGPoint(faceRect.X, 0), new CGPoint(faceRect.X + 40, 0), 0);
            ctx.DrawLinearGradient(side, new CGPoint(faceRect.Right, 0), new CGPoint(faceRect.Right - 40, 0), 0);
        }
        ctx.RestoreState();
        Theme.Stroke(face, Theme.Rgb(0x05, 0x05, 0x06), 3);

        // the screws holding it to the TV, one per corner, each turned a little differently
        foreach (var (sx, sy, a) in new[] { (72f, 68f, 0.35f), (b.Width - 72, 68f, -0.6f), (72f, b.Height - 68, 1.1f), (b.Width - 72, b.Height - 68, -0.2f) })
            DrawScrew(ctx, sx, sy, 13, a);

        // the brand at the left, the obligatory 90s badge in italic chrome at the right, both standing off the face
        DrawBrand(ctx, 112, 62, 48);
        Theme.Chrome(ctx, "DIGITAL", new CGRect(b.Width - 112 - 200, 62, 200, 48), 26, Theme.Rgb(0xE8, 0xEC, 0xF1), Theme.Rgb(0x7A, 0x82, 0x8C), italic: true, align: UITextAlignment.Right);

        // the bezel the glass is set into: dark at the top where it shadows itself, catching the light along its lower lip
        var bezelRect = Glass.Inset(-8, -8);
        var bezel = UIBezierPath.FromRoundedRect(bezelRect, 24);
        ctx.SaveState();
        ctx.SetShadow(new CGSize(0, 3), 10, UIColor.Black.With(160).CGColor);
        Theme.Rgb(0x0C, 0x0E, 0x11).SetFill(); bezel.Fill();
        ctx.RestoreState();
        Theme.FillGradient(ctx, bezel, new[] { Theme.Rgb(0x0A, 0x0C, 0x0F), Theme.Rgb(0x16, 0x19, 0x1E), Theme.Rgb(0x2C, 0x31, 0x38) }, new nfloat[] { 0, 0.7f, 1 });
        var lip = new UIBezierPath(); lip.MoveTo(new CGPoint(bezelRect.X + 24, bezelRect.Bottom - 1.5f)); lip.AddLineTo(new CGPoint(bezelRect.Right - 24, bezelRect.Bottom - 1.5f));
        Theme.Stroke(lip, UIColor.White.With(45), 1.5f);
        Theme.Stroke(bezel, Theme.Rgb(0x04, 0x05, 0x06), 1.5f);

        var glass = UIBezierPath.FromRoundedRect(Glass, 18);
        Theme.FillGradient(ctx, glass, Theme.Glass1, Theme.Glass2);
        Theme.Stroke(glass, Theme.Rgb(0x02, 0x03, 0x04), 2);
    }

    /// <summary>A Phillips screw: a countersunk recess, a domed head lit from the upper left, and the cross turned by <paramref name="angle"/>.</summary>
    private static void DrawScrew(CGContext ctx, nfloat cx, nfloat cy, nfloat r, float angle)
    {
        using var space = CGColorSpace.CreateDeviceRGB();
        // the recess: dark, with a lit lower lip
        var recess = new CGRect(cx - r - 4, cy - r - 4, 2 * (r + 4), 2 * (r + 4));
        ctx.SaveState();
        ctx.SetShadow(new CGSize(0, 1.5f), 1.5f, UIColor.White.With(50).CGColor);
        Theme.Rgb(0x05, 0x06, 0x08).SetFill(); ctx.FillEllipseInRect(recess);
        ctx.RestoreState();
        // the head: a dome, bright where the light hits it
        var head = new CGRect(cx - r, cy - r, 2 * r, 2 * r);
        ctx.SaveState();
        ctx.AddEllipseInRect(head); ctx.Clip();
        using (var dome = new CGGradient(space, new[] { Theme.Rgb(0x9A, 0xA1, 0xAB).CGColor, Theme.Rgb(0x5E, 0x65, 0x6F).CGColor, Theme.Rgb(0x23, 0x27, 0x2D).CGColor }, new nfloat[] { 0, 0.55f, 1 }))
            ctx.DrawRadialGradient(dome, new CGPoint(cx - r * 0.35f, cy - r * 0.4f), 0, new CGPoint(cx, cy), r * 1.15f, CGGradientDrawingOptions.DrawsAfterEndLocation);
        ctx.RestoreState();
        ctx.SetStrokeColor(Theme.Rgb(0x14, 0x17, 0x1B).CGColor);
        ctx.SetLineWidth(1.5f);
        ctx.StrokeEllipseInRect(head);
        // the cross: a dark slot with a bright edge on its lower-right side
        ctx.SaveState();
        ctx.TranslateCTM(cx, cy);
        ctx.RotateCTM(angle);
        ctx.SetLineCap(CGLineCap.Round);
        foreach (var (w, c, dx, dy) in new[] { (4.6f, UIColor.White.With(70), 0.8f, 0.8f), (4.2f, Theme.Rgb(0x0B, 0x0D, 0x10), 0f, 0f) })
        {
            ctx.SetStrokeColor(c.CGColor);
            ctx.SetLineWidth(w);
            nfloat l = r * 0.68f;
            ctx.MoveTo(-l + dx, dy); ctx.AddLineToPoint(l + dx, dy); ctx.StrokePath();
            ctx.MoveTo(dx, -l + dy); ctx.AddLineToPoint(dx, l + dy); ctx.StrokePath();
        }
        ctx.RestoreState();
    }

    /// <summary>The "808 Radio" mark with its three broadcast arcs.</summary>
    private static void DrawBrand(CGContext ctx, nfloat x, nfloat y, nfloat h)
    {
        var num = Theme.Chrome(ctx, "808", new CGRect(x, y, 300, h), h * 0.72f, Theme.Rgb(0xF2, 0xF5, 0xF8), Theme.Rgb(0x80, 0x88, 0x92));
        nfloat wx = x + num.Width + h * 0.18f;
        var word = Theme.Chrome(ctx, "Radio", new CGRect(wx, y + h * 0.03f, 300, h), h * 0.52f, Theme.Rgb(0xFF, 0xC8, 0x6A), Theme.Rgb(0xC8, 0x6A, 0x12), UIFontWeight.Semibold);
        nfloat cx = wx + word.Width + h * 0.1f, cy = y + h / 2;
        ctx.SaveState();
        ctx.SetShadow(new CGSize(0, 3), 4, UIColor.Black.With(200).CGColor);
        ctx.SetLineCap(CGLineCap.Round);
        ctx.SetLineWidth((nfloat)Math.Max(2, (double)h / 11));
        for (int i = 1; i <= 3; i++)
        {
            nfloat r = h * 0.15f * i;
            ctx.SetStrokeColor(Theme.Orange.With(255 - (i - 1) * 60).CGColor);
            ctx.AddArc(cx, cy, r, (nfloat)(-45 * Math.PI / 180), (nfloat)(45 * Math.PI / 180), false);
            ctx.StrokePath();
        }
        ctx.RestoreState();
    }
}
