using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Geometry;

/// <summary>Lossless geometry: no resampling, only byte moves. Every method allocates the destination and leaves
/// the source untouched.</summary>
public static class Transforms
{
    /// <summary>Copies a region out, row by row, keeping the working format.</summary>
    /// <exception cref="ArgumentException"><paramref name="region"/> does not lie inside the source.</exception>
    public static PixelBuffer Crop(PixelView source, PixelRect region, IPixelPool? pool = null)
    {
        if (!region.FitsIn(source.Width, source.Height))
            throw new ArgumentException($"The region {region} does not fit in {source.Width}x{source.Height}.", nameof(region));

        var bytesPerPixel = source.Format.BytesPerPixel();
        var rowBytes = region.Width * bytesPerPixel;
        var destination = PixelBuffer.Rent(region.Width, region.Height, source.Format, pool);
        for (var y = 0; y < region.Height; y++)
        {
            source.Row(region.Y + y).Slice(region.X * bytesPerPixel, rowBytes)
                .CopyTo(destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>Copies the whole view.</summary>
    public static PixelBuffer Copy(PixelView source, IPixelPool? pool = null)
    {
        var destination = PixelBuffer.Rent(source.Width, source.Height, source.Format, pool);
        for (var y = 0; y < source.Height; y++)
            source.Row(y).CopyTo(destination.View.Row(y));
        return destination;
    }

    /// <summary>Mirrors left-to-right.</summary>
    public static PixelBuffer FlipHorizontal(PixelView source, IPixelPool? pool = null)
    {
        var bytesPerPixel = source.Format.BytesPerPixel();
        var destination = PixelBuffer.Rent(source.Width, source.Height, source.Format, pool);

        // A 4-byte pixel is one uint: two million Slice/CopyTo calls become two million uint moves.
        if (source.Format == WorkingFormat.Rgba8)
        {
            for (var y = 0; y < source.Height; y++)
            {
                var from = MemoryMarshal.Cast<byte, uint>(source.Row(y));
                var to = MemoryMarshal.Cast<byte, uint>(destination.View.Row(y));
                from.CopyTo(to);
                to.Reverse();
            }

            return destination;
        }

        for (var y = 0; y < source.Height; y++)
        {
            var from = source.Row(y);
            var to = destination.View.Row(y);
            for (var x = 0; x < source.Width; x++)
            {
                var sx = (source.Width - 1 - x) * bytesPerPixel;
                from.Slice(sx, bytesPerPixel).CopyTo(to.Slice(x * bytesPerPixel, bytesPerPixel));
            }
        }

        return destination;
    }

    /// <summary>Mirrors top-to-bottom.</summary>
    public static PixelBuffer FlipVertical(PixelView source, IPixelPool? pool = null)
    {
        var destination = PixelBuffer.Rent(source.Width, source.Height, source.Format, pool);
        var rowBytes = source.Width * source.Format.BytesPerPixel();
        for (var y = 0; y < source.Height; y++)
            source.Row(source.Height - 1 - y).Slice(0, rowBytes).CopyTo(destination.View.Row(y));
        return destination;
    }

    /// <summary>Rotates by a multiple of 90°: a transpose plus a mirror, never a resample.</summary>
    /// <summary>
    /// Copies whole pixels along the rotated diagonal. Casting once per worker and indexing by stride keeps
    /// the inner loop to one load and one store; output rows are disjoint, so the worker count cannot change
    /// a byte.
    /// </summary>
    private static void Transpose<TPixel>(PixelView source, PixelView destination, bool clockwise)
        where TPixel : unmanaged
    {
        var pixelSize = Unsafe.SizeOf<TPixel>();
        RowParallel.For(destination.Width, destination.Height, (start, end) =>
        {
            var all = MemoryMarshal.Cast<byte, TPixel>(source.Span);
            var sourceStride = source.Stride / pixelSize;
            for (var y = start; y < end; y++)
            {
                var to = MemoryMarshal.Cast<byte, TPixel>(destination.Row(y));
                for (var x = 0; x < destination.Width; x++)
                {
                    var sx = clockwise ? y : source.Width - 1 - y;
                    var sy = clockwise ? source.Height - 1 - x : x;
                    to[x] = all[sy * sourceStride + sx];
                }
            }
        });
    }

    public static PixelBuffer Rotate90(PixelView source, bool clockwise, IPixelPool? pool = null)
    {
        var bytesPerPixel = source.Format.BytesPerPixel();
        var destination = PixelBuffer.Rent(source.Height, source.Width, source.Format, pool);

        // One move per pixel, whatever the pixel width is: a rotate is a permutation, so it never needs to
        // look at a channel. Before this, only Rgba8 took the fast path and Rgba16 fell through to an
        // eight-byte Slice+CopyTo per pixel with no parallelism -- measured 4x slower than the oracle at
        // Rgba16 (PerfRgba16), while Rgba8 was 3.9x faster.
        if (bytesPerPixel == 4)
        {
            Transpose<uint>(source, destination.View, clockwise);
            return destination;
        }

        if (bytesPerPixel == 8)
        {
            Transpose<ulong>(source, destination.View, clockwise);
            return destination;
        }

        for (var y = 0; y < destination.Height; y++)
        {
            var to = destination.View.Row(y);
            for (var x = 0; x < destination.Width; x++)
            {
                var sx = clockwise ? y : source.Width - 1 - y;
                var sy = clockwise ? source.Height - 1 - x : x;
                source.Row(sy).Slice(sx * bytesPerPixel, bytesPerPixel)
                    .CopyTo(to.Slice(x * bytesPerPixel, bytesPerPixel));
            }
        }

        return destination;
    }

    /// <summary>Rotates 180°.</summary>
    public static PixelBuffer Rotate180(PixelView source, IPixelPool? pool = null)
    {
        var bytesPerPixel = source.Format.BytesPerPixel();
        var destination = PixelBuffer.Rent(source.Width, source.Height, source.Format, pool);

        if (source.Format == WorkingFormat.Rgba8)
        {
            for (var y = 0; y < source.Height; y++)
            {
                var to = MemoryMarshal.Cast<byte, uint>(destination.View.Row(y));
                var from = MemoryMarshal.Cast<byte, uint>(source.Row(source.Height - 1 - y));
                from.CopyTo(to);
                to.Reverse();
            }

            return destination;
        }

        for (var y = 0; y < source.Height; y++)
        {
            var to = destination.View.Row(y);
            var from = source.Row(source.Height - 1 - y);
            for (var x = 0; x < source.Width; x++)
            {
                from.Slice((source.Width - 1 - x) * bytesPerPixel, bytesPerPixel)
                    .CopyTo(to.Slice(x * bytesPerPixel, bytesPerPixel));
            }
        }

        return destination;
    }

    /// <summary>Grows the canvas and fills the new border with <paramref name="background"/>.</summary>
    public static PixelBuffer Pad(PixelView source, int left, int top, int right, int bottom,
        RgbaColor background, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(left);
        ArgumentOutOfRangeException.ThrowIfNegative(top);
        ArgumentOutOfRangeException.ThrowIfNegative(right);
        ArgumentOutOfRangeException.ThrowIfNegative(bottom);

        var width = checked(source.Width + left + right);
        var height = checked(source.Height + top + bottom);
        var bytesPerPixel = source.Format.BytesPerPixel();
        var destination = PixelBuffer.Rent(width, height, source.Format, pool);

        Span<byte> pixel = stackalloc byte[16];
        WriteColor(background, source.Format, pixel[..bytesPerPixel]);
        for (var y = 0; y < height; y++)
        {
            var row = destination.View.Row(y);
            for (var offset = 0; offset < width * bytesPerPixel; offset += bytesPerPixel)
                pixel[..bytesPerPixel].CopyTo(row.Slice(offset, bytesPerPixel));
        }

        for (var y = 0; y < source.Height; y++)
            source.Row(y).Slice(0, source.Width * bytesPerPixel)
                .CopyTo(destination.View.Row(top + y).Slice(left * bytesPerPixel, source.Width * bytesPerPixel));

        return destination;
    }

    internal static void WriteColor(RgbaColor color, WorkingFormat format, Span<byte> destination)
    {
        switch (format)
        {
            case WorkingFormat.Rgba8:
                destination[0] = color.R;
                destination[1] = color.G;
                destination[2] = color.B;
                destination[3] = color.A;
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(destination);
                samples[0] = (ushort)(color.R * 257);
                samples[1] = (ushort)(color.G * 257);
                samples[2] = (ushort)(color.B * 257);
                samples[3] = (ushort)(color.A * 257);
                break;
            }

            default:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(destination);
                samples[0] = color.R / 255f;
                samples[1] = color.G / 255f;
                samples[2] = color.B / 255f;
                samples[3] = color.A / 255f;
                break;
            }
        }
    }
}
