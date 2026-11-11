using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Geometry;

/// <summary>How a resize reconciles the source aspect ratio with the requested canvas.</summary>
public enum ResizeMode : byte
{
    /// <summary>Scale to exactly the requested size, ignoring the aspect ratio.</summary>
    Stretch,

    /// <summary>Scale to fit inside the canvas, fill the rest with the background. Output is the requested size.</summary>
    BoxPad,

    /// <summary>Scale to cover the canvas, crop the overflow. Output is the requested size.</summary>
    CropPad,

    /// <summary>Scale to fit, and shrink the output to the scaled size — no padding.</summary>
    Max,

    /// <summary>Scale to cover, and grow the output to the scaled size — no cropping.</summary>
    Min,
}

/// <summary>Where the content sits when the canvas and the content are different sizes.</summary>
public enum Anchor : byte
{
    /// <summary>Top-left corner.</summary>
    TopLeft,

    /// <summary>Top edge, centred.</summary>
    Top,

    /// <summary>Top-right corner.</summary>
    TopRight,

    /// <summary>Left edge, centred.</summary>
    Left,

    /// <summary>Centre of both axes.</summary>
    Centre,

    /// <summary>Right edge, centred.</summary>
    Right,

    /// <summary>Bottom-left corner.</summary>
    BottomLeft,

    /// <summary>Bottom edge, centred.</summary>
    Bottom,

    /// <summary>Bottom-right corner.</summary>
    BottomRight,
}

/// <summary>
/// Canvas fitting: resizing without distorting, padding or cropping around an anchor, tiling, and rounded
/// corners. All of these end up as a placement of one rectangle inside another, so they share one copy path.
/// </summary>
public static class Canvas
{
    /// <summary>Resizes into a canvas the caller describes, honouring the aspect ratio if the mode says so.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="width">Requested canvas width; for <see cref="ResizeMode.Max"/> and
    /// <see cref="ResizeMode.Min"/> a bound rather than the output width.</param>
    /// <param name="height">Requested canvas height.</param>
    /// <param name="resampler">Sampling filter.</param>
    /// <param name="mode">How to reconcile the aspect ratio.</param>
    /// <param name="anchor">Where the content sits in the canvas.</param>
    /// <param name="background">Colour for the padding.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public static WorkingBuffer Resize(PixelView source, int width, int height, Resampler resampler = Resampler.Bicubic,
        ResizeMode mode = ResizeMode.BoxPad, Anchor anchor = Anchor.Centre, RgbaColor background = default,
        IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (mode == ResizeMode.Stretch)
            return Resample.Resize(source, width, height, resampler, pool);

        var scale = mode is ResizeMode.Max or ResizeMode.BoxPad
            ? MathF.Min(width / (float)source.Width, height / (float)source.Height)
            : MathF.Max(width / (float)source.Width, height / (float)source.Height);

        var scaledWidth = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var scaledHeight = Math.Max(1, (int)MathF.Round(source.Height * scale));

        using var scaled = Resample.Resize(source, scaledWidth, scaledHeight, resampler, null);
        if (mode is ResizeMode.Max or ResizeMode.Min)
        {
            // The scaled size is the answer; hand back a copy so the caller owns the buffer the same way it
            // owns every other result.
            return Transforms.Copy(scaled.View, pool);
        }

        var offsetX = Offset(anchor, horizontal: true, width, scaledWidth);
        var offsetY = Offset(anchor, horizontal: false, height, scaledHeight);
        return Place(scaled.View, width, height, offsetX, offsetY, background, pool);
    }

    /// <summary>Changes the canvas without scaling the content: pad when it grows, crop when it shrinks.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="width">New canvas width.</param>
    /// <param name="height">New canvas height.</param>
    /// <param name="anchor">Which part of the source survives a crop, and where it sits in a pad.</param>
    /// <param name="background">Colour for the padding.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public static WorkingBuffer ResizeCanvas(PixelView source, int width, int height, Anchor anchor = Anchor.Centre,
        RgbaColor background = default, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        return Place(source, width, height,
            Offset(anchor, horizontal: true, width, source.Width),
            Offset(anchor, horizontal: false, height, source.Height),
            background, pool);
    }

    /// <summary>Repeats the source to fill a canvas, optionally mirroring alternate tiles so the seams vanish.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="width">Canvas width.</param>
    /// <param name="height">Canvas height.</param>
    /// <param name="mirror">Flip every other tile horizontally and vertically.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public static WorkingBuffer Tile(PixelView source, int width, int height, bool mirror = false,
        IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var bytesPerPixel = source.Format.BytesPerPixel();
        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);

        for (var y = 0; y < height; y++)
        {
            var row = y % source.Height;
            if (mirror && (y / source.Height) % 2 == 1)
                row = source.Height - 1 - row;

            var sourceRow = source.Row(row);
            var destinationRow = destination.View.Row(y);

            for (var x = 0; x < width; x++)
            {
                var column = x % source.Width;
                if (mirror && (x / source.Width) % 2 == 1)
                    column = source.Width - 1 - column;

                sourceRow.Slice(column * bytesPerPixel, bytesPerPixel)
                    .CopyTo(destinationRow.Slice(x * bytesPerPixel, bytesPerPixel));
            }
        }

        return destination;
    }

    /// <summary>
    /// Removes a uniform border matching the top-left pixel, within <paramref name="tolerance"/>. A fully
    /// uniform image trims to a single pixel rather than to nothing — the operators never return an empty
    /// buffer.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="tolerance">Largest per-channel difference still considered "the same", 0..1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is negative.</exception>
    public static WorkingBuffer Trim(PixelView source, float tolerance = 0f, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);

        var width = source.Width;
        var height = source.Height;
        var row = new float[width * 4];

        // The reference goes through the same conversion as every other row: x / 255 and x * (1 / 255)
        // differ in the last bit, and a 1-ulp difference would make a uniform image look non-uniform.
        Internal.Pixels.ReadRowRaw(source.Row(0), width, source.Format, row);
        var reference = row.AsSpan(0, 4).ToArray();

        var top = 0;
        while (top < height && !RowDiffers(source, top, reference, tolerance, row))
            top++;

        if (top == height)
            return Transforms.Crop(source, new PixelRect(0, 0, 1, 1), pool);

        var bottom = height - 1;
        while (bottom > top && !RowDiffers(source, bottom, reference, tolerance, row))
            bottom--;

        var left = 0;
        while (left < width && !ColumnDiffers(source, left, top, bottom, reference, tolerance))
            left++;

        var right = width - 1;
        while (right > left && !ColumnDiffers(source, right, top, bottom, reference, tolerance))
            right--;

        return Transforms.Crop(source, new PixelRect(left, top, right - left + 1, bottom - top + 1), pool);
    }

    /// <summary>Rounds the corners by multiplying alpha with an antialiased rounded-rectangle coverage mask.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="radius">Corner radius in pixels; at half the short side the result is a circle.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is not positive.</exception>
    public static WorkingBuffer RoundCorners(PixelView source, float radius, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);

        var destination = Transforms.Copy(source, pool);
        var builder = new PixelPathBuilder();
        builder.AddRoundedRectangle(new PixelRect(0, 0, source.Width, source.Height), radius);
        PathOps.Clip(destination.View, builder.Build(), PathFillRule.NonZero);
        return destination;
    }

    /// <summary>
    /// Composites the source into a new canvas at the given offset. The offset may be negative: that is what a
    /// crop is, and keeping one code path means pad and crop cannot disagree about rounding.
    /// </summary>
    private static WorkingBuffer Place(PixelView source, int width, int height, int offsetX, int offsetY,
        RgbaColor background, IPixelPool? pool)
    {
        var bytesPerPixel = source.Format.BytesPerPixel();
        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);

        Span<byte> pixel = stackalloc byte[16];
        Transforms.WriteColor(background, source.Format, pixel[..bytesPerPixel]);
        for (var y = 0; y < height; y++)
        {
            var row = destination.View.Row(y);
            for (var offset = 0; offset < width * bytesPerPixel; offset += bytesPerPixel)
                pixel[..bytesPerPixel].CopyTo(row.Slice(offset, bytesPerPixel));
        }

        var sourceX = Math.Max(0, -offsetX);
        var sourceY = Math.Max(0, -offsetY);
        var destinationX = Math.Max(0, offsetX);
        var destinationY = Math.Max(0, offsetY);
        var copyWidth = Math.Min(source.Width - sourceX, width - destinationX);
        var copyHeight = Math.Min(source.Height - sourceY, height - destinationY);

        if (copyWidth <= 0 || copyHeight <= 0)
            return destination;

        for (var y = 0; y < copyHeight; y++)
        {
            source.Row(sourceY + y).Slice(sourceX * bytesPerPixel, copyWidth * bytesPerPixel)
                .CopyTo(destination.View.Row(destinationY + y).Slice(destinationX * bytesPerPixel, copyWidth * bytesPerPixel));
        }

        return destination;
    }

    private static int Offset(Anchor anchor, bool horizontal, int canvas, int content)
    {
        var span = canvas - content;
        return anchor switch
        {
            Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => 0,
            Anchor.TopRight or Anchor.Right or Anchor.BottomRight => span,
            _ => span / 2,
        };
    }

    private static bool RowDiffers(PixelView source, int y, ReadOnlySpan<float> reference, float tolerance,
        float[] row)
    {
        Internal.Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
        for (var x = 0; x < source.Width; x++)
        {
            var offset = x * 4;
            if (Differs(row, offset, reference, tolerance))
                return true;
        }

        return false;
    }

    private static bool ColumnDiffers(PixelView source, int x, int top, int bottom, ReadOnlySpan<float> reference,
        float tolerance)
    {
        Span<float> pixel = stackalloc float[4];
        var bytesPerPixel = source.Format.BytesPerPixel();
        for (var y = top; y <= bottom; y++)
        {
            Internal.Pixels.ReadRowRaw(source.Row(y).Slice(x * bytesPerPixel, bytesPerPixel), 1, source.Format, pixel);
            if (Differs(pixel, 0, reference, tolerance))
                return true;
        }

        return false;
    }

    private static bool Differs(ReadOnlySpan<float> row, int offset, ReadOnlySpan<float> reference, float tolerance)
    {
        return MathF.Abs(row[offset] - reference[0]) > tolerance
               || MathF.Abs(row[offset + 1] - reference[1]) > tolerance
               || MathF.Abs(row[offset + 2] - reference[2]) > tolerance
               || MathF.Abs(row[offset + 3] - reference[3]) > tolerance;
    }
}
