using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Byte-domain fast path for point operators on Rgba8.
/// <para>The LUT is built by running the operator's <b>own</b> row transform through
/// <see cref="Pixels.ReadRowRaw"/> and <see cref="Pixels.WriteRowRaw"/> — the exact same scalar code the
/// normal path uses — so the byte result is identical by construction, not by re-deriving the arithmetic.
/// There is no second implementation to drift.</para>
/// <para>Both paths are row-parallel (architecture.md §6.2): rows are independent, so the worker count
/// cannot change a single output byte.</para>
/// </summary>
internal static class PointLut
{
    // Reused per thread so steady-state managed allocation stays at zero; a byte[]/float[] can be
    // captured by the parallel lambda, whereas a stackalloc Span cannot.
    [ThreadStatic] private static byte[]? _lutBytes;

    /// <summary>
    /// Colour-matrix products, laid out <b>value-major</b>: three records of 256 × 4 floats, where lane
    /// <c>row</c> of <c>records[(input * 256 + v) * 4 + row]</c> is what input byte <c>v</c> on channel
    /// <c>input</c> contributes to output channel <c>row</c>. The padding lane stays zero.
    /// </summary>
    [ThreadStatic] private static float[]? _matrixRecords;

    /// <summary>Test hook: force the scalar path so the vector twin can be compared byte for byte.</summary>
    internal static bool ForceScalarMatrixPack;

    /// <summary>Alpha bytes of four packed pixels, everything else zero (little-endian <c>0xFF000000</c>).</summary>
    private static readonly Vector128<byte> AlphaLanes =
        Vector128.Create(0xFF000000u, 0xFF000000u, 0xFF000000u, 0xFF000000u).AsByte();

    /// <summary>
    /// Vector twin of <see cref="Pixels.ToByte"/>: <c>clamp(v·255 + 0.5, 0, 255)</c> then truncate.
    /// This is the same clamp/convert/narrow sequence already proven byte-identical by RowMathTests
    /// for the unpremultiply writer. Four pixels (16 bytes) are packed per iteration.
    /// </summary>
    private static Vector128<int> ClampBytes(Vector128<float> value, Vector128<float> scale,
        Vector128<float> half, Vector128<float> max, Vector128<float> zero)
    {
        var scaled = value * scale + half;
        return Vector128.ConvertToInt32(Vector128.Max(Vector128.Min(scaled, max), zero));
    }

    internal static WorkingBuffer ApplyRgba8(PixelView source, IPixelPool? pool, PointOps.RowTransform transform)
    {
        var lut = _lutBytes ??= new byte[4 * 256];

        Span<byte> onePixel = stackalloc byte[4];
        Span<float> oneRow = stackalloc float[4];
        Span<byte> oneResult = stackalloc byte[4];

        for (var value = 0; value < 256; value++)
        {
            onePixel[0] = onePixel[1] = onePixel[2] = onePixel[3] = (byte)value;
            Pixels.ReadRowRaw(onePixel, 1, WorkingFormat.Rgba8, oneRow);
            transform(oneRow, 1);
            Pixels.WriteRowRaw(oneRow, 1, WorkingFormat.Rgba8, oneResult);

            lut[value] = oneResult[0];
            lut[256 + value] = oneResult[1];
            lut[512 + value] = oneResult[2];
            lut[768 + value] = oneResult[3];
        }

        var destination = WorkingBuffer.Rent(source.Width, source.Height, source.Format, pool);
        var view = destination.View;
        RowParallel.For(source.Width, source.Height, (start, end) =>
        {
            for (var y = start; y < end; y++)
            {
                var from = source.Row(y);
                var to = view.Row(y);
                var length = source.Width * 4;
                for (var i = 0; i < length; i += 4)
                {
                    to[i] = lut[from[i]];
                    to[i + 1] = lut[256 + from[i + 1]];
                    to[i + 2] = lut[512 + from[i + 2]];
                    to[i + 3] = lut[768 + from[i + 3]];
                }
            }
        });

        return destination;
    }

    /// <summary>
    /// Byte-domain path for a 3×4 colour matrix.
    /// <para>The products are precomputed per byte value and stored <b>value-major</b>: one 16-byte record
    /// per input byte holding that byte's contribution to all three output channels. One pixel therefore
    /// costs three 16-byte loads instead of nine 4-byte loads, and the clamp/pack reads the sum straight out
    /// of the register — no stack staging.</para>
    /// <para>The arithmetic and its order are unchanged (<c>((r + g) + b) + offset</c>, no FMA, no
    /// reassociation), so the byte result is identical by construction; <c>PointLutTests</c> and the
    /// end-to-end parity run hold that to the byte. Storing the nine products coefficient-major is the
    /// shape this replaced: it needed nine dependent scalar loads per pixel and measured 2× slower.</para>
    /// </summary>
    internal static WorkingBuffer ApplyMatrixRgba8(PixelView source, IPixelPool? pool, ReadOnlySpan<float> m)
    {
        var records = _matrixRecords ??= new float[3 * 256 * 4];
        const float scale = 1f / 255f;
        for (var input = 0; input < 3; input++)
        {
            var c0 = m[input];
            var c1 = m[4 + input];
            var c2 = m[8 + input];
            var recordBase = input * 256 * 4;
            for (var v = 0; v < 256; v++)
            {
                var sample = v * scale;
                var at = recordBase + v * 4;
                records[at] = c0 * sample;
                records[at + 1] = c1 * sample;
                records[at + 2] = c2 * sample;
                records[at + 3] = 0f;
            }
        }

        var offsets = Vector128.Create(m[3], m[7], m[11], 0f);

        // Grayscale builds three identical matrix rows; the record lanes are then identical too, so the
        // general path already produces the same byte three times. That used to be a separate scalar
        // branch — it is now just the vector path, which is why it no longer needs special-casing.
        var destination = WorkingBuffer.Rent(source.Width, source.Height, source.Format, pool);
        var view = destination.View;
        RowParallel.For(source.Width, source.Height, (start, end) =>
        {
            var useVector = Vector128.IsHardwareAccelerated && !ForceScalarMatrixPack;
            var scaleVector = Vector128.Create(255f);
            var halfVector = Vector128.Create(0.5f);
            var maxVector = Vector128.Create(255f);
            var zeroVector = Vector128<float>.Zero;

            for (var y = start; y < end; y++)
            {
                var from = source.Row(y);
                var to = view.Row(y);
                var length = source.Width * 4;
                var i = 0;
                var vectorEnd = useVector ? length - length % 16 : 0;
                for (; i < vectorEnd; i += 16)
                {
                    var q0 = ClampBytes(Contribution(from, i, records, offsets),
                        scaleVector, halfVector, maxVector, zeroVector);
                    var q1 = ClampBytes(Contribution(from, i + 4, records, offsets),
                        scaleVector, halfVector, maxVector, zeroVector);
                    var q2 = ClampBytes(Contribution(from, i + 8, records, offsets),
                        scaleVector, halfVector, maxVector, zeroVector);
                    var q3 = ClampBytes(Contribution(from, i + 12, records, offsets),
                        scaleVector, halfVector, maxVector, zeroVector);

                    var low = Vector128.Narrow(q0.AsUInt32(), q1.AsUInt32());
                    var high = Vector128.Narrow(q2.AsUInt32(), q3.AsUInt32());
                    var packed = Vector128.Narrow(low, high);

                    // The record padding lane is zero, so alpha comes back with one mask-and-or instead of
                    // four byte stores.
                    var alpha = Vector128.LoadUnsafe(ref from[i]) & AlphaLanes;
                    (packed | alpha).StoreUnsafe(ref to[i]);
                }

                // Scalar twin: tails shorter than 4 pixels, and the non-vector fallback.
                for (; i < length; i += 4)
                {
                    var value = Contribution(from, i, records, offsets);
                    to[i] = Pixels.ToByte(value.GetElement(0));
                    to[i + 1] = Pixels.ToByte(value.GetElement(1));
                    to[i + 2] = Pixels.ToByte(value.GetElement(2));
                    to[i + 3] = from[i + 3];
                }
            }
        });

        return destination;
    }

    /// <summary>
    /// One pixel's three output channels: <c>((r·c, g·c) + b·c) + offset</c>, each channel's contributions
    /// already multiplied out in <paramref name="records"/>. The addition order matches the scalar row
    /// transform exactly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Contribution(ReadOnlySpan<byte> row, int p, float[] records,
        Vector128<float> offsets)
    {
        var r = Vector128.LoadUnsafe(ref records[row[p] * 4]);
        var g = Vector128.LoadUnsafe(ref records[(256 + row[p + 1]) * 4]);
        var b = Vector128.LoadUnsafe(ref records[(512 + row[p + 2]) * 4]);
        return ((r + g) + b) + offsets;
    }
}
