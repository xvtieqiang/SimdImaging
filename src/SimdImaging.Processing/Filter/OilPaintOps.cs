using System.Runtime.CompilerServices;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Oil paint: every pixel becomes the mean colour of the most common intensity in its neighbourhood, which
/// flattens gradients into patches with hard, brush-like boundaries.
/// <para>
/// Counting is integer and the tie-break is "lowest bin wins", so the result does not depend on the order the
/// window is visited in — only on the window itself. The window rows are kept in a ring, so the cost is
/// O(width × height × radius²) with nothing sized by the image.
/// </para>
/// </summary>
public static class OilPaintOps
{
    /// <summary>Applies the oil-paint effect.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="radius">Neighbourhood radius in pixels.</param>
    /// <param name="levels">Intensity bins; fewer bins means broader patches.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not positive or <paramref name="levels"/> is
    /// outside 2..256.</exception>
    public static WorkingBuffer OilPaint(PixelView source, int radius = 2, int levels = 20, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        if (levels is < 2 or > 256)
            throw new ArgumentOutOfRangeException(nameof(levels), levels, "Levels must be 2..256.");

        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var window = radius * 2 + 1;

        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;

        // Bands are independent: a pixel's answer depends only on its own window, and the ring below is a cache,
        // not state that crosses rows. Each worker therefore keeps its own ring, counters and output row, and
        // the arithmetic per pixel is untouched — the split cannot change a byte.
        RowParallel.For(width, height, (start, end) =>
        {
            var counts = new int[levels];
            var sumR = new int[levels];
            var sumG = new int[levels];
            var sumB = new int[levels];

            var rows = new float[window * stride];
            var output = new float[stride];
            var computed = start - 1;

            void EnsureRow(int y)
            {
                for (var current = computed + 1; current <= y; current++)
                {
                    Pixels.ReadRowRaw(source.Row(current), width, source.Format,
                        rows.AsSpan((current % window) * stride, stride));
                }

                computed = Math.Max(computed, y);
            }

            EnsureRow(Math.Min(start + radius, height - 1));

        for (var y = start; y < end; y++)
        {
            EnsureRow(Math.Min(y + radius, height - 1));
            var centre = rows.AsSpan((y % window) * stride, stride);

            // Seed the window at x = 0 with a full count, then slide it: each step removes the one column that
            // leaves (kx = -radius, clamped) and adds the one that enters (kx = +radius, clamped). Counts and
            // sums are integers, so a removal undoes an addition exactly — the tallies are bit-identical to
            // recounting from scratch, which is what made this change safe to make.
            //
            // What it buys: the old loop cleared four level-sized arrays at every pixel (80 int writes at
            // levels = 20) and re-read all (2r+1)² samples; now nothing is cleared and only 2·(2r+1) samples
            // are touched per step.
            Array.Clear(counts);
            Array.Clear(sumR);
            Array.Clear(sumG);
            Array.Clear(sumB);

            for (var ky = -radius; ky <= radius; ky++)
            {
                var sy = Math.Clamp(y + ky, 0, height - 1);
                var windowRow = rows.AsSpan((sy % window) * stride, stride);
                for (var kx = -radius; kx <= radius; kx++)
                    Accumulate(windowRow, Math.Clamp(kx, 0, width - 1) * 4, 1, counts, sumR, sumG, sumB, levels);
            }

            for (var x = 0; x < width; x++)
            {
                if (x > 0)
                {
                    var leaving = Math.Clamp(x - 1 - radius, 0, width - 1) * 4;
                    var entering = Math.Clamp(x + radius, 0, width - 1) * 4;
                    for (var ky = -radius; ky <= radius; ky++)
                    {
                        var sy = Math.Clamp(y + ky, 0, height - 1);
                        var windowRow = rows.AsSpan((sy % window) * stride, stride);
                        Accumulate(windowRow, leaving, -1, counts, sumR, sumG, sumB, levels);
                        Accumulate(windowRow, entering, 1, counts, sumR, sumG, sumB, levels);
                    }
                }

                var best = 0;
                for (var bin = 1; bin < levels; bin++)
                {
                    if (counts[bin] > counts[best])
                        best = bin;
                }

                var count = counts[best];
                var target = x * 4;
                output[target] = sumR[best] / (count * 255f);
                output[target + 1] = sumG[best] / (count * 255f);
                output[target + 2] = sumB[best] / (count * 255f);
                output[target + 3] = centre[target + 3];
            }

            Pixels.WriteRowRaw(output, width, source.Format, view.Row(y));
        }
        });

        return destination;
    }
    /// <summary>
    /// Adds (<paramref name="sign"/> = 1) or removes (-1) one window sample from the tallies. Removal is the
    /// exact inverse of addition because every counter here is an integer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Accumulate(ReadOnlySpan<float> row, int offset, int sign, int[] counts, int[] sumR,
        int[] sumG, int[] sumB, int levels)
    {
        var r = (int)(row[offset] * 255f + 0.5f);
        var g = (int)(row[offset + 1] * 255f + 0.5f);
        var b = (int)(row[offset + 2] * 255f + 0.5f);

        var bin = (r + g + b) / 3 * levels / 256;
        if (bin >= levels)
            bin = levels - 1;

        counts[bin] += sign;
        sumR[bin] += sign * r;
        sumG[bin] += sign * g;
        sumB[bin] += sign * b;
    }

}
