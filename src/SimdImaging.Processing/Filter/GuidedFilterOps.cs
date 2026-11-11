using System.Runtime.Intrinsics;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Guided filter with the image as its own guide: inside a window the output is modelled as
/// <c>a·I + b</c>, where <c>a</c> falls toward zero where the local variance is small (flat areas get
/// averaged) and toward one at edges (they are kept). The coefficients are themselves box-averaged, which is
/// what removes the blockiness a per-window fit would leave.
/// <para>
/// Each channel is filtered independently, so hue survives but the coefficients are not shared between
/// channels. Rows are processed in bands: a band needs two box radii of context on each side, so the working
/// set is five planes of <c>bandRows + 4·radius</c> rows instead of five full images — 4K drops from about
/// 160 MB to about 11 MB.
/// </para>
/// </summary>
public static class GuidedFilterOps
{
    /// <summary>Edge-preserving smoothing.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="radius">Box radius in pixels.</param>
    /// <param name="epsilon">Variance regulariser in normalised units squared: larger smooths more, smaller
    /// keeps more edges. 1e-4 is roughly a 2.5/255 intensity difference.</param>
    /// <param name="bandRows">Rows per band; the band plus two box radii on each side is held in memory.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The radius or band size is not positive, or the epsilon is
    /// not finite and positive.</exception>
    public static WorkingBuffer GuidedFilter(PixelView source, int radius = 4, float epsilon = 1e-4f,
        int bandRows = 128, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bandRows);
        if (!(epsilon > 0f) || float.IsInfinity(epsilon))
            throw new ArgumentOutOfRangeException(nameof(epsilon), epsilon, "Epsilon must be finite and positive.");

        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;

        // Copied up front so each channel pass can read-modify-write its own lane without clobbering the two
        // it has already written.
        var destination = Transforms.Copy(source, pool);

        // Two stages, each reaching `radius` rows: a band carrying 2·radius of context on both sides
        // reproduces the full-image result exactly once the outer rows are discarded.
        var context = radius * 2;
        var capacity = Math.Min(height, bandRows + context * 2) * width;

        var values = new float[capacity];
        var moments = new float[capacity];
        var second = new float[capacity];
        var meanA = new float[capacity];
        var meanB = new float[capacity];
        var columnSums = new float[width];
        var prefix = new float[width + 1];
        var row = new float[stride];

        for (var bandTop = 0; bandTop < height; bandTop += bandRows)
        {
            var bandBottom = Math.Min(height, bandTop + bandRows);
            var top = Math.Max(0, bandTop - context);
            var bottom = Math.Min(height, bandBottom + context);
            var rows = bottom - top;
            var length = rows * width;

            for (var channel = 0; channel < 3; channel++)
            {
                for (var i = 0; i < rows; i++)
                {
                    Pixels.ReadRowRaw(source.Row(top + i), width, source.Format, row);
                    var plane = values.AsSpan(i * width, width);
                    for (var x = 0; x < width; x++)
                        plane[x] = row[x * 4 + channel];
                }

                var input = values.AsSpan(0, length);
                var meanPlane = moments.AsSpan(0, length);
                var secondPlane = second.AsSpan(0, length);
                var aPlane = meanA.AsSpan(0, length);
                var bPlane = meanB.AsSpan(0, length);

                BoxMean(input, meanPlane, width, rows, radius, columnSums, prefix);

                for (var i = 0; i < length; i++)
                    secondPlane[i] = input[i] * input[i];

                BoxMean(secondPlane, aPlane, width, rows, radius, columnSums, prefix);

                // a = var / (var + eps), b = mean - a * mean. Written back over the moments so the second box
                // pass can start immediately.
                for (var i = 0; i < length; i++)
                {
                    var mean = meanPlane[i];
                    var variance = MathF.Max(0f, aPlane[i] - mean * mean);
                    var a = variance / (variance + epsilon);
                    meanPlane[i] = a;
                    secondPlane[i] = mean * (1f - a);
                }

                BoxMean(meanPlane, aPlane, width, rows, radius, columnSums, prefix);
                BoxMean(secondPlane, bPlane, width, rows, radius, columnSums, prefix);

                for (var y = bandTop; y < bandBottom; y++)
                {
                    var baseIndex = (y - top) * width;
                    Pixels.ReadRowRaw(destination.View.Row(y), width, source.Format, row);

                    for (var x = 0; x < width; x++)
                    {
                        var index = baseIndex + x;
                        row[x * 4 + channel] = Math.Clamp(aPlane[index] * input[index] + bPlane[index], 0f, 1f);
                    }

                    Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
                }
            }
        }

        return destination;
    }

    /// <summary>
    /// Box mean with replicated edges, in two running sums: a column sum over the vertical window, then a
    /// prefix sum over the columns so the horizontal window is one subtraction. Cost is O(1) per pixel and the
    /// scratch is two arrays of width, not a plane.
    /// <para>
    /// The prefix sum is serial by nature; the window subtraction is not, so the interior of every row is done
    /// eight pixels at a time. Only the single edge pixel is left scalar, and <c>float</c> is enough: the
    /// prefix of a 4K row reaches about 35 000, where a float still resolves 0.004 — four hundredths of a code
    /// value once divided by the window area.
    /// </para>
    /// </summary>
    private static void BoxMean(ReadOnlySpan<float> input, Span<float> output, int width, int height, int radius,
        float[] columnSums, float[] prefix)
    {
        var window = radius * 2 + 1;
        var norm = 1f / (window * window);
        Array.Clear(columnSums);

        for (var y = -radius; y <= radius; y++)
        {
            var row = input.Slice(Math.Clamp(y, 0, height - 1) * width, width);
            for (var x = 0; x < width; x++)
                columnSums[x] += row[x];
        }

        var interior = width > radius * 2 + 1;

        for (var y = 0; y < height; y++)
        {
            if (y > 0)
            {
                var add = input.Slice(Math.Clamp(y + radius, 0, height - 1) * width, width);
                var remove = input.Slice(Math.Clamp(y - radius - 1, 0, height - 1) * width, width);
                for (var x = 0; x < width; x++)
                    columnSums[x] += add[x] - remove[x];
            }

            var running = 0f;
            prefix[0] = 0f;
            for (var x = 0; x < width; x++)
            {
                running += columnSums[x];
                prefix[x + 1] = running;
            }

            var outputRow = output.Slice(y * width, width);

            if (!interior)
            {
                for (var x = 0; x < width; x++)
                {
                    var lower = x - radius;
                    var upper = x + radius;
                    var sum = 0f;

                    // Replicated edges: the pixels off the left and right repeat the border column.
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
                    outputRow[x] = sum * norm;
                }

                continue;
            }

            for (var x = 0; x < radius; x++)
            {
                var upper = Math.Min(x + radius, width - 1);
                var sum = (radius - x) * columnSums[0] + prefix[upper + 1];
                outputRow[x] = sum * norm;
            }

            var start = radius;
            var end = width - radius;
            var i = start;

            if (Vector256.IsHardwareAccelerated)
            {
                var scale = Vector256.Create(norm);
                for (; i + 8 <= end; i += 8)
                {
                    var high = Vector256.LoadUnsafe(ref prefix[i + radius + 1]);
                    var low = Vector256.LoadUnsafe(ref prefix[i - radius]);
                    ((high - low) * scale).StoreUnsafe(ref outputRow[i]);
                }
            }

            for (; i < end; i++)
                outputRow[i] = (prefix[i + radius + 1] - prefix[i - radius]) * norm;

            for (var x = end; x < width; x++)
            {
                var lower = x - radius;
                var sum = prefix[width] - prefix[lower] + (x + radius - (width - 1)) * columnSums[width - 1];
                outputRow[x] = sum * norm;
            }
        }
    }
}
