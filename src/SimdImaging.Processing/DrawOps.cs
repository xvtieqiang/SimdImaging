using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Solid fills. These mutate the destination they are given — drawing is a sequence of writes into one canvas,
/// and copying the canvas for every rectangle would dominate the cost of the rectangle.
/// </summary>
public static class DrawOps
{
    /// <summary>Fills the whole view with <paramref name="color"/>.</summary>
    public static void Fill(PixelView destination, RgbaColor color)
    {
        for (var y = 0; y < destination.Height; y++)
            FillRow(destination.Row(y), destination.Width, color, destination.Format);
    }

    /// <summary>Fills <paramref name="rect"/>, clipped to the view. A rectangle entirely outside is a no-op.</summary>
    public static void FillRect(PixelView destination, PixelRect rect, RgbaColor color)
    {
        var left = Math.Max(rect.X, 0);
        var top = Math.Max(rect.Y, 0);
        var right = Math.Min(rect.X + rect.Width, destination.Width);
        var bottom = Math.Min(rect.Y + rect.Height, destination.Height);
        if (left >= right || top >= bottom)
            return;

        var bytesPerPixel = destination.Format.BytesPerPixel();
        for (var y = top; y < bottom; y++)
        {
            var line = destination.Row(y).Slice(left * bytesPerPixel, (right - left) * bytesPerPixel);
            FillRow(line, right - left, color, destination.Format);
        }
    }

    /// <summary>Strokes a rectangle outline of <paramref name="thickness"/> pixels, clipped to the view.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not positive.</exception>
    public static void Rect(PixelView destination, PixelRect rect, RgbaColor color, int thickness = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thickness);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        FillRect(destination, new PixelRect(rect.X, rect.Y, rect.Width, thickness), color);
        FillRect(destination, new PixelRect(rect.X, rect.Y + rect.Height - thickness, rect.Width, thickness), color);
        FillRect(destination, new PixelRect(rect.X, rect.Y, thickness, rect.Height), color);
        FillRect(destination, new PixelRect(rect.X + rect.Width - thickness, rect.Y, thickness, rect.Height), color);
    }

    /// <summary>
    /// Bresenham line with the endpoints clipped to the view first. Clipping matters for more than tidiness:
    /// an endpoint at <see cref="int.MaxValue"/> would otherwise make the loop iterate billions of times.
    /// </summary>
    public static void Line(PixelView destination, int x0, int y0, int x1, int y1, RgbaColor color)
    {
        if (!ClipLine(ref x0, ref y0, ref x1, ref y1, destination.Width, destination.Height))
            return;

        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var stepX = x0 < x1 ? 1 : -1;
        var stepY = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            SetPixel(destination, x0, y0, color);
            if (x0 == x1 && y0 == y1)
                break;

            var doubled = 2 * error;
            if (doubled >= dy)
            {
                error += dy;
                x0 += stepX;
            }

            if (doubled <= dx)
            {
                error += dx;
                y0 += stepY;
            }
        }
    }

    /// <summary>Fills the ellipse inscribed in <paramref name="bounds"/>, clipped to the view.</summary>
    public static void FillEllipse(PixelView destination, PixelRect bounds, RgbaColor color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var radiusX = bounds.Width / 2.0;
        var radiusY = bounds.Height / 2.0;
        var centreX = bounds.X + radiusX;
        var centreY = bounds.Y + radiusY;

        var top = Math.Max(bounds.Y, 0);
        var bottom = Math.Min(bounds.Y + bounds.Height, destination.Height);
        for (var y = top; y < bottom; y++)
        {
            var dy = (y + 0.5 - centreY) / radiusY;
            if (Math.Abs(dy) >= 1.0)
                continue;

            var halfWidth = radiusX * Math.Sqrt(1.0 - dy * dy);
            // Pixel-centre coverage, the same convention the polygon scanline uses: ceil/floor of the span
            // shifted by half a pixel. Math.Round would be asymmetric at the exact edges (banker's rounding).
            var left = (int)Math.Ceiling(centreX - halfWidth - 0.5);
            var right = (int)Math.Floor(centreX + halfWidth - 0.5);
            if (right < left)
                continue;

            FillRect(destination, new PixelRect(left, y, right - left + 1, 1), color);
        }
    }

    /// <summary>Fills a polygon with the even-odd rule, one scanline at a time.</summary>
    /// <exception cref="ArgumentException">Fewer than three points.</exception>
    public static void FillPolygon(PixelView destination, ReadOnlySpan<PixelPoint> points, RgbaColor color)
    {
        if (points.Length < 3)
            throw new ArgumentException("A polygon needs at least three points.", nameof(points));

        var minY = int.MaxValue;
        var maxY = int.MinValue;
        foreach (var point in points)
        {
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        minY = Math.Max(minY, 0);
        maxY = Math.Min(maxY, destination.Height - 1);

        var crossings = new List<double>(points.Length);
        for (var y = minY; y <= maxY; y++)
        {
            var scan = y + 0.5;
            crossings.Clear();

            for (var i = 0; i < points.Length; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Length];
                if ((a.Y <= scan && b.Y > scan) || (b.Y <= scan && a.Y > scan))
                {
                    var t = (scan - a.Y) / (double)(b.Y - a.Y);
                    crossings.Add(a.X + t * (b.X - a.X));
                }
            }

            if (crossings.Count < 2)
                continue;

            crossings.Sort();
            for (var i = 0; i + 1 < crossings.Count; i += 2)
            {
                var left = (int)Math.Ceiling(crossings[i] - 0.5);
                var right = (int)Math.Floor(crossings[i + 1] - 0.5);
                if (right < left)
                    continue;

                FillRect(destination, new PixelRect(left, y, right - left + 1, 1), color);
            }
        }
    }

    /// <summary>Zeroes the whole view (transparent black), padding included.</summary>
    public static void Clear(PixelView destination) => destination.Span.Clear();

    private static void SetPixel(PixelView destination, int x, int y, RgbaColor color)
    {
        var bytesPerPixel = destination.Format.BytesPerPixel();
        Span<byte> pixel = stackalloc byte[16];
        Transforms.WriteColor(color, destination.Format, pixel[..bytesPerPixel]);
        pixel[..bytesPerPixel].CopyTo(destination.Row(y).Slice(x * bytesPerPixel, bytesPerPixel));
    }

    /// <summary>Liang-Barsky. Returns false when the segment lies entirely outside.</summary>
    private static bool ClipLine(ref int x0, ref int y0, ref int x1, ref int y1, int width, int height)
    {
        double startX = x0, startY = y0, endX = x1, endY = y1;
        var dx = endX - startX;
        var dy = endY - startY;
        var t0 = 0.0;
        var t1 = 1.0;

        foreach (var (p, q) in new[]
                 {
                     (-dx, startX), (dx, width - 1 - startX),
                     (-dy, startY), (dy, height - 1 - startY),
                 })
        {
            if (p == 0)
            {
                if (q < 0)
                    return false;
                continue;
            }

            var t = q / p;
            if (p < 0)
            {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            }
            else
            {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }
        }

        if (t0 > t1)
            return false;

        x0 = (int)Math.Round(startX + t0 * dx);
        y0 = (int)Math.Round(startY + t0 * dy);
        x1 = (int)Math.Round(startX + t1 * dx);
        y1 = (int)Math.Round(startY + t1 * dy);
        return true;
    }

    private static void FillRow(Span<byte> row, int width, RgbaColor color, WorkingFormat format)
    {
        var bytesPerPixel = format.BytesPerPixel();
        Span<byte> pixel = stackalloc byte[16];
        Transforms.WriteColor(color, format, pixel[..bytesPerPixel]);

        switch (bytesPerPixel)
        {
            case 4:
            {
                var value = MemoryMarshal.Read<uint>(pixel);
                MemoryMarshal.Cast<byte, uint>(row[..(width * 4)]).Fill(value);
                break;
            }

            case 8:
            {
                var value = MemoryMarshal.Read<ulong>(pixel);
                MemoryMarshal.Cast<byte, ulong>(row[..(width * 8)]).Fill(value);
                break;
            }

            default:
            {
                // 16 bytes (float): no 128-bit scalar to broadcast through, so copy the pattern per pixel.
                for (var x = 0; x < width; x++)
                    pixel[..bytesPerPixel].CopyTo(row.Slice(x * bytesPerPixel, bytesPerPixel));
                break;
            }
        }
    }

    /// <summary>Fills a rectangle with a linear gradient between two colours.</summary>
    /// <param name="destination">The canvas.</param>
    /// <param name="rect">The area to fill, clipped.</param>
    /// <param name="from">Colour at the start of the gradient axis.</param>
    /// <param name="to">Colour at the end.</param>
    /// <param name="angleDegrees">0 = left to right, 90 = top to bottom.</param>
    public static void LinearGradient(PixelView destination, PixelRect rect, RgbaColor from, RgbaColor to,
        float angleDegrees = 0f)
    {
        var left = Math.Max(rect.X, 0);
        var top = Math.Max(rect.Y, 0);
        var right = Math.Min(rect.X + rect.Width, destination.Width);
        var bottom = Math.Min(rect.Y + rect.Height, destination.Height);
        if (left >= right || top >= bottom)
            return;

        var radians = angleDegrees * MathF.PI / 180f;
        var dx = MathF.Cos(radians);
        var dy = MathF.Sin(radians);

        // Project the four corners onto the axis so the gradient spans the rectangle exactly at any angle.
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var (x, y) in new[] { (rect.X, rect.Y), (rect.X + rect.Width, rect.Y), (rect.X, rect.Y + rect.Height), (rect.X + rect.Width, rect.Y + rect.Height) })
        {
            var projection = x * dx + y * dy;
            min = Math.Min(min, projection);
            max = Math.Max(max, projection);
        }

        var span = max - min;
        if (span <= 0f)
            span = 1f;

        var stride = destination.Width * 4;
        var row = new float[stride];
        for (var y = top; y < bottom; y++)
        {
            Array.Clear(row);
            for (var x = left; x < right; x++)
            {
                var t = Math.Clamp(((x + 0.5f) * dx + (y + 0.5f) * dy - min) / span, 0f, 1f);
                var o = x * 4;
                row[o] = Lerp(from.R, to.R, t);
                row[o + 1] = Lerp(from.G, to.G, t);
                row[o + 2] = Lerp(from.B, to.B, t);
                row[o + 3] = Lerp(from.A, to.A, t);
            }

            Pixels.WriteRowRaw(row, destination.Width, destination.Format, destination.Row(y));
        }
    }

    /// <summary>Fills a rectangle with a radial gradient: <paramref name="inner"/> at the centre, <paramref name="outer"/> at the radius.</summary>
    public static void RadialGradient(PixelView destination, PixelRect rect, float centreX, float centreY, float radius,
        RgbaColor inner, RgbaColor outer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);

        var left = Math.Max(rect.X, 0);
        var top = Math.Max(rect.Y, 0);
        var right = Math.Min(rect.X + rect.Width, destination.Width);
        var bottom = Math.Min(rect.Y + rect.Height, destination.Height);
        if (left >= right || top >= bottom)
            return;

        var stride = destination.Width * 4;
        var row = new float[stride];
        for (var y = top; y < bottom; y++)
        {
            Array.Clear(row);
            for (var x = left; x < right; x++)
            {
                var ox = x + 0.5f - centreX;
                var oy = y + 0.5f - centreY;
                var t = Math.Clamp(MathF.Sqrt(ox * ox + oy * oy) / radius, 0f, 1f);
                var o = x * 4;
                row[o] = Lerp(inner.R, outer.R, t);
                row[o + 1] = Lerp(inner.G, outer.G, t);
                row[o + 2] = Lerp(inner.B, outer.B, t);
                row[o + 3] = Lerp(inner.A, outer.A, t);
            }

            Pixels.WriteRowRaw(row, destination.Width, destination.Format, destination.Row(y));
        }
    }

    /// <summary>
    /// Fills a rectangle with an angular sweep: the colour runs once around the centre, so the seam sits at
    /// <paramref name="startAngleDegrees"/>. Angles grow clockwise in this y-down coordinate system.
    /// </summary>
    /// <param name="destination">The canvas.</param>
    /// <param name="rect">The area to fill, clipped.</param>
    /// <param name="centreX">Sweep centre.</param>
    /// <param name="centreY">Sweep centre.</param>
    /// <param name="from">Colour at the start angle.</param>
    /// <param name="to">Colour one turn later.</param>
    /// <param name="startAngleDegrees">Where the sweep begins and where the seam falls.</param>
    public static void SweepGradient(PixelView destination, PixelRect rect, float centreX, float centreY,
        RgbaColor from, RgbaColor to, float startAngleDegrees = 0f)
    {
        var left = Math.Max(rect.X, 0);
        var top = Math.Max(rect.Y, 0);
        var right = Math.Min(rect.X + rect.Width, destination.Width);
        var bottom = Math.Min(rect.Y + rect.Height, destination.Height);
        if (left >= right || top >= bottom)
            return;

        var stride = destination.Width * 4;
        var row = new float[stride];

        for (var y = top; y < bottom; y++)
        {
            Array.Clear(row);
            for (var x = left; x < right; x++)
            {
                var ox = x + 0.5f - centreX;
                var oy = y + 0.5f - centreY;
                var angle = MathF.Atan2(oy, ox) * (180f / MathF.PI);

                var t = (angle - startAngleDegrees) % 360f;
                if (t < 0f)
                    t += 360f;
                t /= 360f;

                var offset = x * 4;
                row[offset] = Lerp(from.R, to.R, t);
                row[offset + 1] = Lerp(from.G, to.G, t);
                row[offset + 2] = Lerp(from.B, to.B, t);
                row[offset + 3] = Lerp(from.A, to.A, t);
            }

            Pixels.WriteRowRaw(row, destination.Width, destination.Format, destination.Row(y));
        }
    }

    private static float Lerp(byte a, byte b, float t) => (a + (b - a) * t) / 255f;

    /// <summary>
    /// Scanline flood fill from a seed. Colour distance is compared in straight RGBA; the visited mask bounds
    /// the work, so a tolerance that matches everything still terminates in O(width × height).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The seed is outside the view.</exception>
    public static void FloodFill(PixelView destination, int x, int y, RgbaColor color, float tolerance = 0f)
    {
        if ((uint)x >= (uint)destination.Width)
            throw new ArgumentOutOfRangeException(nameof(x), x, "Seed X is outside the view.");
        if ((uint)y >= (uint)destination.Height)
            throw new ArgumentOutOfRangeException(nameof(y), y, "Seed Y is outside the view.");
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);

        var width = destination.Width;
        var height = destination.Height;
        var threshold = tolerance * tolerance;

        // Arrays, not stackalloc: the local Match function captures them.
        var target = new float[4];
        ReadPixel(destination, x, y, target);

        var replacement = new float[4];
        replacement[0] = color.R / 255f;
        replacement[1] = color.G / 255f;
        replacement[2] = color.B / 255f;
        replacement[3] = color.A / 255f;

        if (Matches(replacement, target, threshold))
            return;

        var visited = new bool[width * height];
        var pending = new Stack<int>();
        pending.Push(y * width + x);
        var sample = new float[4];

        bool Match(int px, int py)
        {
            ReadPixel(destination, px, py, sample);
            return Matches(sample, target, threshold);
        }

        while (pending.Count > 0)
        {
            var index = pending.Pop();
            var seedY = index / width;
            var seedX = index % width;
            if (visited[index] || !Match(seedX, seedY))
                continue;

            var left = seedX;
            while (left > 0 && !visited[seedY * width + left - 1] && Match(left - 1, seedY))
                left--;

            var right = seedX;
            while (right < width - 1 && !visited[seedY * width + right + 1] && Match(right + 1, seedY))
                right++;

            for (var px = left; px <= right; px++)
            {
                SetPixel(destination, px, seedY, color);
                visited[seedY * width + px] = true;
            }

            for (var offset = -1; offset <= 1; offset += 2)
            {
                var nextY = seedY + offset;
                if (nextY < 0 || nextY >= height)
                    continue;

                var px = left;
                while (px <= right)
                {
                    if (!visited[nextY * width + px] && Match(px, nextY))
                    {
                        pending.Push(nextY * width + px);

                        // Skip this contiguous run, not the whole span: a span can hold several runs.
                        while (px + 1 <= right && !visited[nextY * width + px + 1] && Match(px + 1, nextY))
                            px++;
                    }

                    px++;
                }
            }
        }
    }

    private static bool Matches(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float threshold)
    {
        var dr = a[0] - b[0];
        var dg = a[1] - b[1];
        var db = a[2] - b[2];
        var da = a[3] - b[3];
        return dr * dr + dg * dg + db * db + da * da <= threshold;
    }

    private static void ReadPixel(PixelView view, int x, int y, Span<float> destination)
    {
        var bytesPerPixel = view.Format.BytesPerPixel();
        var span = view.Row(y).Slice(x * bytesPerPixel, bytesPerPixel);

        switch (view.Format)
        {
            case WorkingFormat.Rgba8:
                destination[0] = span[0] / 255f;
                destination[1] = span[1] / 255f;
                destination[2] = span[2] / 255f;
                destination[3] = span[3] / 255f;
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(span);
                destination[0] = samples[0] / 65535f;
                destination[1] = samples[1] / 65535f;
                destination[2] = samples[2] / 65535f;
                destination[3] = samples[3] / 65535f;
                break;
            }

            default:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span);
                destination[0] = samples[0];
                destination[1] = samples[1];
                destination[2] = samples[2];
                destination[3] = samples[3];
                break;
            }
        }
    }

}
