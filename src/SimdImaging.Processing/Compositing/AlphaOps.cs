using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>Alpha is the part every filter gets wrong: filters must see premultiplied colour, and composition
/// must resolve it back. These are the only two places that conversion happens.</summary>
public static class AlphaOps
{
    /// <summary>Multiplies colour by alpha.</summary>
    public static PixelBuffer Premultiply(PixelView source, IPixelPool? pool = null)
    {
        var destination = PixelBuffer.Rent(source.Width, source.Height, source.Format, pool);
        var row = new float[source.Width * 4];
        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
            for (var i = 0; i < source.Width * 4; i += 4)
            {
                var alpha = row[i + 3];
                row[i] *= alpha;
                row[i + 1] *= alpha;
                row[i + 2] *= alpha;
            }

            Pixels.WriteRowRaw(row, source.Width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>Divides colour by alpha; a zero alpha yields zero colour.</summary>
    public static PixelBuffer Unpremultiply(PixelView source, IPixelPool? pool = null)
    {
        var destination = PixelBuffer.Rent(source.Width, source.Height, source.Format, pool);
        var row = new float[source.Width * 4];
        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
            for (var i = 0; i < source.Width * 4; i += 4)
            {
                var alpha = row[i + 3];
                var inverse = alpha > 0f ? 1f / alpha : 0f;
                row[i] *= inverse;
                row[i + 1] *= inverse;
                row[i + 2] *= inverse;
            }

            Pixels.WriteRowRaw(row, source.Width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>Source-over composition — the common case.</summary>
    public static PixelBuffer Composite(PixelView background, PixelView overlay, int x, int y, IPixelPool? pool = null) =>
        Composite(background, overlay, x, y, PorterDuff.SourceOver, pool);

    /// <summary>
    /// One of the twelve Porter-Duff operators at (<paramref name="x"/>, <paramref name="y"/>). Both inputs are
    /// read as premultiplied floats, the factors are applied per pixel and the result is unpremultiplied on the
    /// way out, so colour and alpha can never disagree.
    /// </summary>
    /// <exception cref="ArgumentException">The overlay does not fit at that position.</exception>
    public static PixelBuffer Composite(PixelView background, PixelView overlay, int x, int y, PorterDuff mode,
        IPixelPool? pool = null)
    {
        if (x < 0 || y < 0 || x + overlay.Width > background.Width || y + overlay.Height > background.Height)
            throw new ArgumentException(
                $"A {overlay.Width}x{overlay.Height} overlay at ({x},{y}) does not fit in {background.Width}x{background.Height}.", nameof(overlay));

        var destination = Transforms.Copy(background, pool);
        var bytesPerPixel = background.Format.BytesPerPixel();
        var overlayRow = new float[overlay.Width * 4];
        var backgroundRow = new float[overlay.Width * 4];

        for (var row = 0; row < overlay.Height; row++)
        {
            Pixels.ReadRowPremultiplied(overlay.Row(row), overlay.Width, overlay.Format, overlayRow);
            var destinationBytes = destination.View.Row(y + row).Slice(x * bytesPerPixel, overlay.Width * bytesPerPixel);
            Pixels.ReadRowPremultiplied(destinationBytes, overlay.Width, background.Format, backgroundRow);

            for (var i = 0; i < overlay.Width * 4; i += 4)
            {
                var sourceAlpha = overlayRow[i + 3];
                var destinationAlpha = backgroundRow[i + 3];
                var (sourceFactor, destinationFactor) = Factors(mode, sourceAlpha, destinationAlpha);

                backgroundRow[i] = overlayRow[i] * sourceFactor + backgroundRow[i] * destinationFactor;
                backgroundRow[i + 1] = overlayRow[i + 1] * sourceFactor + backgroundRow[i + 1] * destinationFactor;
                backgroundRow[i + 2] = overlayRow[i + 2] * sourceFactor + backgroundRow[i + 2] * destinationFactor;
                backgroundRow[i + 3] = sourceAlpha * sourceFactor + destinationAlpha * destinationFactor;
            }

            Pixels.WriteRowUnpremultiplied(backgroundRow, overlay.Width, background.Format, destinationBytes);
        }

        return destination;
    }

    private static (float Source, float Destination) Factors(PorterDuff mode, float sa, float da) => mode switch
    {
        PorterDuff.Clear => (0f, 0f),
        PorterDuff.Source => (1f, 0f),
        PorterDuff.Destination => (0f, 1f),
        PorterDuff.SourceOver => (1f, 1f - sa),
        PorterDuff.DestinationOver => (1f - da, 1f),
        PorterDuff.SourceIn => (da, 0f),
        PorterDuff.DestinationIn => (0f, sa),
        PorterDuff.SourceOut => (1f - da, 0f),
        PorterDuff.DestinationOut => (0f, 1f - sa),
        PorterDuff.SourceAtop => (da, 1f - sa),
        PorterDuff.DestinationAtop => (1f - da, sa),
        PorterDuff.Xor => (1f - da, 1f - sa),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown Porter-Duff operator."),
    };
}
