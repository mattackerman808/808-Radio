namespace Radio808.TV;

/// <summary>
/// The slab of wood the radio is set into: a photograph of a dark hardwood board (ambientCG's Wood051, public
/// domain), stained dark oak, with a satin finish laid over it: a sheen down from the top and darker corners.
/// </summary>
public sealed class WoodView : UIView
{
    private readonly UIImage? _wood;

    public WoodView(CGRect frame) : base(frame)
    {
        _wood = UIImage.FromBundle("Assets/wood.jpg");
        ContentMode = UIViewContentMode.Redraw;
        BackgroundColor = Theme.Rgb(28, 17, 11);
    }

    public override void Draw(CGRect rect)
    {
        var ctx = UIGraphics.GetCurrentContext();
        var b = Bounds;
        if (_wood != null)
        {
            // the board, filling the screen with the grain running across
            nfloat scale = (nfloat)(Math.Max((double)(b.Width / _wood.Size.Width), (double)(b.Height / _wood.Size.Height)) * 1.15);
            nfloat w = _wood.Size.Width * scale, h = _wood.Size.Height * scale;
            _wood.Draw(new CGRect((b.Width - w) / 2, (b.Height - h) / 2, w, h));
            // the stain: a dark oak, the board's own light and dark under a warm brown, then darkened
            ctx.SaveState();
            ctx.SetBlendMode(CGBlendMode.Color);
            Theme.Rgb(110, 66, 40).With(170).SetFill(); ctx.FillRect(b);
            ctx.SetBlendMode(CGBlendMode.Multiply);
            Theme.Rgb(150, 126, 108).SetFill(); ctx.FillRect(b);
            ctx.RestoreState();
        }
        // the finish
        var sheen = UIBezierPath.FromRect(new CGRect(b.X, b.Y, b.Width, b.Height * 0.5f));
        Theme.FillGradient(ctx, sheen, UIColor.White.With(22), UIColor.White.With(0));
        using var space = CGColorSpace.CreateDeviceRGB();
        using var vignette = new CGGradient(space, new[] { UIColor.Black.With(0).CGColor, UIColor.Black.With(0).CGColor, UIColor.Black.With(130).CGColor }, new nfloat[] { 0, 0.5f, 1 });
        var c = new CGPoint(b.GetMidX(), b.GetMidY());
        ctx.DrawRadialGradient(vignette, c, 0, c, b.Width * 0.7f, CGGradientDrawingOptions.DrawsAfterEndLocation);
    }
}
