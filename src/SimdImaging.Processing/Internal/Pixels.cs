using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Internal;

/// <summary>Row-level sample access: the only place that knows how a <see cref="WorkingFormat"/> maps to floats.
/// Everything above works in premultiplied RGBA floats, in [0,1] for integer formats.</summary>
internal static class Pixels
{
    public const int MaxCode8 = 255;
    public const int MaxCode16 = 65535;

    public static float MaxCode(WorkingFormat format) => format switch
    {
        WorkingFormat.Rgba8 => MaxCode8,
        WorkingFormat.Rgba16 => MaxCode16,
        _ => 1f,
    };

    /// <summary>Reads a row as floats, straight (unassociated) alpha.</summary>
    public static void ReadRowRaw(ReadOnlySpan<byte> row, int width, WorkingFormat format, Span<float> destination)
    {
        switch (format)
        {
            case WorkingFormat.Rgba8:
                for (int x = 0, s = 0, d = 0; x < width; x++, s += 4, d += 4)
                {
                    destination[d] = row[s] * (1f / MaxCode8);
                    destination[d + 1] = row[s + 1] * (1f / MaxCode8);
                    destination[d + 2] = row[s + 2] * (1f / MaxCode8);
                    destination[d + 3] = row[s + 3] * (1f / MaxCode8);
                }
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                for (int x = 0, s = 0, d = 0; x < width; x++, s += 4, d += 4)
                {
                    destination[d] = samples[s] * (1f / MaxCode16);
                    destination[d + 1] = samples[s + 1] * (1f / MaxCode16);
                    destination[d + 2] = samples[s + 2] * (1f / MaxCode16);
                    destination[d + 3] = samples[s + 3] * (1f / MaxCode16);
                }
                break;
            }

            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                samples[..(width * 4)].CopyTo(destination);
                break;
            }
        }
    }

    /// <summary>Reads a row as floats with colour multiplied by alpha — what every filter must see, or a
    /// transparent neighbourhood bleeds its colour into the result.</summary>
    public static void ReadRowPremultiplied(ReadOnlySpan<byte> row, int width, WorkingFormat format, Span<float> destination)
    {
        if (format == WorkingFormat.Rgba8 && Vector128.IsHardwareAccelerated)
        {
            ReadRowPremultipliedRgba8Vector(row, width, destination);
            return;
        }

        ReadRowPremultipliedScalar(row, width, format, destination);
    }

    /// <summary>The scalar twin: the reference every vector tier must reproduce byte for byte.</summary>
    internal static void ReadRowPremultipliedScalar(ReadOnlySpan<byte> row, int width, WorkingFormat format, Span<float> destination)
    {
        ReadRowRaw(row, width, format, destination);
        for (var i = 0; i < width * 4; i += 4)
        {
            var alpha = destination[i + 3];
            destination[i] *= alpha;
            destination[i + 1] *= alpha;
            destination[i + 2] *= alpha;
        }
    }

    /// <summary>
    /// Four pixels per iteration: 16 bytes → two ushort vectors → four uint vectors → four float vectors,
    /// then each pixel's alpha is broadcast over its RGB lanes. Storing the alpha lane unmultiplied keeps the
    /// result identical to the scalar twin (multiplying lane 3 would square it).
    /// </summary>
    internal static void ReadRowPremultipliedRgba8Vector(ReadOnlySpan<byte> row, int width, Span<float> destination)
    {
        var scale = Vector128.Create(1f / MaxCode8);
        var x = 0;
        for (; x + 4 <= width; x += 4)
        {
            var packed = Vector128.LoadUnsafe(ref Unsafe.AsRef(in row[x * 4]));
            var (low, high) = Vector128.Widen(packed);       // 8 + 8 ushort channels
            var (r0, r1) = Vector128.Widen(low);             // 4 + 4 uint channels
            var (r2, r3) = Vector128.Widen(high);

            var p0 = Vector128.ConvertToSingle(r0.AsInt32()) * scale;
            var p1 = Vector128.ConvertToSingle(r1.AsInt32()) * scale;
            var p2 = Vector128.ConvertToSingle(r2.AsInt32()) * scale;
            var p3 = Vector128.ConvertToSingle(r3.AsInt32()) * scale;

            StorePremultiplied(p0, destination, (x + 0) * 4);
            StorePremultiplied(p1, destination, (x + 1) * 4);
            StorePremultiplied(p2, destination, (x + 2) * 4);
            StorePremultiplied(p3, destination, (x + 3) * 4);
        }

        for (; x < width; x++)
        {
            var s = x * 4;
            var d = x * 4;
            var a = row[s + 3] * (1f / MaxCode8);
            destination[d] = row[s] * (1f / MaxCode8) * a;
            destination[d + 1] = row[s + 1] * (1f / MaxCode8) * a;
            destination[d + 2] = row[s + 2] * (1f / MaxCode8) * a;
            destination[d + 3] = a;
        }
    }

    private static void StorePremultiplied(Vector128<float> pixel, Span<float> destination, int offset)
    {
        var alpha = Vector128.Create(pixel.GetElement(3));
        var product = pixel * alpha;
        product = product.WithElement(3, pixel.GetElement(3));
        product.StoreUnsafe(ref destination[offset]);
    }

    /// <summary>Writes floats as samples, straight alpha, clamping and rounding to the format's range.</summary>
    public static void WriteRowRaw(ReadOnlySpan<float> source, int width, WorkingFormat format, Span<byte> row)
    {
        switch (format)
        {
            case WorkingFormat.Rgba8:
                for (int x = 0, s = 0, d = 0; x < width; x++, s += 4, d += 4)
                {
                    row[d] = ToByte(source[s]);
                    row[d + 1] = ToByte(source[s + 1]);
                    row[d + 2] = ToByte(source[s + 2]);
                    row[d + 3] = ToByte(source[s + 3]);
                }
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                for (int x = 0, s = 0, d = 0; x < width; x++, s += 4, d += 4)
                {
                    samples[d] = ToUInt16(source[s]);
                    samples[d + 1] = ToUInt16(source[s + 1]);
                    samples[d + 2] = ToUInt16(source[s + 2]);
                    samples[d + 3] = ToUInt16(source[s + 3]);
                }
                break;
            }

            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                for (var i = 0; i < width * 4; i++)
                    samples[i] = source[i];
                break;
            }
        }
    }

    /// <summary>Writes premultiplied floats after dividing the colour back out; <c>a == 0</c> writes 0.</summary>
    /// <summary>Test hook: forces the scalar twin so a test can compare the two paths on real data.</summary>
    internal static bool ForceScalarWrite;

    public static void WriteRowUnpremultiplied(ReadOnlySpan<float> source, int width, WorkingFormat format, Span<byte> row)
    {
        if (format == WorkingFormat.Rgba8 && !ForceScalarWrite && Vector128.IsHardwareAccelerated)
        {
            WriteRowUnpremultipliedRgba8Vector(source, width, row);
            return;
        }

        WriteRowUnpremultipliedScalar(source, width, format, row);
    }

    /// <summary>The scalar twin: divide, then <c>round(v·255)</c> with a clamp.</summary>
    internal static void WriteRowUnpremultipliedScalar(ReadOnlySpan<float> source, int width, WorkingFormat format, Span<byte> row)
    {
        Span<float> straight = stackalloc float[4];
        for (var x = 0; x < width; x++)
        {
            var offset = x * 4;
            var alpha = source[offset + 3];
            var inverse = alpha > 0f ? 1f / alpha : 0f;
            straight[0] = source[offset] * inverse;
            straight[1] = source[offset + 1] * inverse;
            straight[2] = source[offset + 2] * inverse;
            straight[3] = alpha;
            WritePixel(straight, format, row, offset);
        }
    }

    /// <summary>
    /// Four pixels per iteration. The alpha lane is restored from the source after the reciprocal multiply —
    /// dividing it would normalise alpha to 1 and write 255 for every non-zero alpha.
    /// </summary>
    internal static void WriteRowUnpremultipliedRgba8Vector(ReadOnlySpan<float> source, int width, Span<byte> row)
    {
        var zero = Vector128<float>.Zero;
        var one = Vector128.Create(1f);
        var scale = Vector128.Create((float)MaxCode8);
        var half = Vector128.Create(0.5f);
        var ceiling = Vector128.Create((float)MaxCode8);

        var x = 0;
        for (; x + 4 <= width; x += 4)
        {
            var offset = x * 4;
            var p0 = Vector128.LoadUnsafe(ref Unsafe.AsRef(in source[offset]));
            var p1 = Vector128.LoadUnsafe(ref Unsafe.AsRef(in source[offset + 4]));
            var p2 = Vector128.LoadUnsafe(ref Unsafe.AsRef(in source[offset + 8]));
            var p3 = Vector128.LoadUnsafe(ref Unsafe.AsRef(in source[offset + 12]));

            var i0 = EncodePixel(p0, zero, one, scale, half, ceiling);
            var i1 = EncodePixel(p1, zero, one, scale, half, ceiling);
            var i2 = EncodePixel(p2, zero, one, scale, half, ceiling);
            var i3 = EncodePixel(p3, zero, one, scale, half, ceiling);

            var low = Vector128.Narrow(i0.AsUInt32(), i1.AsUInt32());    // 8 ushorts: pixels 0-1
            var high = Vector128.Narrow(i2.AsUInt32(), i3.AsUInt32());   // 8 ushorts: pixels 2-3
            Vector128.Narrow(low, high).StoreUnsafe(ref row[offset]);
        }

        for (; x < width; x++)
        {
            var offset = x * 4;
            var alpha = source[offset + 3];
            var inverse = alpha > 0f ? 1f / alpha : 0f;
            row[offset] = ToByte(source[offset] * inverse);
            row[offset + 1] = ToByte(source[offset + 1] * inverse);
            row[offset + 2] = ToByte(source[offset + 2] * inverse);
            row[offset + 3] = ToByte(alpha);
        }
    }

    private static Vector128<int> EncodePixel(Vector128<float> pixel, Vector128<float> zero, Vector128<float> one,
        Vector128<float> scale, Vector128<float> half, Vector128<float> ceiling)
    {
        var alpha = Vector128.Create(pixel.GetElement(3));
        // alpha <= 0, not alpha == 0: a Lanczos overshoot makes a filtered alpha negative, and dividing a
        // negative colour by a negative alpha would flip the sign into a bright pixel. The scalar twin
        // guards with `alpha > 0`, so this must match it exactly.
        var nonPositive = Vector128.LessThanOrEqual(alpha, zero);
        var inverse = Vector128.ConditionalSelect(nonPositive, zero, one / alpha);
        var straight = pixel * inverse;
        straight = straight.WithElement(3, pixel.GetElement(3));

        var scaled = straight * scale + half;
        var clamped = Vector128.Max(Vector128.Min(scaled, ceiling), zero);
        return Vector128.ConvertToInt32(clamped);
    }

    private static void WritePixel(ReadOnlySpan<float> pixel, WorkingFormat format, Span<byte> row, int pixelOffset)
    {
        switch (format)
        {
            case WorkingFormat.Rgba8:
                row[pixelOffset] = ToByte(pixel[0]);
                row[pixelOffset + 1] = ToByte(pixel[1]);
                row[pixelOffset + 2] = ToByte(pixel[2]);
                row[pixelOffset + 3] = ToByte(pixel[3]);
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                samples[pixelOffset] = ToUInt16(pixel[0]);
                samples[pixelOffset + 1] = ToUInt16(pixel[1]);
                samples[pixelOffset + 2] = ToUInt16(pixel[2]);
                samples[pixelOffset + 3] = ToUInt16(pixel[3]);
                break;
            }

            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                samples[pixelOffset] = pixel[0];
                samples[pixelOffset + 1] = pixel[1];
                samples[pixelOffset + 2] = pixel[2];
                samples[pixelOffset + 3] = pixel[3];
                break;
            }
        }
    }

    internal static byte ToByte(float value)
    {
        var scaled = value * MaxCode8 + 0.5f;
        if (scaled <= 0f) return 0;
        if (scaled >= MaxCode8) return MaxCode8;
        return (byte)scaled;
    }

    private static ushort ToUInt16(float value)
    {
        var scaled = value * MaxCode16 + 0.5f;
        if (scaled <= 0f) return 0;
        if (scaled >= MaxCode16) return MaxCode16;
        return (ushort)scaled;
    }
}
