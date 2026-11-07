using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>
/// Builds a <see cref="PixelPath"/>. Move / line / cubic / quadratic / close are the primitives; the shapes
/// below are conveniences written in terms of them, so the flattener only ever sees four verbs.
/// </summary>
public sealed class PixelPathBuilder
{
    // Control-point offset that makes four cubics approximate a quarter circle to ~0.02 %; the standard
    // value for a unit circle, scaled by the radius.
    private const float CircleKappa = 0.5522847498307936f;

    private readonly List<PathCommand> _commands = [];
    private PathPoint _current;
    private bool _hasCurrent;
    private PathPoint _start;
    private bool _closed;

    /// <summary>Current point, valid after the first <see cref="MoveTo(float, float)"/> or drawing command.</summary>
    public PathPoint CurrentPoint => _current;

    /// <summary>Starts a new subpath at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public PixelPathBuilder MoveTo(float x, float y) => MoveTo(new PathPoint(x, y));

    /// <summary>Starts a new subpath.</summary>
    /// <exception cref="ArgumentException">A coordinate is not finite.</exception>
    public PixelPathBuilder MoveTo(PathPoint point)
    {
        Validate(point);
        _commands.Add(new PathCommand(PathVerb.MoveTo, point));
        _current = point;
        _start = point;
        _hasCurrent = true;
        _closed = false;
        return this;
    }

    /// <summary>Draws a straight segment to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public PixelPathBuilder LineTo(float x, float y) => LineTo(new PathPoint(x, y));

    /// <summary>Draws a straight segment.</summary>
    /// <exception cref="InvalidOperationException">No current point: the path needs a <see cref="MoveTo(float, float)"/> first.</exception>
    /// <exception cref="ArgumentException">A coordinate is not finite.</exception>
    public PixelPathBuilder LineTo(PathPoint point)
    {
        Validate(point);
        EnsureStarted();
        _commands.Add(new PathCommand(PathVerb.LineTo, point));
        _current = point;
        return this;
    }

    /// <summary>Draws a cubic Bézier through two control points to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public PixelPathBuilder CubicTo(float control1X, float control1Y, float control2X, float control2Y, float x, float y) =>
        CubicTo(new PathPoint(control1X, control1Y), new PathPoint(control2X, control2Y), new PathPoint(x, y));

    /// <summary>Draws a cubic Bézier.</summary>
    /// <exception cref="InvalidOperationException">No current point.</exception>
    /// <exception cref="ArgumentException">A coordinate is not finite.</exception>
    public PixelPathBuilder CubicTo(PathPoint control1, PathPoint control2, PathPoint end)
    {
        Validate(control1);
        Validate(control2);
        Validate(end);
        EnsureStarted();
        _commands.Add(new PathCommand(PathVerb.CubicTo, end, control1, control2));
        _current = end;
        return this;
    }

    /// <summary>Draws a quadratic Bézier with one control point to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public PixelPathBuilder QuadraticTo(float controlX, float controlY, float x, float y) =>
        QuadraticTo(new PathPoint(controlX, controlY), new PathPoint(x, y));

    /// <summary>Draws a quadratic Bézier.</summary>
    /// <exception cref="InvalidOperationException">No current point.</exception>
    /// <exception cref="ArgumentException">A coordinate is not finite.</exception>
    public PixelPathBuilder QuadraticTo(PathPoint control, PathPoint end)
    {
        Validate(control);
        Validate(end);
        EnsureStarted();
        _commands.Add(new PathCommand(PathVerb.QuadraticTo, end, control));
        _current = end;
        return this;
    }

    /// <summary>Closes the current subpath. A following drawing command starts again at the subpath start.</summary>
    public PixelPathBuilder Close()
    {
        if (!_hasCurrent || _closed)
            return this;

        _commands.Add(new PathCommand(PathVerb.Close, _start));
        _current = _start;
        _closed = true;
        return this;
    }

    /// <summary>Appends a rectangle as its own closed subpath. A non-positive extent adds nothing.</summary>
    public PixelPathBuilder AddRectangle(PixelRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return this;

        var right = rect.X + rect.Width;
        var bottom = rect.Y + rect.Height;
        MoveTo(rect.X, rect.Y);
        LineTo(right, rect.Y);
        LineTo(right, bottom);
        LineTo(rect.X, bottom);
        return Close();
    }

    /// <summary>
    /// Appends a rounded rectangle as one closed subpath: straight edges and four quarter-circle cubics. The
    /// radius is clamped to half the short side, where the shape becomes an ellipse. A non-positive extent
    /// adds nothing.
    /// </summary>
    public PixelPathBuilder AddRoundedRectangle(PixelRect rect, float radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return this;

        var r = Math.Clamp(radius, 0f, MathF.Min(rect.Width, rect.Height) * 0.5f);
        if (r <= 0f)
            return AddRectangle(rect);

        var left = rect.X;
        var top = rect.Y;
        var right = rect.X + rect.Width;
        var bottom = rect.Y + rect.Height;
        var k = r * CircleKappa;

        MoveTo(left + r, top);
        LineTo(right - r, top);
        CubicTo(right - r + k, top, right, top + r - k, right, top + r);
        LineTo(right, bottom - r);
        CubicTo(right, bottom - r + k, right - r + k, bottom, right - r, bottom);
        LineTo(left + r, bottom);
        CubicTo(left + r - k, bottom, left, bottom - r + k, left, bottom - r);
        LineTo(left, top + r);
        CubicTo(left, top + r - k, left + r - k, top, left + r, top);
        return Close();
    }

    /// <summary>Appends an axis-aligned ellipse as one closed subpath of four cubics. A non-positive radius
    /// adds nothing.</summary>
    public PixelPathBuilder AddEllipse(float centreX, float centreY, float radiusX, float radiusY)
    {
        if (!(radiusX > 0f) || !(radiusY > 0f) || float.IsInfinity(radiusX) || float.IsInfinity(radiusY))
            return this;

        var kx = radiusX * CircleKappa;
        var ky = radiusY * CircleKappa;
        var left = centreX - radiusX;
        var right = centreX + radiusX;
        var top = centreY - radiusY;
        var bottom = centreY + radiusY;

        MoveTo(centreX, top);
        CubicTo(centreX + kx, top, right, centreY - ky, right, centreY);
        CubicTo(right, centreY + ky, centreX + kx, bottom, centreX, bottom);
        CubicTo(centreX - kx, bottom, left, centreY + ky, left, centreY);
        CubicTo(left, centreY - ky, centreX - kx, top, centreX, top);
        return Close();
    }

    /// <summary>Snapshots the commands. The builder stays usable; later edits do not affect the result.</summary>
    public PixelPath Build() => new(_commands.ToArray());

    private void EnsureStarted()
    {
        if (_hasCurrent)
        {
            if (!_closed)
                return;

            // Drawing after Close starts a new subpath at the old start point, per SVG.
            _commands.Add(new PathCommand(PathVerb.MoveTo, _current));
            _start = _current;
            _closed = false;
            return;
        }

        throw new InvalidOperationException("The path has no current point; call MoveTo first.");
    }

    private static void Validate(PathPoint point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            throw new ArgumentException($"Path coordinates must be finite, got ({point.X}, {point.Y}).", nameof(point));
    }
}
