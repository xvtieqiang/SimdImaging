using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// Lossless orientation changes that are not already a plain rotate or flip: the two transposes and the
/// rotate-then-flip combinations. Every one is a single pass — composing Rotate90 and Flip would read and
/// write the buffer twice for no gain.
/// </summary>
public static class Orientation
{
    /// <summary>Swaps the axes: destination (x, y) reads source (y, x). Width and height swap.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="anti">Mirror across the anti-diagonal instead: destination (x, y) reads
    /// <c>(width-1-y, height-1-x)</c>.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static PixelBuffer Transpose(PixelView source, bool anti = false, IPixelPool? pool = null)
    {
        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        var bytesPerPixel = source.Format.BytesPerPixel();
        var destination = PixelBuffer.Rent(sourceHeight, sourceWidth, source.Format, pool);

        for (var y = 0; y < sourceHeight; y++)
        {
            var row = source.Row(y);
            for (var x = 0; x < sourceWidth; x++)
            {
                var destinationX = anti ? sourceHeight - 1 - y : y;
                var destinationY = anti ? sourceWidth - 1 - x : x;

                row.Slice(x * bytesPerPixel, bytesPerPixel)
                    .CopyTo(destination.View.Row(destinationY).Slice(destinationX * bytesPerPixel, bytesPerPixel));
            }
        }

        return destination;
    }

    /// <summary>
    /// Applies the EXIF orientation to the pixels, so the stored raster matches what a viewer shows. The result
    /// is always <see cref="ImageOrientation.Normal"/>: leaving the tag in place would rotate twice.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="orientation">The EXIF / TIFF orientation, 1..8.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static PixelBuffer AutoOrient(PixelView source, ImageOrientation orientation, IPixelPool? pool = null)
    {
        if (orientation == ImageOrientation.Normal)
            return Transforms.Copy(source, pool);

        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        var bytesPerPixel = source.Format.BytesPerPixel();

        // 5..8 swap the axes: the stored image is rotated a quarter turn.
        var swapped = (int)orientation >= 5;
        var width = swapped ? sourceHeight : sourceWidth;
        var height = swapped ? sourceWidth : sourceHeight;
        var destination = PixelBuffer.Rent(width, height, source.Format, pool);

        for (var y = 0; y < height; y++)
        {
            var row = destination.View.Row(y);

            for (var x = 0; x < width; x++)
            {
                int sx, sy;
                switch (orientation)
                {
                    case ImageOrientation.FlipHorizontal:
                        sx = sourceWidth - 1 - x;
                        sy = y;
                        break;
                    case ImageOrientation.Rotate180:
                        sx = sourceWidth - 1 - x;
                        sy = sourceHeight - 1 - y;
                        break;
                    case ImageOrientation.FlipVertical:
                        sx = x;
                        sy = sourceHeight - 1 - y;
                        break;
                    case ImageOrientation.Transpose:
                        sx = y;
                        sy = x;
                        break;
                    case ImageOrientation.Rotate90:
                        sx = y;
                        sy = sourceHeight - 1 - x;
                        break;
                    case ImageOrientation.Transverse:
                        sx = sourceWidth - 1 - y;
                        sy = sourceHeight - 1 - x;
                        break;
                    case ImageOrientation.Rotate270:
                        sx = sourceWidth - 1 - y;
                        sy = x;
                        break;
                    default:
                        sx = x;
                        sy = y;
                        break;
                }

                source.Row(sy).Slice(sx * bytesPerPixel, bytesPerPixel)
                    .CopyTo(row.Slice(x * bytesPerPixel, bytesPerPixel));
            }
        }

        return destination;
    }

    /// <summary>Rotates by a quarter turn and optionally mirrors, in one pass.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="quarterTurns">Clockwise quarter turns, taken modulo 4.</param>
    /// <param name="flipHorizontal">Mirror the result left-to-right.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static PixelBuffer RotateFlip(PixelView source, int quarterTurns, bool flipHorizontal,
        IPixelPool? pool = null)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        var bytesPerPixel = source.Format.BytesPerPixel();

        var swapped = turns % 2 == 1;
        var width = swapped ? sourceHeight : sourceWidth;
        var height = swapped ? sourceWidth : sourceHeight;
        var destination = PixelBuffer.Rent(width, height, source.Format, pool);

        for (var y = 0; y < height; y++)
        {
            var row = destination.View.Row(y);

            for (var x = 0; x < width; x++)
            {
                var sourceX = x;
                if (flipHorizontal)
                    sourceX = width - 1 - sourceX;

                int sx, sy;
                switch (turns)
                {
                    case 0:
                        sx = sourceX;
                        sy = y;
                        break;
                    case 1:
                        sx = y;
                        sy = sourceHeight - 1 - sourceX;
                        break;
                    case 2:
                        sx = sourceWidth - 1 - sourceX;
                        sy = sourceHeight - 1 - y;
                        break;
                    default:
                        sx = sourceWidth - 1 - y;
                        sy = sourceX;
                        break;
                }

                source.Row(sy).Slice(sx * bytesPerPixel, bytesPerPixel)
                    .CopyTo(row.Slice(x * bytesPerPixel, bytesPerPixel));
            }
        }

        return destination;
    }
}
