using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Geometry;

/// <summary>Maps a destination coordinate to a source coordinate. Written as out parameters so the call
/// allocates nothing per pixel.</summary>
/// <param name="destinationX">Destination column, pixel centre based.</param>
/// <param name="destinationY">Destination row.</param>
/// <param name="sourceX">Where to read.</param>
/// <param name="sourceY">Where to read.</param>
public delegate void PixelWarp(float destinationX, float destinationY, out float sourceX, out float sourceY);

/// <summary>
/// The sampler the geometric operators share, exposed for callers that need a single colour: nearest and
/// bilinear only, because those are the two every other filter agrees on. Coordinates are continuous with the
/// pixel centre at <c>i + 0.5</c>.
/// </summary>
public static class Sampling
{
    /// <summary>Reads one colour at a continuous coordinate, clamping at the edges.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="x">Column coordinate.</param>
    /// <param name="y">Row coordinate.</param>
    /// <param name="resampler">Nearest or bilinear.</param>
    /// <exception cref="NotSupportedException">Any other resampler.</exception>
    public static RgbaColor Sample(PixelView source, float x, float y, Resampler resampler = Resampler.Bilinear) =>
        resampler switch
        {
            Resampler.Nearest => Nearest(source, x, y),
            Resampler.Bilinear => Bilinear(source, x, y),
            _ => throw new NotSupportedException(
                $"{resampler} is not available for point sampling; use Nearest or Bilinear."),
        };

    /// <summary>Reads many colours at once; <paramref name="destination"/> must be at least as long.</summary>
    /// <exception cref="ArgumentException">The destination is shorter than the point list.</exception>
    public static void Sample(PixelView source, ReadOnlySpan<(float X, float Y)> points, Span<RgbaColor> destination,
        Resampler resampler = Resampler.Bilinear)
    {
        if (destination.Length < points.Length)
            throw new ArgumentException("Destination is shorter than the point list.", nameof(destination));

        for (var i = 0; i < points.Length; i++)
            destination[i] = Sample(source, points[i].X, points[i].Y, resampler);
    }

    /// <summary>
    /// Warps into a new canvas through a caller-supplied mapping. Points that land outside the source become
    /// <paramref name="background"/> rather than clamping, so a rotation does not smear its edge pixels.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="width">Destination width.</param>
    /// <param name="height">Destination height.</param>
    /// <param name="map">Destination → source coordinate mapping.</param>
    /// <param name="resampler">Nearest or bilinear.</param>
    /// <param name="background">Colour outside the source.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="NullReferenceException"><paramref name="map"/> is null.</exception>
    public static PixelBuffer Warp(PixelView source, int width, int height, PixelWarp map,
        Resampler resampler = Resampler.Bilinear, RgbaColor background = default, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(map);

        var stride = width * 4;
        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var row = new float[stride];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                map(x + 0.5f, y + 0.5f, out var sourceX, out var sourceY);

                var offset = x * 4;

                // Coordinates are areal: pixel i covers [i, i+1), its centre sits at i + 0.5. Anything outside
                // that box is background, so a warp does not smear the border pixel across the canvas.
                if (sourceX < 0f || sourceX >= source.Width || sourceY < 0f || sourceY >= source.Height)
                {
                    row[offset] = background.R / 255f;
                    row[offset + 1] = background.G / 255f;
                    row[offset + 2] = background.B / 255f;
                    row[offset + 3] = background.A / 255f;
                    continue;
                }

                var colour = Sample(source, sourceX, sourceY, resampler);
                row[offset] = colour.R / 255f;
                row[offset + 1] = colour.G / 255f;
                row[offset + 2] = colour.B / 255f;
                row[offset + 3] = colour.A / 255f;
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    private static RgbaColor Nearest(PixelView source, float x, float y)
    {
        var sx = Math.Clamp((int)MathF.Floor(x), 0, source.Width - 1);
        var sy = Math.Clamp((int)MathF.Floor(y), 0, source.Height - 1);
        return ReadColor(source, sx, sy);
    }

    private static RgbaColor Bilinear(PixelView source, float x, float y)
    {
        var fx = Math.Clamp(x - 0.5f, 0f, source.Width - 1f);
        var fy = Math.Clamp(y - 0.5f, 0f, source.Height - 1f);

        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var x1 = Math.Min(x0 + 1, source.Width - 1);
        var y1 = Math.Min(y0 + 1, source.Height - 1);
        var tx = fx - x0;
        var ty = fy - y0;

        var c00 = ReadColor(source, x0, y0);
        var c10 = ReadColor(source, x1, y0);
        var c01 = ReadColor(source, x0, y1);
        var c11 = ReadColor(source, x1, y1);

        return new RgbaColor(
            Lerp(Lerp(c00.R, c10.R, tx), Lerp(c01.R, c11.R, tx), ty),
            Lerp(Lerp(c00.G, c10.G, tx), Lerp(c01.G, c11.G, tx), ty),
            Lerp(Lerp(c00.B, c10.B, tx), Lerp(c01.B, c11.B, tx), ty),
            Lerp(Lerp(c00.A, c10.A, tx), Lerp(c01.A, c11.A, tx), ty));
    }

    private static byte Lerp(byte a, byte b, float t)
    {
        var value = a + (b - a) * t;
        var rounded = (int)(value + 0.5f);
        return (byte)Math.Clamp(rounded, 0, 255);
    }

    private static RgbaColor ReadColor(PixelView view, int x, int y)
    {
        var bytesPerPixel = view.Format.BytesPerPixel();
        var span = view.Row(y).Slice(x * bytesPerPixel, bytesPerPixel);

        switch (view.Format)
        {
            case WorkingFormat.Rgba8:
                return new RgbaColor(span[0], span[1], span[2], span[3]);

            case WorkingFormat.Rgba16:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(span);
                return new RgbaColor(
                    (byte)(samples[0] / 257), (byte)(samples[1] / 257),
                    (byte)(samples[2] / 257), (byte)(samples[3] / 257));
            }

            default:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span);
                return new RgbaColor(ToByte(samples[0]), ToByte(samples[1]), ToByte(samples[2]), ToByte(samples[3]));
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
}
