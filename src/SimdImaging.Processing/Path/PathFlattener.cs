namespace SimdImaging.Processing;

/// <summary>A flattened subpath: a polyline plus whether the source contour was closed.</summary>
internal sealed class SubPath
{
    public List<PathPoint> Points { get; } = [];

    public bool Closed { get; set; }

    public PathPoint Start { get; set; }
}

/// <summary>
/// Turns Bézier commands into polylines, adaptively: a segment is split until both control points are within
/// the tolerance of its chord. A cubic lies inside the convex hull of its four points, so that test bounds the
/// distance from the curve to the chord — no sampling, no overshoot.
/// </summary>
internal static class PathFlattener
{
    /// <summary>Maximum deviation from the true curve, in pixels. A quarter pixel is below what a pixel-centre
    /// rasteriser can express, so refining further changes nothing that is drawn.</summary>
    public const float Tolerance = 0.25f;

    /// <summary>Guards against a pathological curve that never tests flat (a NaN cannot, a finite one will).</summary>
    private const int MaxDepth = 18;

    public static List<SubPath> Flatten(PixelPath path)
    {
        var subpaths = new List<SubPath>();
        SubPath? current = null;
        var point = default(PathPoint);
        var hasPoint = false;

        foreach (var command in path.Commands)
        {
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    current = new SubPath { Start = command.End };
                    current.Points.Add(command.End);
                    subpaths.Add(current);
                    point = command.End;
                    hasPoint = true;
                    break;

                case PathVerb.LineTo:
                    current = EnsureSubPath(point, hasPoint, current, subpaths);
                    Add(current.Points, command.End);
                    point = command.End;
                    break;

                case PathVerb.CubicTo:
                    current = EnsureSubPath(point, hasPoint, current, subpaths);
                    FlattenCubic(point, command.Control1, command.Control2, command.End, current.Points);
                    point = command.End;
                    break;

                case PathVerb.QuadraticTo:
                {
                    current = EnsureSubPath(point, hasPoint, current, subpaths);
                    // Exact degree elevation: a quadratic is a cubic with two-thirds-weighted controls.
                    var control1 = point + (command.Control1 - point) * (2f / 3f);
                    var control2 = command.End + (command.Control1 - command.End) * (2f / 3f);
                    FlattenCubic(point, control1, control2, command.End, current.Points);
                    point = command.End;
                    break;
                }

                case PathVerb.Close:
                    if (current is not null)
                    {
                        current.Closed = true;
                        point = current.Start;
                        hasPoint = true;
                        current = null;
                    }

                    break;
            }
        }

        return subpaths;
    }

    private static SubPath EnsureSubPath(PathPoint point, bool hasPoint, SubPath? current, List<SubPath> subpaths)
    {
        if (current is not null)
            return current;

        if (!hasPoint)
            throw new ArgumentException("A drawing command precedes any MoveTo.", "path");

        // A command after Close starts a fresh contour at the same point, the way SVG resumes a path.
        var created = new SubPath { Start = point };
        created.Points.Add(point);
        subpaths.Add(created);
        return created;
    }

    private static void FlattenCubic(PathPoint p0, PathPoint p1, PathPoint p2, PathPoint p3, List<PathPoint> output,
        int depth = 0)
    {
        if (depth >= MaxDepth || IsFlat(p0, p1, p2, p3))
        {
            Add(output, p3);
            return;
        }

        var p01 = Midpoint(p0, p1);
        var p12 = Midpoint(p1, p2);
        var p23 = Midpoint(p2, p3);
        var p012 = Midpoint(p01, p12);
        var p123 = Midpoint(p12, p23);
        var middle = Midpoint(p012, p123);

        FlattenCubic(p0, p01, p012, middle, output, depth + 1);
        FlattenCubic(middle, p123, p23, p3, output, depth + 1);
    }

    private static bool IsFlat(PathPoint p0, PathPoint p1, PathPoint p2, PathPoint p3) =>
        DistanceToChord(p1, p0, p3) <= Tolerance && DistanceToChord(p2, p0, p3) <= Tolerance;

    private static float DistanceToChord(PathPoint point, PathPoint start, PathPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;

        var px = point.X - start.X;
        var py = point.Y - start.Y;

        if (lengthSquared <= 1e-12f)
            return MathF.Sqrt(px * px + py * py);

        var cross = MathF.Abs(dx * py - dy * px);
        return cross / MathF.Sqrt(lengthSquared);
    }

    private static PathPoint Midpoint(PathPoint a, PathPoint b) => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);

    private static void Add(List<PathPoint> points, PathPoint point)
    {
        if (points.Count > 0)
        {
            var last = points[^1];
            if (MathF.Abs(last.X - point.X) < 1e-4f && MathF.Abs(last.Y - point.Y) < 1e-4f)
                return;
        }

        points.Add(point);
    }

    /// <summary>Drops consecutive duplicates so a stroke never builds a zero-length segment.</summary>
    public static List<PathPoint> Dedupe(List<PathPoint> points)
    {
        var result = new List<PathPoint>(points.Count);
        foreach (var point in points)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                if (MathF.Abs(last.X - point.X) < 1e-4f && MathF.Abs(last.Y - point.Y) < 1e-4f)
                    continue;
            }

            result.Add(point);
        }

        return result;
    }
}
