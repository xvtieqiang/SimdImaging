using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>
/// Path rasterisation: Bézier flattening, then a coverage scanline fill with the even-odd or non-zero rule.
/// <para>
/// Unlike the shape primitives (<see cref="DrawOps.FillEllipse"/>, <see cref="DrawOps.FillPolygon"/>), which
/// decide per pixel centre, a path is antialiased: sixteen sub-scanlines per row give the vertical term and
/// the exact overlap of each span with each pixel gives the horizontal one. A pixel fully inside is written
/// straight through; only the rim is blended, so a solid fill still costs one row write per span.
/// </para>
/// </summary>
public static class PathOps
{
    /// <summary>Fills a path, one scanline at a time.</summary>
    /// <param name="destination">The canvas.</param>
    /// <param name="path">The geometry; open subpaths are closed implicitly, as fill always does.</param>
    /// <param name="color">The fill colour.</param>
    /// <param name="rule">Which points count as inside.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static void Fill(PixelView destination, PixelPath path, RgbaColor color,
        PathFillRule rule = PathFillRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsEmpty)
            return;

        FillSubPaths(destination, PathFlattener.Flatten(path), rule, color);
    }

    /// <summary>
    /// Multiplies every pixel's alpha by the path's coverage — a clip, or the mask half of one. Pixels fully
    /// inside keep their alpha; pixels outside become transparent; the edge is scaled by partial coverage.
    /// </summary>
    /// <param name="destination">The canvas; alpha is modified in place.</param>
    /// <param name="path">The clip region.</param>
    /// <param name="rule">Which points count as inside.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static void Clip(PixelView destination, PixelPath path, PathFillRule rule = PathFillRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.IsEmpty)
        {
            // No geometry covers nothing: a clip to an empty path removes the whole image, the way SVG does.
            for (var y = 0; y < destination.Height; y++)
            {
                for (var x = 0; x < destination.Width; x++)
                    ScaleAlpha(destination, x, y, 0f);
            }

            return;
        }

        ClipSubPaths(destination, PathFlattener.Flatten(path), rule);
    }

    /// <summary>Strokes a path with a one-pixel miter-joined, butt-capped line.</summary>
    public static void Stroke(PixelView destination, PixelPath path, RgbaColor color) =>
        Stroke(destination, path, color, new StrokeOptions());

    /// <summary>
    /// Strokes a path: each segment becomes a rectangle, corners and ends are filled in by the requested join
    /// and cap, then everything is united by filling with the non-zero rule. Every piece is wound the same
    /// way, so overlaps add winding instead of cancelling into holes.
    /// </summary>
    /// <param name="destination">The canvas.</param>
    /// <param name="path">The geometry.</param>
    /// <param name="color">The stroke colour.</param>
    /// <param name="options">Width, join, cap and dash pattern.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive, the miter limit is below 1, or
    /// a dash length is not positive.</exception>
    public static void Stroke(PixelView destination, PixelPath path, RgbaColor color, StrokeOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        Validate(options);
        if (path.IsEmpty)
            return;

        var half = options.Width * 0.5f;
        var pieces = new List<SubPath>();
        var scratch = new List<PathPoint>(64);

        foreach (var subpath in PathFlattener.Flatten(path))
        {
            var points = PathFlattener.Dedupe(subpath.Points);
            if (points.Count == 0)
                continue;

            if (points.Count == 1)
            {
                if (options.Cap == LineCap.Round && half > 0f)
                    AddDisc(pieces, scratch, points[0], half);

                continue;
            }

            if (subpath.Closed)
                points.Add(points[0]);

            if (options.DashPattern.Length > 0)
            {
                foreach (var run in SplitDashes(points, options.DashPattern.Span, options.DashOffset))
                    StrokePolyline(pieces, scratch, run, false, half, options);
            }
            else
            {
                StrokePolyline(pieces, scratch, points, subpath.Closed, half, options);
            }
        }

        FillSubPaths(destination, pieces, PathFillRule.NonZero, color);
    }

    private static void Validate(StrokeOptions options)
    {
        if (!float.IsFinite(options.Width) || options.Width <= 0f)
            throw new ArgumentOutOfRangeException(nameof(options), options.Width, "Stroke width must be finite and positive.");
        if (!float.IsFinite(options.MiterLimit) || options.MiterLimit < 1f)
            throw new ArgumentOutOfRangeException(nameof(options), options.MiterLimit, "Miter limit must be at least 1.");
        if (!float.IsFinite(options.DashOffset))
            throw new ArgumentOutOfRangeException(nameof(options), options.DashOffset, "Dash offset must be finite.");

        foreach (var dash in options.DashPattern.Span)
        {
            if (!float.IsFinite(dash) || dash <= 0f)
                throw new ArgumentOutOfRangeException(nameof(options), dash, "Dash lengths must be finite and positive.");
        }
    }

    // ---- stroke geometry -------------------------------------------------------------------------------

    private static void StrokePolyline(List<SubPath> pieces, List<PathPoint> scratch, List<PathPoint> points,
        bool closed, float half, StrokeOptions options)
    {
        var segments = points.Count - 1;
        if (segments <= 0)
            return;

        for (var i = 0; i < segments; i++)
            AddSegment(pieces, scratch, points[i], points[i + 1], half);

        for (var i = 1; i < segments; i++)
            AddJoin(pieces, scratch, points[i - 1], points[i], points[i + 1], half, options.Join, options.MiterLimit);

        if (closed && segments >= 2)
            AddJoin(pieces, scratch, points[segments - 1], points[0], points[1], half, options.Join, options.MiterLimit);

        if (closed)
            return;

        AddCap(pieces, scratch, points[0], Unit(points[1], points[0]), half, options.Cap);
        AddCap(pieces, scratch, points[^1], Unit(points[^2], points[^1]), half, options.Cap);
    }

    private static void AddSegment(List<SubPath> pieces, List<PathPoint> scratch, PathPoint a, PathPoint b, float half)
    {
        var (dx, dy) = Unit(a, b);
        if (dx == 0f && dy == 0f)
            return;

        var nx = -dy * half;
        var ny = dx * half;

        scratch.Clear();
        scratch.Add(new PathPoint(a.X + nx, a.Y + ny));
        scratch.Add(new PathPoint(b.X + nx, b.Y + ny));
        scratch.Add(new PathPoint(b.X - nx, b.Y - ny));
        scratch.Add(new PathPoint(a.X - nx, a.Y - ny));
        AddPiece(pieces, scratch);
    }

    private static void AddJoin(List<SubPath> pieces, List<PathPoint> scratch, PathPoint a, PathPoint b, PathPoint c,
        float half, LineJoin join, float miterLimit)
    {
        var (d1X, d1Y) = Unit(a, b);
        var (d2X, d2Y) = Unit(b, c);
        if ((d1X == 0f && d1Y == 0f) || (d2X == 0f && d2Y == 0f))
            return;

        var cross = d1X * d2Y - d1Y * d2X;
        var dot = d1X * d2X + d1Y * d2Y;

        // A direction change below a millionth of a radian leaves nothing to fill: the two rectangles
        // already overlap.
        if (MathF.Abs(cross) < 1e-6f && dot > 0f)
            return;

        // The outside of the corner is the right-hand side of a left turn and vice versa.
        var side = cross > 0f ? -1f : 1f;
        var o1X = -d1Y * side * half;
        var o1Y = d1X * side * half;
        var o2X = -d2Y * side * half;
        var o2Y = d2X * side * half;

        scratch.Clear();
        scratch.Add(b);

        switch (join)
        {
            case LineJoin.Round:
            {
                var from = MathF.Atan2(o1Y, o1X);
                var to = MathF.Atan2(o2Y, o2X);
                var delta = NormaliseAngle(to - from);
                var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI / 16f)));
                for (var i = 0; i <= steps; i++)
                {
                    var angle = from + delta * i / steps;
                    scratch.Add(new PathPoint(b.X + MathF.Cos(angle) * half, b.Y + MathF.Sin(angle) * half));
                }

                break;
            }

            case LineJoin.Bevel:
                scratch.Add(new PathPoint(b.X + o1X, b.Y + o1Y));
                scratch.Add(new PathPoint(b.X + o2X, b.Y + o2Y));
                break;

            default:
            {
                var miterX = o1X + o2X;
                var miterY = o1Y + o2Y;
                var length = MathF.Sqrt(miterX * miterX + miterY * miterY);

                // Opposite normals mean a full reversal; there is no miter direction to speak of.
                if (length < 1e-6f)
                {
                    scratch.Add(new PathPoint(b.X + o1X, b.Y + o1Y));
                    scratch.Add(new PathPoint(b.X + o2X, b.Y + o2Y));
                    break;
                }

                miterX /= length;
                miterY /= length;

                var cosHalf = (miterX * o1X + miterY * o1Y) / half;
                var miterLength = cosHalf > 1e-6f ? half / cosHalf : float.MaxValue;

                if (miterLength > miterLimit * half)
                {
                    scratch.Add(new PathPoint(b.X + o1X, b.Y + o1Y));
                    scratch.Add(new PathPoint(b.X + o2X, b.Y + o2Y));
                    break;
                }

                scratch.Add(new PathPoint(b.X + o1X, b.Y + o1Y));
                scratch.Add(new PathPoint(b.X + miterX * miterLength, b.Y + miterY * miterLength));
                scratch.Add(new PathPoint(b.X + o2X, b.Y + o2Y));
                break;
            }
        }

        AddPiece(pieces, scratch);
    }

    private static void AddCap(List<SubPath> pieces, List<PathPoint> scratch, PathPoint point, (float X, float Y) outward,
        float half, LineCap cap)
    {
        if (cap == LineCap.Butt || (outward.X == 0f && outward.Y == 0f))
            return;

        var nx = -outward.Y * half;
        var ny = outward.X * half;

        scratch.Clear();
        switch (cap)
        {
            case LineCap.Square:
            {
                var ex = outward.X * half;
                var ey = outward.Y * half;
                scratch.Add(new PathPoint(point.X + nx, point.Y + ny));
                scratch.Add(new PathPoint(point.X + nx + ex, point.Y + ny + ey));
                scratch.Add(new PathPoint(point.X - nx + ex, point.Y - ny + ey));
                scratch.Add(new PathPoint(point.X - nx, point.Y - ny));
                break;
            }

            default:
            {
                // Semicircle from +n to -n through the outward direction. The normal is the left normal of
                // `outward`, so sweeping -π passes through it rather than around the back.
                var from = MathF.Atan2(ny, nx);
                const int Steps = 16;
                scratch.Add(point);
                for (var i = 0; i <= Steps; i++)
                {
                    var angle = from - MathF.PI * i / Steps;
                    scratch.Add(new PathPoint(point.X + MathF.Cos(angle) * half, point.Y + MathF.Sin(angle) * half));
                }

                break;
            }
        }

        AddPiece(pieces, scratch);
    }

    private static void AddDisc(List<SubPath> pieces, List<PathPoint> scratch, PathPoint centre, float radius)
    {
        const int Steps = 24;
        scratch.Clear();
        for (var i = 0; i < Steps; i++)
        {
            var angle = 2f * MathF.PI * i / Steps;
            scratch.Add(new PathPoint(centre.X + MathF.Cos(angle) * radius, centre.Y + MathF.Sin(angle) * radius));
        }

        AddPiece(pieces, scratch);
    }

    /// <summary>Adds one convex piece, reversed when needed so every piece winds the same way — the property
    /// that makes a non-zero union of overlapping pieces behave.</summary>
    private static void AddPiece(List<SubPath> pieces, List<PathPoint> points)
    {
        if (points.Count < 3)
            return;

        var area = SignedArea(points);
        if (MathF.Abs(area) < 1e-6f)
            return;

        var piece = new SubPath();
        if (area > 0f)
        {
            piece.Points.AddRange(points);
        }
        else
        {
            for (var i = points.Count - 1; i >= 0; i--)
                piece.Points.Add(points[i]);
        }

        piece.Closed = true;
        pieces.Add(piece);
    }

    private static float SignedArea(List<PathPoint> points)
    {
        var total = 0f;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            total += a.X * b.Y - b.X * a.Y;
        }

        return total * 0.5f;
    }

    private static (float X, float Y) Unit(PathPoint from, PathPoint to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6f)
            return (0f, 0f);
        return (dx / length, dy / length);
    }

    /// <summary>Wraps an angle into (-π, π], the shortest way round.</summary>
    private static float NormaliseAngle(float angle)
    {
        while (angle > MathF.PI)
            angle -= 2f * MathF.PI;
        while (angle <= -MathF.PI)
            angle += 2f * MathF.PI;
        return angle;
    }

    // ---- dashes ----------------------------------------------------------------------------------------

    private static List<List<PathPoint>> SplitDashes(List<PathPoint> points, ReadOnlySpan<float> pattern, float offset)
    {
        var runs = new List<List<PathPoint>>();
        var lengths = NormalisePattern(pattern);
        if (lengths.Length == 0)
        {
            runs.Add(points);
            return runs;
        }

        var total = 0f;
        foreach (var length in lengths)
            total += length;

        var phase = offset % total;
        if (phase < 0f)
            phase += total;

        var index = 0;
        while (phase >= lengths[index])
        {
            phase -= lengths[index];
            index = (index + 1) % lengths.Length;
        }

        var remaining = lengths[index] - phase;
        var on = index % 2 == 0;
        var run = new List<PathPoint>();
        if (on)
            run.Add(points[0]);

        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (length <= 0f)
                continue;

            var travelled = 0f;
            while (travelled < length)
            {
                var step = MathF.Min(remaining, length - travelled);
                travelled += step;
                remaining -= step;

                var t = travelled / length;
                var point = new PathPoint(a.X + dx * t, a.Y + dy * t);
                if (on)
                    run.Add(point);

                if (remaining > 1e-6f)
                    continue;

                if (on)
                {
                    if (run.Count > 1)
                        runs.Add(run);
                    run = [];
                }
                else
                {
                    run = [point];
                }

                on = !on;
                index = (index + 1) % lengths.Length;
                remaining = lengths[index];
            }
        }

        if (on && run.Count > 1)
            runs.Add(run);

        return runs;
    }

    private static float[] NormalisePattern(ReadOnlySpan<float> pattern)
    {
        var lengths = new float[pattern.Length % 2 == 0 ? pattern.Length : pattern.Length * 2];
        for (var i = 0; i < lengths.Length; i++)
            lengths[i] = pattern[i % pattern.Length];
        return lengths;
    }

    // ---- coverage rasteriser ---------------------------------------------------------------------------

    /// <summary>
    /// Vertical sub-scanlines per pixel row. Sixteen is 2^-4, so sixteen full samples sum to exactly one in
    /// binary floating point and an interior pixel needs no tolerance to take the replace path.
    /// </summary>
    private const int SubScanlines = 16;

    private const float SampleWeight = 1f / SubScanlines;

    private readonly record struct Edge(float X0, float Y0, float X1, float Y1, float Slope, int Direction);

    private readonly record struct Crossing(float X, int Direction);

    private static void FillSubPaths(PixelView destination, List<SubPath> subpaths, PathFillRule rule, RgbaColor color) =>
        Rasterize(destination, subpaths, rule, (y, coverage) => CompositeRow(destination, y, coverage, color));

    private static void ClipSubPaths(PixelView destination, List<SubPath> subpaths, PathFillRule rule) =>
        Rasterize(destination, subpaths, rule, (y, coverage) => MaskRow(destination, y, coverage), everyRow: true);

    /// <summary>
    /// Scanline rasteriser with coverage: sixteen sub-scanlines per row supply the vertical term, the exact
    /// overlap of each span with each pixel the horizontal one. Edges are bucketed by their first row and kept
    /// in an active list, so a row pays for the edges that cross it, not for the whole path.
    /// </summary>
    /// <param name="everyRow">Also report the rows the path does not reach, with zero coverage. A mask needs
    /// that: outside the geometry the alpha must be cleared, not left alone.</param>
    private static void Rasterize(PixelView destination, List<SubPath> subpaths, PathFillRule rule,
        Action<int, float[]> row, bool everyRow = false)
    {
        var edges = new List<Edge>();
        var minY = float.MaxValue;
        var maxY = float.MinValue;

        foreach (var subpath in subpaths)
        {
            var points = subpath.Points;
            if (points.Count < 2)
                continue;

            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if (a.Y == b.Y)
                    continue;

                var downward = a.Y < b.Y;
                var x0 = downward ? a.X : b.X;
                var y0 = downward ? a.Y : b.Y;
                var x1 = downward ? b.X : a.X;
                var y1 = downward ? b.Y : a.Y;

                edges.Add(new Edge(x0, y0, x1, y1, (x1 - x0) / (y1 - y0), downward ? 1 : -1));
                minY = MathF.Min(minY, y0);
                maxY = MathF.Max(maxY, y1);
            }
        }

        if (edges.Count == 0)
        {
            if (everyRow)
            {
                var empty = new float[destination.Width];
                for (var y = 0; y < destination.Height; y++)
                    row(y, empty);
            }

            return;
        }

        // Clamp in float before the cast: a path far outside the canvas would otherwise overflow to
        // int.MinValue and skip the whole loop.
        var top = (int)MathF.Max(0f, MathF.Floor(minY));
        var bottom = (int)MathF.Min(destination.Height - 1f, MathF.Ceiling(maxY));
        if (top > bottom)
            return;

        var firstRow = everyRow ? 0 : top;
        var lastRow = everyRow ? destination.Height - 1 : bottom;

        var rows = bottom - top + 1;
        var bucketHead = new int[rows];
        Array.Fill(bucketHead, -1);
        var next = new int[edges.Count];
        for (var i = 0; i < edges.Count; i++)
        {
            var index = (int)Math.Clamp(MathF.Floor(edges[i].Y0) - top, 0f, rows - 1f);
            next[i] = bucketHead[index];
            bucketHead[index] = i;
        }

        var active = new int[edges.Count];
        var activeCount = 0;
        var coverage = new float[destination.Width];
        var crossings = new List<Crossing>(edges.Count);

        for (var y = firstRow; y <= lastRow; y++)
        {
            if (y < top || y > bottom)
            {
                Array.Clear(coverage, 0, coverage.Length);
                row(y, coverage);
                continue;
            }

            var live = 0;
            for (var i = 0; i < activeCount; i++)
            {
                if (edges[active[i]].Y1 > y)
                    active[live++] = active[i];
            }

            activeCount = live;
            for (var i = bucketHead[y - top]; i >= 0; i = next[i])
                active[activeCount++] = i;

            if (activeCount == 0)
                continue;

            Array.Clear(coverage, 0, coverage.Length);

            for (var s = 0; s < SubScanlines; s++)
            {
                var scan = y + (s + 0.5f) / SubScanlines;
                crossings.Clear();

                for (var i = 0; i < activeCount; i++)
                {
                    var edge = edges[active[i]];
                    if (edge.Y0 <= scan && edge.Y1 > scan)
                        crossings.Add(new Crossing(edge.X0 + (scan - edge.Y0) * edge.Slope, edge.Direction));
                }

                if (crossings.Count < 2)
                    continue;

                crossings.Sort(static (a, b) =>
                {
                    var delta = a.X.CompareTo(b.X);
                    return delta != 0 ? delta : a.Direction.CompareTo(b.Direction);
                });

                AccumulateSpans(crossings, coverage, rule);
            }

            row(y, coverage);
        }
    }

    private static void AccumulateSpans(List<Crossing> crossings, float[] coverage, PathFillRule rule)
    {
        var winding = 0;
        var inside = false;
        var spanStart = 0f;

        foreach (var crossing in crossings)
        {
            var wasInside = rule == PathFillRule.NonZero ? winding != 0 : inside;

            if (rule == PathFillRule.NonZero)
                winding += crossing.Direction;
            else
                inside = !inside;

            var nowInside = rule == PathFillRule.NonZero ? winding != 0 : inside;

            if (!wasInside && nowInside)
            {
                spanStart = crossing.X;
            }
            else if (wasInside && !nowInside)
            {
                AccumulateSpan(coverage, spanStart, crossing.X);
            }
        }
    }

    /// <summary>Adds the exact horizontal overlap of one span with each pixel, weighted by one sub-scanline.</summary>
    private static void AccumulateSpan(float[] coverage, float x0, float x1)
    {
        var width = coverage.Length;
        var start = MathF.Max(x0, 0f);
        var end = MathF.Min(x1, width);
        if (end <= start)
            return;

        var first = (int)MathF.Floor(start);
        var last = (int)MathF.Floor(end);
        if (last >= width)
            last = width - 1;

        if (first == last)
        {
            coverage[first] += (end - start) * SampleWeight;
            return;
        }

        coverage[first] += (first + 1 - start) * SampleWeight;
        for (var x = first + 1; x < last; x++)
            coverage[x] += SampleWeight;
        coverage[last] += (end - last) * SampleWeight;
    }

    private static void CompositeRow(PixelView destination, int y, float[] coverage, RgbaColor color)
    {
        var width = destination.Width;
        var x = 0;

        if (color.A == 255)
        {
            // Interior runs replace; only the edge pixels blend. A solid fill therefore costs the same as it
            // did before antialiasing, and coverage 1 with an opaque colour is exactly a replace anyway.
            while (x < width)
            {
                if (coverage[x] >= 1f)
                {
                    var start = x;
                    while (x < width && coverage[x] >= 1f)
                        x++;

                    DrawOps.FillRect(destination, new PixelRect(start, y, x - start, 1), color);
                    continue;
                }

                if (coverage[x] > 0f)
                    BlendPixel(destination, x, y, color, MathF.Min(coverage[x], 1f));
                x++;
            }

            return;
        }

        for (; x < width; x++)
        {
            if (coverage[x] > 0f)
                BlendPixel(destination, x, y, color, MathF.Min(coverage[x], 1f));
        }
    }

    /// <summary>Source-over with the coverage folded into the source alpha, in straight (unassociated) space.</summary>
    private static void BlendPixel(PixelView destination, int x, int y, RgbaColor color, float coverage)
    {
        var sourceAlpha = color.A / 255f * coverage;
        if (sourceAlpha <= 0f)
            return;

        ReadPixel(destination, x, y, out var r, out var g, out var b, out var a);
        var destinationWeight = a * (1f - sourceAlpha);
        var alpha = sourceAlpha + destinationWeight;

        if (alpha <= 0f)
        {
            WritePixel(destination, x, y, 0f, 0f, 0f, 0f);
            return;
        }

        var scale = 1f / alpha;
        WritePixel(destination, x, y,
            (color.R / 255f * sourceAlpha + r * destinationWeight) * scale,
            (color.G / 255f * sourceAlpha + g * destinationWeight) * scale,
            (color.B / 255f * sourceAlpha + b * destinationWeight) * scale,
            alpha);
    }

    private static void MaskRow(PixelView destination, int y, float[] coverage)
    {
        for (var x = 0; x < destination.Width; x++)
        {
            var c = coverage[x];
            if (c >= 1f)
                continue;

            ScaleAlpha(destination, x, y, MathF.Max(c, 0f));
        }
    }

    private static void ScaleAlpha(PixelView view, int x, int y, float coverage)
    {
        var size = view.Format.BytesPerPixel();
        var span = view.Row(y).Slice(x * size, size);

        switch (view.Format)
        {
            case WorkingFormat.Rgba8:
                span[3] = ToByte(span[3] / 255f * coverage);
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(span);
                samples[3] = ToUInt16(samples[3] / 65535f * coverage);
                break;
            }

            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(span);
                samples[3] *= coverage;
                break;
            }
        }
    }

    private static void ReadPixel(PixelView view, int x, int y, out float r, out float g, out float b, out float a)
    {
        var size = view.Format.BytesPerPixel();
        var span = view.Row(y).Slice(x * size, size);

        switch (view.Format)
        {
            case WorkingFormat.Rgba8:
                r = span[0] / 255f;
                g = span[1] / 255f;
                b = span[2] / 255f;
                a = span[3] / 255f;
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(span);
                r = samples[0] / 65535f;
                g = samples[1] / 65535f;
                b = samples[2] / 65535f;
                a = samples[3] / 65535f;
                break;
            }

            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(span);
                r = samples[0];
                g = samples[1];
                b = samples[2];
                a = samples[3];
                break;
            }
        }
    }

    private static void WritePixel(PixelView view, int x, int y, float r, float g, float b, float a)
    {
        var size = view.Format.BytesPerPixel();
        var span = view.Row(y).Slice(x * size, size);

        switch (view.Format)
        {
            case WorkingFormat.Rgba8:
                span[0] = ToByte(r);
                span[1] = ToByte(g);
                span[2] = ToByte(b);
                span[3] = ToByte(a);
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(span);
                samples[0] = ToUInt16(r);
                samples[1] = ToUInt16(g);
                samples[2] = ToUInt16(b);
                samples[3] = ToUInt16(a);
                break;
            }

            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(span);
                samples[0] = r;
                samples[1] = g;
                samples[2] = b;
                samples[3] = a;
                break;
            }
        }
    }

    private static byte ToByte(float value)
    {
        var scaled = value * 255f + 0.5f;
        if (scaled <= 0f) return 0;
        if (scaled >= 255f) return 255;
        return (byte)scaled;
    }

    private static ushort ToUInt16(float value)
    {
        var scaled = value * 65535f + 0.5f;
        if (scaled <= 0f) return 0;
        if (scaled >= 65535f) return 65535;
        return (ushort)scaled;
    }
}
