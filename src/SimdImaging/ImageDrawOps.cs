using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>
/// Drawing operations. Unlike the operators (which return a new image), these mutate this image in place: a
/// canvas that copies itself for every rectangle is not a canvas.
/// </summary>
public sealed partial class Image
{
    /// <summary>Fills the whole image.</summary>
    public void Fill(RgbaColor color) => DrawOps.Fill(this[0].Pixels.View, color);

    /// <summary>Fills a rectangle, clipped to the image bounds.</summary>
    public void FillRect(PixelRect rect, RgbaColor color) => DrawOps.FillRect(this[0].Pixels.View, rect, color);

    /// <summary>Strokes a rectangle outline of <paramref name="thickness"/> pixels.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not positive.</exception>
    public void DrawRect(PixelRect rect, RgbaColor color, int thickness = 1) =>
        DrawOps.Rect(this[0].Pixels.View, rect, color, thickness);

    /// <summary>Draws a one-pixel Bresenham line, clipped to the image.</summary>
    public void DrawLine(int x0, int y0, int x1, int y1, RgbaColor color) =>
        DrawOps.Line(this[0].Pixels.View, x0, y0, x1, y1, color);

    /// <summary>Fills the ellipse inscribed in <paramref name="bounds"/>.</summary>
    public void FillEllipse(PixelRect bounds, RgbaColor color) =>
        DrawOps.FillEllipse(this[0].Pixels.View, bounds, color);

    /// <summary>Fills a polygon with the even-odd rule.</summary>
    /// <exception cref="ArgumentException">Fewer than three points.</exception>
    public void FillPolygon(ReadOnlySpan<PixelPoint> points, RgbaColor color) =>
        DrawOps.FillPolygon(this[0].Pixels.View, points, color);

    /// <summary>Fills a path of lines and Bézier curves, after flattening it to within a quarter pixel.</summary>
    /// <param name="path">The geometry; open subpaths are closed implicitly.</param>
    /// <param name="color">The fill colour.</param>
    /// <param name="rule">Even-odd punches holes through nested contours; non-zero does not.</param>
    public void FillPath(PixelPath path, RgbaColor color, PathFillRule rule = PathFillRule.NonZero) =>
        PathOps.Fill(this[0].Pixels.View, path, color, rule);

    /// <summary>Multiplies alpha by the path's coverage: a path-shaped clip mask.</summary>
    /// <param name="path">The clip region; an empty path leaves nothing visible.</param>
    /// <param name="rule">Even-odd or non-zero.</param>
    public void ClipPath(PixelPath path, PathFillRule rule = PathFillRule.NonZero) =>
        PathOps.Clip(this[0].Pixels.View, path, rule);

    /// <summary>Strokes a path with a one-pixel miter-joined line.</summary>
    public void DrawPath(PixelPath path, RgbaColor color) =>
        PathOps.Stroke(this[0].Pixels.View, path, color);

    /// <summary>Strokes a path with the given width, join, cap and dash pattern.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width, miter limit or a dash length is invalid.</exception>
    public void DrawPath(PixelPath path, RgbaColor color, StrokeOptions options) =>
        PathOps.Stroke(this[0].Pixels.View, path, color, options);

    /// <summary>Fills a rectangle with a linear gradient.</summary>
    public void LinearGradient(PixelRect rect, RgbaColor from, RgbaColor to, float angleDegrees = 0f) =>
        DrawOps.LinearGradient(this[0].Pixels.View, rect, from, to, angleDegrees);

    /// <summary>Fills a rectangle with a radial gradient.</summary>
    public void RadialGradient(PixelRect rect, float centreX, float centreY, float radius, RgbaColor inner, RgbaColor outer) =>
        DrawOps.RadialGradient(this[0].Pixels.View, rect, centreX, centreY, radius, inner, outer);

    /// <summary>Fills a rectangle with an angular sweep around a centre.</summary>
    /// <param name="rect">The area to fill, clipped.</param>
    /// <param name="centreX">Sweep centre.</param>
    /// <param name="centreY">Sweep centre.</param>
    /// <param name="from">Colour at the start angle.</param>
    /// <param name="to">Colour one turn later.</param>
    /// <param name="startAngleDegrees">Where the sweep begins and where the seam falls.</param>
    public void SweepGradient(PixelRect rect, float centreX, float centreY, RgbaColor from, RgbaColor to,
        float startAngleDegrees = 0f) =>
        DrawOps.SweepGradient(this[0].Pixels.View, rect, centreX, centreY, from, to, startAngleDegrees);

    /// <summary>Scanline flood fill from a seed, bounded by colour distance.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The seed is outside the image.</exception>
    public void FloodFill(int x, int y, RgbaColor color, float tolerance = 0f) =>
        DrawOps.FloodFill(this[0].Pixels.View, x, y, color, tolerance);

    /// <summary>Clears the image to transparent black.</summary>
    public void Clear() => DrawOps.Clear(this[0].Pixels.View);
}
