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

            // FloatScratch, not `new`: the audit caught these two per worker per call (4K r=2 is 307 KB of
            // rows against 16 workers, every call). Every region read below is written first — `rows` by
            // EnsureRow up to `computed`, `output` completely — which is what makes reuse safe.
            var rows = FloatScratch.Rent(window * stride);
            var output = FloatScratch.Rent(stride);

            // Per source row: the bin and the packed R/G/B, computed once. The slide below touches every
            // sample twice (once leaving, once entering), so without this each sample had its three float
            // conversions and the bin formula evaluated twice. Packing is exact: 0xRRGGBB < 2^24, which a
            // float holds without loss.
            var packed = FloatScratch.Rent(window * width);
            var bins = new byte[window * width];
            var computed = start - 1;

            // Ring bases for the rows the window touches, hoisted out of the pixel loop: clamping the row
            // index, `% window` (a division by a runtime value) and the span construction were being
            // repeated for every pixel and every tap, and none of them depend on the pixel.
            var windowBases = new int[window];

            try
            {

            void EnsureRow(int y)
            {
                for (var current = computed + 1; current <= y; current++)
                {
                    var slot = (current % window) * stride;
                    Pixels.ReadRowRaw(source.Row(current), width, source.Format,
                        rows.AsSpan(slot, stride));

                    var packedBase = (current % window) * width;
                    for (var x = 0; x < width; x++)
                    {
                        var o = slot + (x * 4);
                        var r = (int)(rows[o] * 255f + 0.5f);
                        var g = (int)(rows[o + 1] * 255f + 0.5f);
                        var b = (int)(rows[o + 2] * 255f + 0.5f);
                        packed[packedBase + x] = (r << 16) | (g << 8) | b;

                        var bin = ((r + g + b) / 3 * levels) / 256;
                        bins[packedBase + x] = (byte)(bin >= levels ? levels - 1 : bin);
                    }
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

            for (var k = 0; k < window; k++)
                windowBases[k] = (Math.Clamp(y + k - radius, 0, height - 1) % window) * width;

            for (var k = 0; k < window; k++)
            {
                var packedBase = windowBases[k];
                for (var kx = -radius; kx <= radius; kx++)
                    Accumulate(packed, bins, packedBase + Math.Clamp(kx, 0, width - 1), 1,
                        counts, sumR, sumG, sumB);
            }

            for (var x = 0; x < width; x++)
            {
                if (x > 0)
                {
                    var leaving = Math.Clamp(x - 1 - radius, 0, width - 1);
                    var entering = Math.Clamp(x + radius, 0, width - 1);
                    for (var k = 0; k < window; k++)
                    {
                        var packedBase = windowBases[k];
                        Accumulate(packed, bins, packedBase + leaving, -1, counts, sumR, sumG, sumB);
                        Accumulate(packed, bins, packedBase + entering, 1, counts, sumR, sumG, sumB);
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
            }
            finally
            {
                FloatScratch.Return(rows);
                FloatScratch.Return(output);
            }
        });

        return destination;
    }
    /// <summary>
    /// Adds (<paramref name="sign"/> = 1) or removes (-1) one window sample from the tallies. Removal is the
    /// exact inverse of addition because every counter here is an integer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Accumulate(float[] packed, byte[] bins, int index, int sign, int[] counts, int[] sumR,
        int[] sumG, int[] sumB)
    {
        var rgb = (int)packed[index];
        var bin = bins[index];
        counts[bin] += sign;
        sumR[bin] += sign * ((rgb >> 16) & 0xFF);
        sumG[bin] += sign * ((rgb >> 8) & 0xFF);
        sumB[bin] += sign * (rgb & 0xFF);
    }

}
