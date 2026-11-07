using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>Which transfer curve converts between encoded samples and linear light.</summary>
public enum TransferCurve : byte
{
    /// <summary>Identity: the samples already are linear light.</summary>
    Linear,

    /// <summary>sRGB (IEC 61966-2-1): a 12.92 slope below 0.04045, a 2.4 power above.</summary>
    Srgb,

    /// <summary>Pure 2.2 power, the approximation most textures and shaders assume.</summary>
    Gamma22,

    /// <summary>Rec. 709 / BT.709 OETF: a 4.5 slope below 0.018.</summary>
    Rec709,

    /// <summary>SMPTE ST 2084 (PQ), normalised so 1.0 is 10 000 cd/m².</summary>
    Pq,

    /// <summary>BT.2100 HLG scene OETF, normalised to the 0..1 scene range.</summary>
    Hlg,
}

/// <summary>
/// Encoded ↔ linear light. The curves are evaluated into a 4096-entry table and read back with a linear
/// interpolation, which keeps a 16-bit input within half a code value without a <c>pow</c> per pixel.
/// </summary>
public static class TransferOps
{
    private const int TableSize = 4096;

    /// <summary>Decodes encoded samples into linear light.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="curve">The transfer curve the samples are encoded with.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static PixelBuffer Linearize(PixelView source, TransferCurve curve = TransferCurve.Srgb,
        IPixelPool? pool = null) => Apply(source, curve, toLinear: true, pool);

    /// <summary>Encodes linear light into the given transfer curve.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="curve">The transfer curve to encode with.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static PixelBuffer Delinearize(PixelView source, TransferCurve curve = TransferCurve.Srgb,
        IPixelPool? pool = null) => Apply(source, curve, toLinear: false, pool);

    private static PixelBuffer Apply(PixelView source, TransferCurve curve, bool toLinear, IPixelPool? pool)
    {
        var width = source.Width;
        var stride = width * 4;
        var destination = PixelBuffer.Rent(width, source.Height, source.Format, pool);

        if (curve == TransferCurve.Linear)
        {
            for (var y = 0; y < source.Height; y++)
                source.Row(y).CopyTo(destination.View.Row(y));
            return destination;
        }

        var table = BuildTable(curve, toLinear);
        var row = new float[stride];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                row[offset] = Lookup(table, row[offset]);
                row[offset + 1] = Lookup(table, row[offset + 1]);
                row[offset + 2] = Lookup(table, row[offset + 2]);
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    private static float[] BuildTable(TransferCurve curve, bool toLinear)
    {
        var table = new float[TableSize + 1];
        for (var i = 0; i <= TableSize; i++)
        {
            var value = i / (float)TableSize;
            table[i] = toLinear ? Decode(curve, value) : Encode(curve, value);
        }

        return table;
    }

    private static float Lookup(float[] table, float value)
    {
        var scaled = Math.Clamp(value, 0f, 1f) * TableSize;
        var index = (int)scaled;
        if (index >= TableSize)
            return table[TableSize];

        var fraction = scaled - index;
        return table[index] + (table[index + 1] - table[index]) * fraction;
    }

    private static float Decode(TransferCurve curve, float value) => curve switch
    {
        TransferCurve.Srgb => value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f),
        TransferCurve.Gamma22 => MathF.Pow(value, 2.2f),
        TransferCurve.Rec709 => value < 0.081f
            ? value / 4.5f
            : MathF.Pow((value + 0.099f) / 1.099f, 1f / 0.45f),
        TransferCurve.Pq => ColorMath.PqDecode(value),
        TransferCurve.Hlg => HlgToLinear(value),
        _ => value,
    };

    private static float Encode(TransferCurve curve, float value) => curve switch
    {
        TransferCurve.Srgb => value <= 0.0031308f
            ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f,
        TransferCurve.Gamma22 => MathF.Pow(value, 1f / 2.2f),
        TransferCurve.Rec709 => value < 0.018f
            ? value * 4.5f
            : 1.099f * MathF.Pow(value, 0.45f) - 0.099f,
        TransferCurve.Pq => ColorMath.PqEncode(value),
        TransferCurve.Hlg => LinearToHlg(value),
        _ => value,
    };

    private const float HlgA = 0.17883277f;
    private const float HlgB = 0.28466892f;
    private const float HlgC = 0.55991073f;

    private static float HlgToLinear(float value) => value <= 0.5f
        ? value * value / 3f
        : (MathF.Exp((value - HlgC) / HlgA) + HlgB) / 12f;

    private static float LinearToHlg(float value) => value <= 1f / 12f
        ? MathF.Sqrt(3f * value)
        : HlgA * MathF.Log(12f * value - HlgB) + HlgC;
}
