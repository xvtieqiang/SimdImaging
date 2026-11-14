using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Non-local means denoising: every output pixel is a weighted average of the pixels in a search window,
/// with weights given by the similarity of their patches. The sum is <b>exact and unpruned</b> — no early
/// termination, no approximate exp — so a double-precision reference can reproduce it byte for byte.
/// <para>
/// The support of one output pixel is <c>searchRadius + patchRadius</c>, so the operator is band-safe:
/// <c>OpCategory.Neighborhood</c> with that halo produces the same bytes as a whole-image pass. The four
/// conditions that make it true are (a) the view is full width, (b) every read clamps to the view it was
/// given, (c) no statistic is read from outside the view, and (d) each worker's row ring starts at
/// <c>start - support</c>, not at the band's first row.
/// </para>
/// <para>
/// Working domain is straight alpha, like <c>Bilateral</c>: the weights depend on pixel values, which is not
/// the fixed-kernel linear case that premultiplies. Alpha participates in both the distance and the average,
/// so a transparent neighbour does not drag colour into an opaque pixel. The distance is exact integer
/// arithmetic for <c>Rgba8</c> (int) and <c>Rgba16</c> (long — int32 overflows at 65535²), and double with a
/// frozen tap order for <c>Rgba32F</c>.
/// </para>
/// <para>
/// Cost is <c>(2S+1)² · (2P+1)²</c> distance terms per pixel. Measured at 1080p Rgba8 (this machine,
/// best of one warm run): the defaults (S=3, P=1) take 5.8 s; the quality setting (S=5, P=2) takes
/// 16.1 s. This is a seconds-class operator, not a milliseconds-class one — raise the radii with that
/// in mind, and note that the exponential is the floor once the distance is vectorised.
/// </para>
/// </summary>
public static class NonLocalMeansOps
{
    /// <summary>Test hook: forces the scalar distance so the vector twin can be compared byte for byte.
    /// Integer sums are associative, so the two must agree exactly — this is not a tolerance knob.</summary>
    internal static bool ForceScalarSsd;

    /// <summary>Denoises <paramref name="source"/> with non-local means.</summary>
    /// <param name="source">The input pixels.</param>
    /// <param name="searchRadius">Half-width of the search window; 0 restricts the search to the pixel itself.</param>
    /// <param name="patchRadius">Half-width of the comparison patch; 0 compares single pixels.</param>
    /// <param name="h">Filtering strength in sample units (0.1 ≈ 10% of the range). Must be finite and positive.</param>
    /// <param name="sigma">Expected noise standard deviation; the weight ignores distances below
    /// <c>2σ²</c> (the usual non-local means correction). 0 disables the correction.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A radius is negative, <paramref name="h"/> is not
    /// positive and finite, or <paramref name="sigma"/> is negative or not finite.</exception>
    public static WorkingBuffer NonLocalMeans(PixelView source, int searchRadius = 3, int patchRadius = 1,
        float h = 0.1f, float sigma = 0f, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(searchRadius);
        ArgumentOutOfRangeException.ThrowIfNegative(patchRadius);
        if (!(h > 0f) || !float.IsFinite(h))
            throw new ArgumentOutOfRangeException(nameof(h), h, "The filtering strength must be finite and positive.");
        if (!(sigma >= 0f) || !float.IsFinite(sigma))
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "The noise estimate must be finite and non-negative.");

        if (searchRadius == 0 && patchRadius == 0)
            return Geometry.Transforms.Copy(source, pool);

        // A long sum first: two int radii can overflow into a negative ring size and fail with a
        // misleading message from the pool instead of naming the parameter.
        var support64 = (long)searchRadius + patchRadius;
        if (support64 > 1 << 20)
            throw new ArgumentOutOfRangeException(nameof(searchRadius), searchRadius,
                "The combined search and patch radius is unreasonably large.");

        var support = (int)support64;
        var format = source.Format;
        var width = source.Width;
        var height = source.Height;
        var stride = width * format.BytesPerPixel();
        var ringRows = Math.Min(height, 2 * support + 1);
        var ringBytes = Math.Max(1, ringRows * stride);

        var patchPixels = (2 * patchRadius + 1) * (2 * patchRadius + 1);
        // h is expressed in sample units (0..1), so the integer distances are divided by the code range
        // squared as well as by the patch size: without that, the same h means a different strength per format.
        var maxCode = Pixels.MaxCode(format);
        var normalizer = 1.0 / (patchPixels * 4 * maxCode * maxCode);
        var hSquared = (double)h * h;
        var twoSigmaSquared = 2.0 * sigma * sigma;

        var destination = WorkingBuffer.Rent(width, height, format, pool);
        try
        {
            // One row band per worker; the ring is private to the worker, which is what makes the
            // worker count unable to change a byte.
            RowParallel.For(0, height, (start, end) =>
            {
                var ring = ArrayPool<byte>.Shared.Rent(ringBytes);
                var output = ArrayPool<float>.Shared.Rent(width * 4);
                try
                {
                    // Seed at start - support, NOT at start: the first output row reads rows the band
                    // itself never writes, and uninitialised ring rows are silently wrong on small images.
                    var computed = Math.Max(0, start - support) - 1;
                    for (var y = start; y < end; y++)
                    {
                        var last = Math.Min(y + support, height - 1);
                        while (computed < last)
                        {
                            computed++;
                            source.Row(computed).CopyTo(ring.AsSpan((computed % ringRows) * stride, stride));
                        }

                        for (var x = 0; x < width; x++)
                            Accumulate(ring, ringRows, stride, width, height, format, x, y,
                                searchRadius, patchRadius, normalizer, hSquared, twoSigmaSquared, output);

                        Pixels.WriteRowRaw(output.AsSpan(0, width * 4), width, format, destination.View.Row(y));
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(output);
                    ArrayPool<byte>.Shared.Return(ring);
                }
            });
        }
        catch
        {
            destination.Dispose();
            throw;
        }

        return destination;
    }

    /// <summary>The per-pixel weighted average; candidates run dy ascending, then dx ascending.</summary>
    private static void Accumulate(byte[] ring, int ringRows, int stride, int width, int height,
        WorkingFormat format, int x, int y, int searchRadius, int patchRadius, double normalizer,
        double hSquared, double twoSigmaSquared, float[] output)
    {
        double sumR = 0, sumG = 0, sumB = 0, sumA = 0, sumW = 0;

        for (var dy = -searchRadius; dy <= searchRadius; dy++)
        {
            var sy = Math.Clamp(y + dy, 0, height - 1);
            var candidateRow = ring.AsSpan((sy % ringRows) * stride, stride);

            for (var dx = -searchRadius; dx <= searchRadius; dx++)
            {
                var sx = Math.Clamp(x + dx, 0, width - 1);
                var distance = format switch
                {
                    WorkingFormat.Rgba8 =>
                        SsdRgba8(ring, ringRows, stride, width, height, x, y, sx, sy, patchRadius),
                    WorkingFormat.Rgba16 =>
                        SsdRgba16(ring, ringRows, stride, width, height, x, y, sx, sy, patchRadius),
                    _ => SsdFloat(ring, ringRows, stride, width, height, x, y, sx, sy, patchRadius),
                };

                // The centre candidate has distance 0, so sumW >= 1 and there is no zero-division branch.
                var weight = Math.Exp(-Math.Max(distance * normalizer - twoSigmaSquared, 0.0) / hSquared);

                ReadCentre(candidateRow, sx * 4, format, out var r, out var g, out var b, out var a);
                sumR += r * weight;
                sumG += g * weight;
                sumB += b * weight;
                sumA += a * weight;
                sumW += weight;
            }
        }

        var offset = x * 4;
        output[offset] = (float)(sumR / sumW);
        output[offset + 1] = (float)(sumG / sumW);
        output[offset + 2] = (float)(sumB / sumW);
        output[offset + 3] = (float)(sumA / sumW);
    }

    private static void ReadCentre(ReadOnlySpan<byte> row, int offset, WorkingFormat format,
        out float r, out float g, out float b, out float a)
    {
        switch (format)
        {
            case WorkingFormat.Rgba8:
                r = row[offset] * (1f / Pixels.MaxCode8);
                g = row[offset + 1] * (1f / Pixels.MaxCode8);
                b = row[offset + 2] * (1f / Pixels.MaxCode8);
                a = row[offset + 3] * (1f / Pixels.MaxCode8);
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                r = samples[offset] * (1f / Pixels.MaxCode16);
                g = samples[offset + 1] * (1f / Pixels.MaxCode16);
                b = samples[offset + 2] * (1f / Pixels.MaxCode16);
                a = samples[offset + 3] * (1f / Pixels.MaxCode16);
                break;
            }

            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                r = samples[offset];
                g = samples[offset + 1];
                b = samples[offset + 2];
                a = samples[offset + 3];
                break;
            }
        }
    }

    /// <summary>Exact integer SSD for Rgba8: at most 25·4·255² ≈ 6.5e6, comfortably inside int32.</summary>
    private static int SsdRgba8(byte[] ring, int ringRows, int stride, int width, int height,
        int ax, int ay, int bx, int by, int radius)
    {
        // A row of a patch is (2r+1)·4 bytes; below r=2 that is under one vector and the vector setup
        // costs more than it saves.
        if (radius >= 2 && Vector256.IsHardwareAccelerated && !ForceScalarSsd)
            return SsdRgba8Vector(ring, ringRows, stride, width, height, ax, ay, bx, by, radius);

        var sum = 0;
        for (var py = -radius; py <= radius; py++)
        {
            var rowA = ring.AsSpan((Math.Clamp(ay + py, 0, height - 1) % ringRows) * stride, stride);
            var rowB = ring.AsSpan((Math.Clamp(by + py, 0, height - 1) % ringRows) * stride, stride);
            for (var px = -radius; px <= radius; px++)
            {
                var ia = Math.Clamp(ax + px, 0, width - 1) * 4;
                var ib = Math.Clamp(bx + px, 0, width - 1) * 4;
                var d0 = rowA[ia] - rowB[ib];
                var d1 = rowA[ia + 1] - rowB[ib + 1];
                var d2 = rowA[ia + 2] - rowB[ib + 2];
                var d3 = rowA[ia + 3] - rowB[ib + 3];
                sum += d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
            }
        }

        return sum;
    }

    /// <summary>Vector twin of <see cref="SsdRgba8"/>: bytes widen to int16, subtract, then
    /// multiply-add-adjacent squares and accumulates in int32. Every partial sum is exact, so the lane
    /// grouping cannot change the value — the scalar path produces the same integer.</summary>
    private static int SsdRgba8Vector(byte[] ring, int ringRows, int stride, int width, int height,
        int ax, int ay, int bx, int by, int radius)
    {
        var accumulator = Vector256<int>.Zero;
        var scalarTotal = 0;

        for (var py = -radius; py <= radius; py++)
        {
            var rowA = ring.AsSpan((Math.Clamp(ay + py, 0, height - 1) % ringRows) * stride, stride);
            var rowB = ring.AsSpan((Math.Clamp(by + py, 0, height - 1) % ringRows) * stride, stride);

            // The vector block covers four whole pixels, and it may only be used where neither side is
            // clamped: a clamped column repeats the edge pixel, so the block would read the wrong bytes.
            var px = -radius;
            for (; px + 3 <= radius; px += 4)
            {
                var startA = ax + px;
                var startB = bx + px;
                if (startA < 0 || startA + 3 >= width || startB < 0 || startB + 3 >= width)
                    break;

                var a = Load16(rowA, startA * 4);
                var b = Load16(rowB, startB * 4);
                var difference = Avx2.Subtract(Avx2.ConvertToVector256Int16(a), Avx2.ConvertToVector256Int16(b));
                accumulator = Avx2.Add(accumulator, Avx2.MultiplyAddAdjacent(difference, difference));
            }

            for (; px <= radius; px++)
            {
                var ia = Math.Clamp(ax + px, 0, width - 1) * 4;
                var ib = Math.Clamp(bx + px, 0, width - 1) * 4;
                var d0 = rowA[ia] - rowB[ib];
                var d1 = rowA[ia + 1] - rowB[ib + 1];
                var d2 = rowA[ia + 2] - rowB[ib + 2];
                var d3 = rowA[ia + 3] - rowB[ib + 3];
                scalarTotal += d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
            }
        }

        Span<int> lanes = stackalloc int[Vector256<int>.Count];
        accumulator.CopyTo(lanes);
        var total = scalarTotal;
        for (var i = 0; i < lanes.Length; i++)
            total += lanes[i];

        return total;
    }

    private static Vector128<byte> Load16(ReadOnlySpan<byte> span, int offset) =>
        Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), offset));

    /// <summary>Exact SSD for Rgba16 in long: a single 65535² already overflows int32, and the patch sum
    /// reaches 4.3e11.</summary>
    private static long SsdRgba16(byte[] ring, int ringRows, int stride, int width, int height,
        int ax, int ay, int bx, int by, int radius)
    {
        long sum = 0;
        for (var py = -radius; py <= radius; py++)
        {
            var samplesA = MemoryMarshal.Cast<byte, ushort>(
                ring.AsSpan((Math.Clamp(ay + py, 0, height - 1) % ringRows) * stride, stride));
            var samplesB = MemoryMarshal.Cast<byte, ushort>(
                ring.AsSpan((Math.Clamp(by + py, 0, height - 1) % ringRows) * stride, stride));
            for (var px = -radius; px <= radius; px++)
            {
                var ia = Math.Clamp(ax + px, 0, width - 1) * 4;
                var ib = Math.Clamp(bx + px, 0, width - 1) * 4;
                long d0 = samplesA[ia] - samplesB[ib];
                long d1 = samplesA[ia + 1] - samplesB[ib + 1];
                long d2 = samplesA[ia + 2] - samplesB[ib + 2];
                long d3 = samplesA[ia + 3] - samplesB[ib + 3];
                sum += d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
            }
        }

        return sum;
    }

    /// <summary>SSD for Rgba32F in double, taps in the frozen py-then-px order.</summary>
    private static double SsdFloat(byte[] ring, int ringRows, int stride, int width, int height,
        int ax, int ay, int bx, int by, int radius)
    {
        var sum = 0.0;
        for (var py = -radius; py <= radius; py++)
        {
            var samplesA = MemoryMarshal.Cast<byte, float>(
                ring.AsSpan((Math.Clamp(ay + py, 0, height - 1) % ringRows) * stride, stride));
            var samplesB = MemoryMarshal.Cast<byte, float>(
                ring.AsSpan((Math.Clamp(by + py, 0, height - 1) % ringRows) * stride, stride));
            for (var px = -radius; px <= radius; px++)
            {
                var ia = Math.Clamp(ax + px, 0, width - 1) * 4;
                var ib = Math.Clamp(bx + px, 0, width - 1) * 4;
                double d0 = samplesA[ia] - samplesB[ib];
                double d1 = samplesA[ia + 1] - samplesB[ib + 1];
                double d2 = samplesA[ia + 2] - samplesB[ib + 2];
                double d3 = samplesA[ia + 3] - samplesB[ib + 3];
                sum += d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
            }
        }

        return sum;
    }
}
