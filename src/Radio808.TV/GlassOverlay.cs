namespace Radio808.TV;

/// <summary>
/// What makes the display a window rather than a rectangle, drawn once over the live display: the shadow the bezel
/// casts in from the top and sides, and darker corners.
/// </summary>
public sealed class GlassOverlay : UIView
{
    public GlassOverlay(CGRect frame) : base(frame)
    {
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        UserInteractionEnabled = false;
        ContentMode = UIViewContentMode.Redraw;
    }

    public override void Draw(CGRect rect)
    {
        var ctx = UIGraphics.GetCurrentContext();
        var b = Bounds;
        ctx.SaveState();
        UIBezierPath.FromRoundedRect(b, 18).AddClip();

        // the bezel's shadow, falling in from the top and, less, the sides and the bottom
        Edge(ctx, new CGRect(b.X, b.Y, b.Width, 56), 0, 150);
        Edge(ctx, new CGRect(b.X, b.Bottom - 22, b.Width, 22), 2, 70);
        Edge(ctx, new CGRect(b.X, b.Y, 40, b.Height), 1, 90);
        Edge(ctx, new CGRect(b.Right - 40, b.Y, 40, b.Height), 3, 90);

        // darker corners
        using (var space = CGColorSpace.CreateDeviceRGB())
        using (var vignette = new CGGradient(space, new[] { UIColor.Black.With(0).CGColor, UIColor.Black.With(0).CGColor, UIColor.Black.With(90).CGColor }, new nfloat[] { 0, 0.55f, 1 }))
        {
            var c = new CGPoint(b.GetMidX(), b.GetMidY());
            ctx.DrawRadialGradient(vignette, c, 0, c, b.Width * 0.62f, CGGradientDrawingOptions.DrawsAfterEndLocation);
        }

        ctx.RestoreState();
    }

    /// <summary>A shadow fading in from one edge: 0 top, 1 left, 2 bottom, 3 right.</summary>
    private static void Edge(CGContext ctx, CGRect r, int side, int alpha)
    {
        using var space = CGColorSpace.CreateDeviceRGB();
        using var g = new CGGradient(space, new[] { UIColor.Black.With(alpha).CGColor, UIColor.Black.With(0).CGColor }, new nfloat[] { 0, 1 });
        var (from, to) = side switch
        {
            0 => (new CGPoint(r.X, r.Y), new CGPoint(r.X, r.Bottom)),
            1 => (new CGPoint(r.X, r.Y), new CGPoint(r.Right, r.Y)),
            2 => (new CGPoint(r.X, r.Bottom), new CGPoint(r.X, r.Y)),
            _ => (new CGPoint(r.Right, r.Y), new CGPoint(r.X, r.Y)),
        };
        ctx.SaveState();
        ctx.ClipToRect(r);
        ctx.DrawLinearGradient(g, from, to, 0);
        ctx.RestoreState();
    }
}
