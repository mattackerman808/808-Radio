using Radio808.Shared;

namespace Radio808.TV;

/// <summary>The faceplate's colors (the same ones as the Mac and Windows faceplates) and a few CoreGraphics helpers.</summary>
public static class Theme
{
    public static readonly (string Name, UIColor Color)[] Illuminations =
    {
        ("Cyan", Rgb(0x2B, 0xE4, 0xF2)), ("Amber", Rgb(0xFF, 0xA8, 0x26)),
        ("Green", Rgb(0x5C, 0xFF, 0x86)), ("Red", Rgb(0xFF, 0x45, 0x45)),
        ("Blue", Rgb(0x4A, 0x8C, 0xFF)), ("White", Rgb(0xE6, 0xF2, 0xFF)),
    };

    public static UIColor Lit(AppSettings s) => Illuminations[Math.Clamp(s.Illumination, 0, Illuminations.Length - 1)].Color;

    public static readonly UIColor Body1 = Rgb(0x30, 0x34, 0x3B), Body2 = Rgb(0x0D, 0x0F, 0x12);
    public static readonly UIColor Face1 = Rgb(0x1A, 0x1D, 0x22), Face2 = Rgb(0x08, 0x09, 0x0B);
    public static readonly UIColor Key1 = Rgb(0x2A, 0x2E, 0x35), Key2 = Rgb(0x0E, 0x10, 0x13);
    public static readonly UIColor Glass1 = Rgb(0x07, 0x0C, 0x0F), Glass2 = Rgb(0x02, 0x04, 0x05);
    public static readonly UIColor Silver = Rgb(0xB8, 0xBE, 0xC6), Grey = Rgb(0x6E, 0x76, 0x80);
    public static readonly UIColor Alert = Rgb(0xFF, 0x4A, 0x4A), Orange = Rgb(0xF7, 0x94, 0x1D);
    public static readonly UIColor KeyEdge = Rgb(0x3A, 0x3F, 0x47);

    public static UIColor Rgb(int r, int g, int b, int a = 255) => UIColor.FromRGBA(r / 255f, g / 255f, b / 255f, a / 255f);
    /// <summary>The color at an alpha of 0..255.</summary>
    public static UIColor With(this UIColor c, int alpha) => c.ColorWithAlpha(alpha / 255f);

    public static UIFont Font(nfloat size, UIFontWeight weight = UIFontWeight.Bold, bool italic = false)
    {
        var f = UIFont.SystemFontOfSize(size, weight);
        if (!italic) return f;
        var d = f.FontDescriptor.CreateWithTraits(UIFontDescriptorSymbolicTraits.Italic | UIFontDescriptorSymbolicTraits.Bold);
        return d != null ? UIFont.FromDescriptor(d, size) : f;
    }

    public static void FillGradient(CGContext ctx, UIBezierPath path, UIColor top, UIColor bottom)
    {
        ctx.SaveState();
        path.AddClip();
        using var space = CGColorSpace.CreateDeviceRGB();
        using var g = new CGGradient(space, new[] { top.CGColor, bottom.CGColor }, new nfloat[] { 0, 1 });
        var b = path.Bounds;
        ctx.DrawLinearGradient(g, new CGPoint(b.X, b.Y), new CGPoint(b.X, b.Bottom), 0);
        ctx.RestoreState();
    }

    /// <summary>A vertical gradient through several colors at the given positions (0 top .. 1 bottom).</summary>
    public static void FillGradient(CGContext ctx, UIBezierPath path, UIColor[] colors, nfloat[] locations)
    {
        ctx.SaveState();
        path.AddClip();
        using var space = CGColorSpace.CreateDeviceRGB();
        using var g = new CGGradient(space, colors.Select(c => c.CGColor).ToArray(), locations);
        var b = path.Bounds;
        ctx.DrawLinearGradient(g, new CGPoint(b.X, b.Y), new CGPoint(b.X, b.Bottom), 0);
        ctx.RestoreState();
    }

    public static void Stroke(UIBezierPath path, UIColor color, nfloat width)
    {
        color.SetStroke();
        path.LineWidth = width;
        path.Stroke();
    }

    /// <summary>Draws text centered (or left/right aligned) in a rectangle, vertically centered.</summary>
    public static CGSize Label(string text, CGRect r, nfloat size, UIColor color, UIFontWeight weight = UIFontWeight.Bold, UITextAlignment align = UITextAlignment.Center, bool italic = false)
    {
        var attrs = new UIStringAttributes { Font = Font(size, weight, italic), ForegroundColor = color };
        var ns = new NSString(text);
        var sz = ns.GetSizeUsingAttributes(attrs);
        nfloat x = align switch { UITextAlignment.Left => r.X, UITextAlignment.Right => r.Right - sz.Width, _ => r.X + (r.Width - sz.Width) / 2 };
        ns.DrawString(new CGPoint(x, r.Y + (r.Height - sz.Height) / 2), attrs);
        return sz;
    }

    /// <summary>
    /// Text that stands off the surface: a soft shadow under it, a thin highlight along its top, and a chrome
    /// gradient down the letters (brighter at the top). Left-aligned at the rectangle's origin, vertically centered.
    /// </summary>
    public static CGSize Chrome(CGContext ctx, string text, CGRect r, nfloat size, UIColor top, UIColor bottom, UIFontWeight weight = UIFontWeight.Bold, bool italic = false, UITextAlignment align = UITextAlignment.Left)
    {
        var font = Font(size, weight, italic);
        var ns = new NSString(text);
        var sz = ns.GetSizeUsingAttributes(new UIStringAttributes { Font = font });
        nfloat x = align switch { UITextAlignment.Right => r.Right - sz.Width, UITextAlignment.Center => r.X + (r.Width - sz.Width) / 2, _ => r.X };
        var at = new CGPoint(x, r.Y + (r.Height - sz.Height) / 2);
        // the shadow, then the highlight
        ctx.SaveState();
        ctx.SetShadow(new CGSize(0, 3), 4, UIColor.Black.With(200).CGColor);
        ns.DrawString(at, new UIStringAttributes { Font = font, ForegroundColor = bottom });
        ctx.RestoreState();
        ns.DrawString(new CGPoint(at.X, at.Y - 1.5f), new UIStringAttributes { Font = font, ForegroundColor = UIColor.White.With(110) });
        // the letters, as a mask for the gradient
        var scale = UIScreen.MainScreen.Scale;
        var box = new CGRect(at.X - 2, at.Y - 2, sz.Width + 4, sz.Height + 4);
        var renderer = new UIGraphicsImageRenderer(box.Size, new UIGraphicsImageRendererFormat { Scale = scale, Opaque = false });
        var mask = renderer.CreateImage(_ => ns.DrawString(new CGPoint(2, 2), new UIStringAttributes { Font = font, ForegroundColor = UIColor.Black }));
        ctx.SaveState();
        ctx.TranslateCTM(box.X, box.Bottom);
        ctx.ScaleCTM(1, -1);
        ctx.ClipToMask(new CGRect(0, 0, box.Width, box.Height), mask.CGImage!);
        ctx.ScaleCTM(1, -1);
        ctx.TranslateCTM(-box.X, -box.Bottom);
        FillGradient(ctx, UIBezierPath.FromRect(box), new[] { top, top, bottom, bottom.With(220) }, new nfloat[] { 0, 0.42f, 0.58f, 1 });
        ctx.RestoreState();
        return sz;
    }

    public static CGSize Measure(string text, nfloat size, UIFontWeight weight = UIFontWeight.Bold, bool italic = false)
        => new NSString(text).GetSizeUsingAttributes(new UIStringAttributes { Font = Font(size, weight, italic) });
}
