using System;
using System.Globalization;

namespace Radio808.Shared.Map;

/// <summary>A point in tile units or pixels (the map code has no UI framework's point type).</summary>
public readonly record struct MapPt(double X, double Y);

/// <summary>A color without a UI framework: each app converts it to its own.</summary>
public readonly record struct MapColor(byte A, byte R, byte G, byte B)
{
    public static MapColor Hex(string s)
    {
        int v = int.Parse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new MapColor(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
    public MapColor With(byte a) => this with { A = a };
}

/// <summary>
/// What the base map's style needs to draw with, in pixels: the Mac app implements it over Avalonia's drawing
/// context, the Windows app over GDI+. Everything the renderer passes is already in pixel coordinates.
/// </summary>
public interface IMapCanvas
{
    void FillRect(double x, double y, double w, double h, MapColor c);
    /// <summary>Polygon rings filled even-odd (inner rings are holes).</summary>
    void FillPolygon(MapPt[][] rings, MapColor c);
    /// <summary>Polylines with round caps and joins; dashed is a 2-on 2-off pattern in units of the width.</summary>
    void StrokeLines(MapPt[][] lines, MapColor c, double width, bool dashed);
    /// <summary>The size of a bold label at <paramref name="px"/> pixels.</summary>
    (double Width, double Height) MeasureLabel(string s, double px);
    /// <summary>A bold label with its top-left corner at (x, y), with a thin halo behind it.</summary>
    void DrawLabel(string s, double px, double x, double y, MapColor c, MapColor halo);
}
