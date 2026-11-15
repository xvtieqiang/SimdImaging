using System.Runtime.InteropServices;
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
/// <para>
/// The two-argument overload takes a separate guide: the coefficients come from <c>cov(I, G)</c> and
/// <c>var(G)</c> instead of the source's own variance, so a depth map, a mask or a denoised copy can steer the
/// filter without contributing its own colours. That path holds eight planes of band rows (about 17 MB at 4K
/// with the default band). It is kept as its own kernel rather than folded into the self-guided one: the
/// self-guided output is byte-stable and a shared loop would put those bytes at risk for no gain.
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
        Validate(radius, bandRows, epsilon);

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

    /// <summary>Cross-guided filter: <paramref name="guide"/> decides where the edges are,
    /// <paramref name="source"/> supplies the colours.
    /// <para>
    /// The size check is also the fusion guard: inside a banded plan step the view is a band, so a full-size
    /// guide does not match and throws instead of filtering against a misaligned guide.
    /// </para>
    /// </summary>
    /// <param name="source">The pixels to filter.</param>
    /// <param name="guide">Same width and height as <paramref name="source"/>. Channel <c>c</c> of the guide
    /// filters channel <c>c</c>, so a grey guide acts as one scalar guide for all three; its alpha is ignored.</param>
    /// <param name="radius">Box radius in pixels.</param>
    /// <param name="epsilon">Variance regulariser in normalised units squared.</param>
    /// <param name="bandRows">Rows per band.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentException"><paramref name="guide"/> is not the same size as
    /// <paramref name="source"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of range.</exception>
    public static WorkingBuffer GuidedFilter(PixelView source, PixelView guide, int radius = 4,
        float epsilon = 1e-4f, int bandRows = 128, IPixelPool? pool = null)
    {
        if (guide.Width != source.Width || guide.Height != source.Height)
            throw new ArgumentException(
                $"The guide is {guide.Width}x{guide.Height} but the source is {source.Width}x{source.Height}.",
                nameof(guide));

        // The guide is the source: same operator, so take the frozen self-guided path and its bytes.
        if (guide.Span.Overlaps(source.Span))
            return GuidedFilter(source, radius, epsilon, bandRows, pool);

        Validate(radius, bandRows, epsilon);
        return CrossGuided(source, guide, radius, epsilon, bandRows, pool);
    }

    private static void Validate(int radius, int bandRows, float epsilon)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bandRows);
        if (!(epsilon > 0f) || float.IsInfinity(epsilon))
            throw new ArgumentOutOfRangeException(nameof(epsilon), epsilon, "Epsilon must be finite and positive.");
    }

    /// <summary>
    /// The cross-guided kernel: <c>a = cov(I, G) / (var(G) + eps)</c>, <c>b = mean(I) − a·mean(G)</c>, both
    /// box-averaged. Separate from the self-guided loop so that loop keeps its bytes; only <see cref="BoxMean"/>
    /// is shared.
    /// </summary>
    private static WorkingBuffer CrossGuided(PixelView source, PixelView guide, int radius, float epsilon,
        int bandRows, IPixelPool? pool)
    {
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;

        var destination = Transforms.Copy(source, pool);

        var context = radius * 2;
        var capacity = Math.Min(height, bandRows + context * 2) * width;

        // Three planes per side, decoded once per band: the channel loop below used to re-read and re-decode
        // both RGBA rows for each of the three channels, doing 3x the conversions for the same bytes.
        var values = new float[3][];
        var guideValues = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            values[c] = new float[capacity];
            guideValues[c] = new float[capacity];
        }

        var meanInput = new float[capacity];
        var meanGuide = new float[capacity];
        var secondGuide = new float[capacity];
        var cross = new float[capacity];
        var meanA = new float[capacity];
        var meanB = new float[capacity];
        var columnSums = new float[width];
        var prefix = new float[width + 1];
        var row = new float[stride];
        var guideRow = new float[stride];
        var ring = new float[(radius * 2 + 1) * width];

        for (var bandTop = 0; bandTop < height; bandTop += bandRows)
        {
            var bandBottom = Math.Min(height, bandTop + bandRows);
            var top = Math.Max(0, bandTop - context);
            var bottom = Math.Min(height, bandBottom + context);
            var rows = bottom - top;
            var length = rows * width;

            for (var i = 0; i < rows; i++)
            {
                Pixels.ReadRowRaw(source.Row(top + i), width, source.Format, row);
                Pixels.ReadRowRaw(guide.Row(top + i), width, guide.Format, guideRow);

                var at = i * width;
                var red = values[0].AsSpan(at, width);
                var green = values[1].AsSpan(at, width);
                var blue = values[2].AsSpan(at, width);
                var guideRed = guideValues[0].AsSpan(at, width);
                var guideGreen = guideValues[1].AsSpan(at, width);
                var guideBlue = guideValues[2].AsSpan(at, width);

                for (var x = 0; x < width; x++)
                {
                    var o = x * 4;
                    red[x] = row[o];
                    green[x] = row[o + 1];
                    blue[x] = row[o + 2];
                    guideRed[x] = guideRow[o];
                    guideGreen[x] = guideRow[o + 1];
                    guideBlue[x] = guideRow[o + 2];
                }
            }

            for (var channel = 0; channel < 3; channel++)
            {
                var input = values[channel].AsSpan(0, length);
                var guidePlane = guideValues[channel].AsSpan(0, length);
                var meanI = meanInput.AsSpan(0, length);
                var meanG = meanGuide.AsSpan(0, length);
                var scratch = secondGuide.AsSpan(0, length);
                var crossPlane = cross.AsSpan(0, length);
                var aPlane = meanA.AsSpan(0, length);
                var bPlane = meanB.AsSpan(0, length);

                BoxMean(input, meanI, width, rows, radius, columnSums, prefix, ring);
                BoxMean(guidePlane, meanG, width, rows, radius, columnSums, prefix, ring);

                Square(guideValues[channel], secondGuide, length);

                BoxMean(scratch, aPlane, width, rows, radius, columnSums, prefix, ring);

                Multiply(values[channel], guideValues[channel], cross, length);

                BoxMean(crossPlane, bPlane, width, rows, radius, columnSums, prefix, ring);

                // a = cov / (var + eps), b = mean(I) - a * mean(G). Two of the planes are free after being
                // read once: the product plane takes a, the mean-of-products plane takes b.
                // E[G^2] landed in meanA, E[I*G] in meanB; the solve writes b back over meanB and a into cross.
                SolveCoefficients(meanInput, meanGuide, meanA, meanB, meanB, cross, epsilon, length);

                BoxMean(crossPlane, aPlane, width, rows, radius, columnSums, prefix, ring);

                // scratch held the guide's second moment; it is free now and becomes the box-averaged b.
                BoxMean(bPlane, scratch, width, rows, radius, columnSums, prefix, ring);

                for (var y = bandTop; y < bandBottom; y++)
                {
                    var baseIndex = (y - top) * width;
                    Pixels.ReadRowRaw(destination.View.Row(y), width, source.Format, row);

                    for (var x = 0; x < width; x++)
                    {
                        var index = baseIndex + x;
                        row[x * 4 + channel] = Math.Clamp(aPlane[index] * input[index] + scratch[index], 0f, 1f);
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
    /// <summary>Elementwise square of a plane; lane i does what the scalar loop did, in the same order.</summary>
    private static void Square(float[] source, float[] destination, int length)
    {
        var i = 0;
        if (Vectorized)
        {
            for (; i + 8 <= length; i += 8)
            {
                var value = Vector256.LoadUnsafe(ref source[i]);
                (value * value).StoreUnsafe(ref destination[i]);
            }
        }

        for (; i < length; i++)
            destination[i] = source[i] * source[i];
    }

    /// <summary>Elementwise product of two planes.</summary>
    private static void Multiply(float[] a, float[] b, float[] destination, int length)
    {
        var i = 0;
        if (Vectorized)
        {
            for (; i + 8 <= length; i += 8)
            {
                var left = Vector256.LoadUnsafe(ref a[i]);
                var right = Vector256.LoadUnsafe(ref b[i]);
                (left * right).StoreUnsafe(ref destination[i]);
            }
        }

        for (; i < length; i++)
            destination[i] = a[i] * b[i];
    }

    /// <summary>
    /// The per-pixel coefficient solve: <c>a = cov / (var + eps)</c>, <c>b = mean(I) − a·mean(G)</c>.
    /// </summary>
    /// <remarks>
    /// Division rather than a reciprocal multiply, per lane: <c>Vector256.Divide</c> is the same IEEE
    /// correctly-rounded operation the scalar body performs, and multiplying by a precomputed reciprocal would
    /// differ in the last bit and eventually in a byte.
    /// </remarks>
    private static void SolveCoefficients(float[] meanInput, float[] meanGuide, float[] secondMoment,
        float[] covariance, float[] bPlane, float[] aPlane, float epsilon, int length)
    {
        var i = 0;
        if (Vectorized)
        {
            var none = Vector256<float>.Zero;
            var eps = Vector256.Create(epsilon);
            for (; i + 8 <= length; i += 8)
            {
                var meanOfGuide = Vector256.LoadUnsafe(ref meanGuide[i]);
                var variance = Vector256.Max(none,
                    Vector256.LoadUnsafe(ref secondMoment[i]) - (meanOfGuide * meanOfGuide));
                var meanOfInput = Vector256.LoadUnsafe(ref meanInput[i]);
                var a = (Vector256.LoadUnsafe(ref covariance[i]) - (meanOfInput * meanOfGuide)) / (variance + eps);
                (meanOfInput - (a * meanOfGuide)).StoreUnsafe(ref bPlane[i]);
                a.StoreUnsafe(ref aPlane[i]);
            }
        }

        for (; i < length; i++)
        {
            var meanOfGuide = meanGuide[i];
            var variance = MathF.Max(0f, secondMoment[i] - (meanOfGuide * meanOfGuide));
            var a = (covariance[i] - (meanInput[i] * meanOfGuide)) / (variance + epsilon);
            bPlane[i] = meanInput[i] - (a * meanOfGuide);
            aPlane[i] = a;
        }
    }

    /// <summary>Test hook: forces the scalar twin so a test can compare the two paths on the same pixels.</summary>
    internal static bool ForceScalarVector;

    /// <summary>Eight float lanes, and the caller did not ask for scalar.</summary>
    internal static bool Vectorized => Vector256.IsHardwareAccelerated && !ForceScalarVector;

    private static void BoxMean(ReadOnlySpan<float> input, Span<float> output, int width, int height, int radius,
        float[] columnSums, float[] prefix, float[]? ring = null)
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

        if (ring is not null)
        {
            // Same rows, same order, copied verbatim: the ring is a cache of what `input` already holds, so
            // every later subtraction reads the identical float bit pattern it used to re-read from the plane.
            for (var i = 0; i < window; i++)
                input.Slice(Math.Clamp(i - radius, 0, height - 1) * width, width)
                    .CopyTo(ring.AsSpan(i * width, width));
        }

        var interior = width > radius * 2 + 1;

        for (var y = 0; y < height; y++)
        {
            if (y > 0 && ring is null)
            {
                var add = input.Slice(Math.Clamp(y + radius, 0, height - 1) * width, width);
                var remove = input.Slice(Math.Clamp(y - radius - 1, 0, height - 1) * width, width);
                for (var x = 0; x < width; x++)
                    columnSums[x] += add[x] - remove[x];
            }
            else if (y > 0)
            {
                // The slot that held the leaving row is the same slot the entering row belongs in, because the
                // window is exactly 2r+1 rows wide. The expression is the scalar twin's, character for
                // character; only where the leaving row is read from changed.
                var slot = (y - 1) % window;
                var leave = ring.AsSpan(slot * width, width);
                var enter = input.Slice(Math.Clamp(y + radius, 0, height - 1) * width, width);
                var x = 0;

                if (Vectorized && leave.Length >= 8 && enter.Length >= 8)
                {
                    // Lane i computes exactly what the scalar body computes, in the same order, so the sums are
                    // bit-identical; nothing is reduced across lanes.
                    for (; x + 8 <= width; x += 8)
                    {
                        var entering = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(enter), (nuint)x);
                        var leaving = Vector256.LoadUnsafe(ref leave[x]);
                        var sum = Vector256.LoadUnsafe(ref columnSums[x]);
                        // (sum + (entering - leaving)), not ((sum + entering) - leaving): the scalar body adds
                        // the difference, and in floating point the two groupings round differently.
                        (sum + (entering - leaving)).StoreUnsafe(ref columnSums[x]);
                        entering.StoreUnsafe(ref leave[x]);
                    }
                }

                for (; x < width; x++)
                {
                    var value = enter[x];
                    columnSums[x] += value - leave[x];
                    leave[x] = value;
                }
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
