using System.Runtime.Intrinsics;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>One non-zero entry of a sparse two-dimensional kernel, in destination-relative coordinates.</summary>
internal readonly record struct SparseTap(int Dx, int Dy, float Weight);

/// <summary>
/// Convolution over planar float data, the shape deconvolution needs: one plane per colour channel, no
/// per-iteration quantisation to the working format. Two kernels are offered because the two PSFs differ in
/// structure, not in spirit.
/// <para>
/// <b>Determinism.</b> The vector axis is <i>pixels</i> and every lane sums its taps in ascending index
/// order, so a lane's addition sequence is character-for-character the scalar twin's. That is the same
/// discipline the separable blur uses, and it is why the two paths agree byte for byte rather than within a
/// tolerance. Nothing is reduced across lanes.
/// </para>
/// </summary>
internal static class PlanarConvolve
{
    /// <summary>Test hook: forces the scalar twin so a test can compare the two paths on real data.</summary>
    internal static bool ForceScalar;

    /// <summary>
    /// Test hook: takes the <see cref="Vector512{T}"/> path even when the hardware has no AVX-512.
    /// <para>
    /// That is not a benchmark trick — it is the only way to check the fourth tier's correctness on a machine
    /// that cannot execute it natively. <c>Vector512&lt;T&gt;</c> is a 64-byte struct with a software
    /// implementation, so the arithmetic and therefore the bytes can be compared here; the throughput cannot.
    /// </para>
    /// </summary>
    internal static bool ForceVector512;

    /// <summary>Widest tier that will run: 512 when the hardware (or the test hook) allows and scalar is not forced.</summary>
    internal static bool Use512 => !ForceScalar && (Vector512.IsHardwareAccelerated || ForceVector512);

    private static bool Use256 => !ForceScalar && !Use512 && Vector256.IsHardwareAccelerated;

    /// <summary>Rows per band when the caller does not pin one.</summary>
    private const int DefaultBandRows = 64;

    /// <summary>
    /// Symmetric separable kernel: horizontal pass into a band scratch of <c>rows + 2·radius</c> filtered
    /// rows, then the vertical pass reads it. Rows outside the image are clamped, which is edge replication
    /// and matches <see cref="FilterOps.GaussianBlur"/>.
    /// </summary>
    /// <param name="source">Input plane, <c>width * height</c> floats, row-major.</param>
    /// <param name="destination">Output plane, same size. May not alias <paramref name="source"/>.</param>
    /// <param name="width">Plane width in samples.</param>
    /// <param name="height">Plane height in rows.</param>
    /// <param name="kernel">Odd-length symmetric taps, ascending.</param>
    public static void Separable(float[] source, float[] destination, int width, int height, float[] kernel)
    {
        var radius = kernel.Length / 2;
        var bandRows = Math.Clamp(height / Math.Max(1, RowParallel.Degree * 2), 16, DefaultBandRows);
        var bandCount = (height + bandRows - 1) / bandRows;

        RowParallel.ForBands(bandCount, (start, end) =>
        {
            // A band writes at most `bandRows` rows plus one radius of context above and below; a short
            // image still needs every row plus the halo, so the bound is the band, not the height.
            var scratch = FloatScratch.Rent((bandRows + 2 * radius) * width);
            try
            {
                for (var b = start; b < end; b++)
                {
                    var top = b * bandRows;
                    var bottom = Math.Min(height, top + bandRows);

                    // Rows [top - radius, bottom + radius), clamped to the image, filtered horizontally.
                    for (var i = 0; i < bottom - top + 2 * radius; i++)
                    {
                        var y = Math.Clamp(top - radius + i, 0, height - 1);
                        HorizontalRow(source, y * width, width, scratch, i * width, kernel, radius);
                    }

                    for (var y = top; y < bottom; y++)
                        VerticalRow(scratch, (y - top) * width, width, destination, y * width, kernel);
                }
            }
            finally
            {
                FloatScratch.Return(scratch);
            }
        });
    }

    /// <summary>
    /// Direct two-dimensional convolution with a sparse kernel: only the non-zero entries cost anything. A
    /// motion PSF of length <c>L</c> has about <c>L</c> of them, against <c>(2r+1)²</c> for the dense form —
    /// 15 against 529 at the default length. Taps are applied in the order given; the caller sorts them.
    /// </summary>
    public static void Sparse(float[] source, float[] destination, int width, int height, SparseTap[] taps)
    {
        var leftEdge = 0;
        var rightEdge = width;
        foreach (var tap in taps)
        {
            leftEdge = Math.Max(leftEdge, -tap.Dx);
            rightEdge = Math.Min(rightEdge, width - tap.Dx);
        }

        leftEdge = Math.Clamp(leftEdge, 0, width);
        rightEdge = Math.Clamp(rightEdge, leftEdge, width);

        var bandRows = Math.Clamp(height / Math.Max(1, RowParallel.Degree * 2), 16, DefaultBandRows);
        var bandCount = (height + bandRows - 1) / bandRows;

        RowParallel.ForBands(bandCount, (start, end) =>
        {
            for (var b = start; b < end; b++)
            {
                var top = b * bandRows;
                var bottom = Math.Min(height, top + bandRows);
                for (var y = top; y < bottom; y++)
                {
                    var output = y * width;
                    Array.Clear(destination, output, width);

                    foreach (var tap in taps)
                    {
                        var row = Math.Clamp(y + tap.Dy, 0, height - 1) * width;
                        var weight = tap.Weight;
                        var column = 0;
                        for (; column < leftEdge; column++)
                            destination[output + column] +=
                                source[row + Math.Clamp(column + tap.Dx, 0, width - 1)] * weight;

                        if (Use512)
                        {
                            var vector = Vector512.Create(weight);
                            for (; column + 15 < rightEdge; column += 16)
                            {
                                var partial = Vector512.LoadUnsafe(ref destination[output + column]);
                                var sample = Vector512.LoadUnsafe(ref source[row + column + tap.Dx]);
                                (partial + sample * vector).StoreUnsafe(ref destination[output + column]);
                            }
                        }
                        else if (Use256)
                        {
                            var vector = Vector256.Create(weight);
                            for (; column + 7 < rightEdge; column += 8)
                            {
                                var partial = Vector256.LoadUnsafe(ref destination[output + column]);
                                var sample = Vector256.LoadUnsafe(ref source[row + column + tap.Dx]);
                                (partial + sample * vector).StoreUnsafe(ref destination[output + column]);
                            }
                        }

                        for (; column < width; column++)
                            destination[output + column] +=
                                source[row + Math.Clamp(column + tap.Dx, 0, width - 1)] * weight;
                    }
                }
            }
        });
    }

    /// <summary>One output sample of the scalar twin; taps ascending, indices clamped.</summary>
    private static float Sample(float[] source, int row, int width, int x, float[] kernel, int radius)
    {
        var sum = 0f;
        for (var k = -radius; k <= radius; k++)
            sum += source[row + Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
        return sum;
    }

    private static void HorizontalRow(float[] source, int sourceRow, int width, float[] destination,
        int destinationRow, float[] kernel, int radius)
    {
        if (Use512)
        {
            HorizontalRow512(source, sourceRow, width, destination, destinationRow, kernel, radius);
            return;
        }

        if (ForceScalar || !Vector256.IsHardwareAccelerated)
        {
            for (var scalar = 0; scalar < width; scalar++)
                destination[destinationRow + scalar] = Sample(source, sourceRow, width, scalar, kernel, radius);
            return;
        }

        // Only the interior can take eight lanes at once: a lane that needs its index clamped is not a
        // neighbour of the others, and reading past the row silently borrows the next row's samples.
        var prefix = Math.Min(radius, width);
        var x = 0;
        for (; x < prefix; x++)
            destination[destinationRow + x] = Sample(source, sourceRow, width, x, kernel, radius);

        for (; x + 7 + radius <= width - 1; x += 8)
        {
            var sum = Vector256<float>.Zero;
            for (var k = -radius; k <= radius; k++)
                sum += Vector256.LoadUnsafe(ref source[sourceRow + x + k]) * Vector256.Create(kernel[k + radius]);
            sum.StoreUnsafe(ref destination[destinationRow + x]);
        }

        for (; x < width; x++)
            destination[destinationRow + x] = Sample(source, sourceRow, width, x, kernel, radius);
    }

    /// <summary>
    /// Sixteen pixel lanes, taps ascending. Identical arithmetic to the scalar twin per lane, so the bytes
    /// match; the interior rule is the 256 version's — a lane that needs its index clamped is not a neighbour
    /// of the others and must go through <see cref="Sample"/>.
    /// </summary>
    private static void HorizontalRow512(float[] source, int sourceRow, int width, float[] destination,
        int destinationRow, float[] kernel, int radius)
    {
        var prefix = Math.Min(radius, width);
        var x = 0;
        for (; x < prefix; x++)
            destination[destinationRow + x] = Sample(source, sourceRow, width, x, kernel, radius);

        for (; x + 15 + radius <= width - 1; x += 16)
        {
            var sum = Vector512<float>.Zero;
            for (var k = -radius; k <= radius; k++)
                sum += Vector512.LoadUnsafe(ref source[sourceRow + x + k]) * Vector512.Create(kernel[k + radius]);
            sum.StoreUnsafe(ref destination[destinationRow + x]);
        }

        for (; x < width; x++)
            destination[destinationRow + x] = Sample(source, sourceRow, width, x, kernel, radius);
    }

    private static void VerticalRow(float[] source, int sourceRow, int width, float[] destination,
        int destinationRow, float[] kernel)
    {
        var taps = kernel.Length;
        if (Use512)
        {
            var x16 = 0;
            for (; x16 + 15 < width; x16 += 16)
            {
                var sum = Vector512<float>.Zero;
                for (var k = 0; k < taps; k++)
                    sum += Vector512.LoadUnsafe(ref source[sourceRow + k * width + x16]) * Vector512.Create(kernel[k]);
                sum.StoreUnsafe(ref destination[destinationRow + x16]);
            }

            for (; x16 < width; x16++)
            {
                var scalar = 0f;
                for (var k = 0; k < taps; k++)
                    scalar += source[sourceRow + k * width + x16] * kernel[k];
                destination[destinationRow + x16] = scalar;
            }

            return;
        }

        if (ForceScalar || !Vector256.IsHardwareAccelerated)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var k = 0; k < taps; k++)
                    sum += source[sourceRow + k * width + x] * kernel[k];
                destination[destinationRow + x] = sum;
            }

            return;
        }

        var x4 = 0;
        for (; x4 + 7 < width; x4 += 8)
        {
            var sum = Vector256<float>.Zero;
            for (var k = 0; k < taps; k++)
                sum += Vector256.LoadUnsafe(ref source[sourceRow + k * width + x4]) * Vector256.Create(kernel[k]);
            sum.StoreUnsafe(ref destination[destinationRow + x4]);
        }

        for (; x4 < width; x4++)
        {
            var sum = 0f;
            for (var k = 0; k < taps; k++)
                sum += source[sourceRow + k * width + x4] * kernel[k];
            destination[destinationRow + x4] = sum;
        }
    }
}
