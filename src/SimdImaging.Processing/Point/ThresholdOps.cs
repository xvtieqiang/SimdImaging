using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Local-mean adaptive binarisation (Bradley / mean-C): a pixel is white when its luma beats the mean of its
/// neighbourhood scaled by <c>1 - offset</c>. A global threshold cannot separate a page photographed with a
/// shadow; a local mean follows the illumination.
/// <para>
/// The mean comes from a sliding window — column sums plus a prefix sum — so it costs O(1) per pixel and keeps
/// a ring of <c>2r+1</c> luma rows rather than an integral image of the whole picture.
/// </para>
/// </summary>
public static class ThresholdOps
{
    /// <summary>Binarises against a local mean.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="radius">Window radius in pixels.</param>
    /// <param name="offset">How far below the local mean the threshold sits, 0..1; 0.05 keeps faint strokes
    /// while ignoring paper texture.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not positive or the offset is outside
    /// <c>[0, 1)</c>.</exception>
    public static PixelBuffer AdaptiveThreshold(PixelView source, int radius = 8, float offset = 0.05f,
        IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        if (!(offset >= 0f) || offset >= 1f)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be in [0, 1).");

        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var window = radius * 2 + 1;

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var luma = new float[window * width];
        var columnSums = new double[width];
        var prefix = new double[width + 1];
        var row = new float[stride];
        var output = new float[stride];
        var incoming = new float[width];

        void FillLuma(int sourceY, Span<float> target)
        {
            Pixels.ReadRowRaw(source.Row(sourceY), width, source.Format, row);
            for (var x = 0; x < width; x++)
            {
                var offset4 = x * 4;
                target[x] = 0.299f * row[offset4] + 0.587f * row[offset4 + 1] + 0.114f * row[offset4 + 2];
            }
        }

        // Seed the ring with the clamped rows -radius..radius; slot L is (L + radius) % window, so the row
        // leaving at step y always sits exactly where the arriving one lands.
        for (var logical = -radius; logical <= radius; logical++)
        {
            var slot = (logical + radius) % window;
            var values = luma.AsSpan(slot * width, width);
            FillLuma(Math.Clamp(logical, 0, height - 1), values);
            for (var x = 0; x < width; x++)
                columnSums[x] += values[x];
        }

        var norm = 1.0 / (window * window);

        for (var y = 0; y < height; y++)
        {
            if (y > 0)
            {
                var slot = y % window;
                var values = luma.AsSpan(slot * width, width);
                FillLuma(Math.Min(y + radius, height - 1), incoming);

                for (var x = 0; x < width; x++)
                {
                    columnSums[x] += incoming[x] - values[x];
                    values[x] = incoming[x];
                }
            }

            var centre = luma.AsSpan((y + radius) % window * width, width);

            prefix[0] = 0;
            for (var x = 0; x < width; x++)
                prefix[x + 1] = prefix[x] + columnSums[x];

            Pixels.ReadRowRaw(source.Row(y), width, source.Format, output);

            for (var x = 0; x < width; x++)
            {
                var lower = x - radius;
                var upper = x + radius;
                var sum = 0.0;

                if (lower < 0)
                {
                    sum += -lower * columnSums[0];
                    lower = 0;
                }

                if (upper >= width)
                {
                    sum += (upper - width + 1) * columnSums[width - 1];
                    upper = width - 1;
                }

                sum += prefix[upper + 1] - prefix[lower];
                var threshold = sum * norm * (1f - offset);
                var value = centre[x] > threshold ? 1f : 0f;

                var offset4 = x * 4;
                output[offset4] = value;
                output[offset4 + 1] = value;
                output[offset4 + 2] = value;
            }

            Pixels.WriteRowRaw(output, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }
}
