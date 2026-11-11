using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// General affine resampling with bilinear filtering. The caller supplies the forward transform; the inverse is
/// computed once and each destination pixel maps back to a source position — the only direction that never
/// leaves holes when scaling up.
/// </summary>
public static class Mapping
{
    /// <summary>Transforms <paramref name="source"/> into a <paramref name="width"/> × <paramref name="height"/> canvas.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="forward">Source → destination transform.</param>
    /// <param name="width">Destination width.</param>
    /// <param name="height">Destination height.</param>
    /// <param name="background">Colour written where the inverse mapping lands outside the source.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="forward"/> is singular.</exception>
    public static WorkingBuffer Transform(PixelView source, AffineMatrix forward, int width, int height,
        RgbaColor background, IPixelPool? pool = null)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (!forward.TryInvert(out var inverse))
            throw new ArgumentException("The transform is singular and cannot be inverted.", nameof(forward));

        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);
        var row = new float[width * 4];
        var (br, bg, bb, ba) = (background.R / 255f, background.G / 255f, background.B / 255f, background.A / 255f);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sx = inverse.A * (x + 0.5f) + inverse.B * (y + 0.5f) + inverse.C;
                var sy = inverse.D * (x + 0.5f) + inverse.E * (y + 0.5f) + inverse.F;
                var o = x * 4;

                if (sx < 0f || sy < 0f || sx > source.Width || sy > source.Height)
                {
                    row[o] = br;
                    row[o + 1] = bg;
                    row[o + 2] = bb;
                    row[o + 3] = ba;
                    continue;
                }

                SampleBilinear(source, sx - 0.5f, sy - 0.5f, out var r, out var g, out var b, out var a);
                var inverseAlpha = a > 0f ? 1f / a : 0f;
                row[o] = r * inverseAlpha;
                row[o + 1] = g * inverseAlpha;
                row[o + 2] = b * inverseAlpha;
                row[o + 3] = a;
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>Bilinear sample in pixel-centre coordinates; returns premultiplied colour. Edges replicate.</summary>
    internal static void SampleBilinear(PixelView source, float x, float y, out float r, out float g, out float b, out float a)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var tx = x - x0;
        var ty = y - y0;

        var x1 = Math.Clamp(x0 + 1, 0, source.Width - 1);
        var y1 = Math.Clamp(y0 + 1, 0, source.Height - 1);
        x0 = Math.Clamp(x0, 0, source.Width - 1);
        y0 = Math.Clamp(y0, 0, source.Height - 1);

        ReadPremultiplied(source, x0, y0, out var r00, out var g00, out var b00, out var a00);
        ReadPremultiplied(source, x1, y0, out var r10, out var g10, out var b10, out var a10);
        ReadPremultiplied(source, x0, y1, out var r01, out var g01, out var b01, out var a01);
        ReadPremultiplied(source, x1, y1, out var r11, out var g11, out var b11, out var a11);

        var w00 = (1f - tx) * (1f - ty);
        var w10 = tx * (1f - ty);
        var w01 = (1f - tx) * ty;
        var w11 = tx * ty;

        r = r00 * w00 + r10 * w10 + r01 * w01 + r11 * w11;
        g = g00 * w00 + g10 * w10 + g01 * w01 + g11 * w11;
        b = b00 * w00 + b10 * w10 + b01 * w01 + b11 * w11;
        a = a00 * w00 + a10 * w10 + a01 * w01 + a11 * w11;
    }

    private static void ReadPremultiplied(PixelView source, int x, int y,
        out float r, out float g, out float b, out float a)
    {
        var bytesPerPixel = source.Format.BytesPerPixel();
        var span = source.Row(y).Slice(x * bytesPerPixel, bytesPerPixel);

        switch (source.Format)
        {
            case WorkingFormat.Rgba8:
                a = span[3] * (1f / 255f);
                r = span[0] * (1f / 255f) * a;
                g = span[1] * (1f / 255f) * a;
                b = span[2] * (1f / 255f) * a;
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(span);
                a = samples[3] * (1f / 65535f);
                r = samples[0] * (1f / 65535f) * a;
                g = samples[1] * (1f / 65535f) * a;
                b = samples[2] * (1f / 65535f) * a;
                break;
            }

            default:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span);
                a = samples[3];
                r = samples[0] * a;
                g = samples[1] * a;
                b = samples[2] * a;
                break;
            }
        }
    }

    /// <summary>
    /// Perspective transform: an 8-parameter homography <c>[a b c; d e f; g h 1]</c> mapping source to
    /// destination, inverted internally and sampled bilinearly. Points behind the camera are background.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="forward">Eight row-major parameters of the forward homography.</param>
    /// <param name="width">Destination width.</param>
    /// <param name="height">Destination height.</param>
    /// <param name="background">Colour outside the source.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentException">The homography is not eight values or is singular.</exception>
    public static WorkingBuffer Perspective(PixelView source, ReadOnlySpan<float> forward, int width, int height,
        RgbaColor background, IPixelPool? pool = null)
    {
        if (forward.Length != 8)
            throw new ArgumentException("A homography needs eight parameters.", nameof(forward));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Dimensions must be positive.");

        double a = forward[0], b = forward[1], c = forward[2];
        double d = forward[3], e = forward[4], f = forward[5];
        double g = forward[6], h = forward[7];

        // Adjugate inverse of [a b c; d e f; g h 1].
        var ia = e - f * h;
        var ib = c * h - b;
        var ic = b * f - c * e;
        var id = f * g - d;
        var ie = a - c * g;
        var @if = c * d - a * f;
        var ig = d * h - e * g;
        var ih = b * g - a * h;
        var ii = a * e - b * d;

        var determinant = a * ia + b * id + c * ig;
        if (Math.Abs(determinant) < 1e-12)
            throw new ArgumentException("The homography is singular.", nameof(forward));

        var scale = 1.0 / determinant;
        ia *= scale; ib *= scale; ic *= scale;
        id *= scale; ie *= scale; @if *= scale;
        ig *= scale; ih *= scale; ii *= scale;

        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);
        var row = new float[width * 4];
        var (br, bg, bb, ba) = (background.R / 255f, background.G / 255f, background.B / 255f, background.A / 255f);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double px = x + 0.5;
                double py = y + 0.5;
                var w = ig * px + ih * py + ii;
                var o = x * 4;

                if (w <= 1e-12)
                {
                    row[o] = br;
                    row[o + 1] = bg;
                    row[o + 2] = bb;
                    row[o + 3] = ba;
                    continue;
                }

                var sx = (ia * px + ib * py + ic) / w;
                var sy = (id * px + ie * py + @if) / w;

                if (sx < 0 || sy < 0 || sx > source.Width || sy > source.Height)
                {
                    row[o] = br;
                    row[o + 1] = bg;
                    row[o + 2] = bb;
                    row[o + 3] = ba;
                    continue;
                }

                SampleBilinear(source, (float)sx - 0.5f, (float)sy - 0.5f, out var r, out var gg, out var bl, out var al);
                var inverseAlpha = al > 0f ? 1f / al : 0f;
                row[o] = r * inverseAlpha;
                row[o + 1] = gg * inverseAlpha;
                row[o + 2] = bl * inverseAlpha;
                row[o + 3] = al;
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

}
