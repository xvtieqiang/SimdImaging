using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>Which curve compresses high dynamic range into 0..1.</summary>
public enum ToneMapCurve : byte
{
    /// <summary>No compression: only clamp.</summary>
    None,

    /// <summary><c>x / (1 + x)</c> — cheap, washes out highlights.</summary>
    Reinhard,

    /// <summary>Reinhard with a white point: <c>x(1 + x/w²)/(1 + x)</c>.</summary>
    ReinhardExtended,

    /// <summary>The Uncharted 2 filmic curve.</summary>
    Hable,

    /// <summary>The Narkowicz ACES approximation used by most real-time renderers.</summary>
    Aces,

    /// <summary>BT.2390 EETF, evaluated in the PQ domain — the broadcast HDR → SDR mapping.</summary>
    Bt2390,
}

/// <summary>
/// High dynamic range operators. <see cref="ToneMap"/> reads linear light and writes linear light in 0..1 —
/// encode with <c>Delinearize</c> afterwards if the target is a display. <see cref="ApplyGainMap"/> goes the
/// other way and produces values above one.
/// </summary>
public static class HdrOps
{
    /// <summary>Compresses linear light into 0..1.</summary>
    /// <param name="source">The pixels, read as linear light.</param>
    /// <param name="curve">The tone curve.</param>
    /// <param name="exposure">Exposure in stops, applied before the curve.</param>
    /// <param name="whitePoint">Scene value that should map to white; also the display peak for
    /// <see cref="ToneMapCurve.Bt2390"/>, in units of 10 000 cd/m².</param>
    /// <param name="autoExposure">Add an exposure offset so the log-average luminance lands on
    /// <paramref name="targetLuminance"/>.</param>
    /// <param name="targetLuminance">Middle grey the auto exposure aims for.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The white point or target is out of range.</exception>
    public static PixelBuffer ToneMap(PixelView source, ToneMapCurve curve = ToneMapCurve.Aces, float exposure = 0f,
        float whitePoint = 1f, bool autoExposure = false, float targetLuminance = 0.18f, IPixelPool? pool = null)
    {
        if (!(whitePoint > 0f) || float.IsInfinity(whitePoint))
            throw new ArgumentOutOfRangeException(nameof(whitePoint), whitePoint, "White point must be finite and positive.");
        if (!(targetLuminance > 0f) || targetLuminance > 1f)
            throw new ArgumentOutOfRangeException(nameof(targetLuminance), targetLuminance, "Target must be in (0, 1].");
        if (!float.IsFinite(exposure))
            throw new ArgumentOutOfRangeException(nameof(exposure), exposure, "Exposure must be finite.");

        var stops = exposure;
        if (autoExposure)
        {
            var average = LogAverageLuminance(source);
            if (average > 0f)
                stops += MathF.Log2(targetLuminance / average);
        }

        var scale = MathF.Pow(2f, stops);
        var width = source.Width;
        var stride = width * 4;
        var destination = PixelBuffer.Rent(width, source.Height, source.Format, pool);
        var row = new float[stride];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                row[offset] = Map(row[offset] * scale, curve, whitePoint);
                row[offset + 1] = Map(row[offset + 1] * scale, curve, whitePoint);
                row[offset + 2] = Map(row[offset + 2] * scale, curve, whitePoint);
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>
    /// Applies a gain map to an SDR base: <c>linear(base) · 2^(gain · log2(headroom) · weight)</c>. The result
    /// is linear light and may exceed one — that is the point of a gain map.
    /// </summary>
    /// <param name="source">The SDR base, read as sRGB.</param>
    /// <param name="gainMap">The gain map; its luma is the log2 gain, normalised to 0..1.</param>
    /// <param name="headroom">Largest brightness multiplier the map can reach, above one.</param>
    /// <param name="weight">Scales the gain, 0..1 — a way to dial the effect down.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentException">The images differ in size.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The headroom or weight is out of range.</exception>
    public static PixelBuffer ApplyGainMap(PixelView source, PixelView gainMap, float headroom = 4f, float weight = 1f,
        IPixelPool? pool = null)
    {
        if (source.Width != gainMap.Width || source.Height != gainMap.Height)
            throw new ArgumentException($"Gain map is {gainMap.Width}x{gainMap.Height}, base is {source.Width}x{source.Height}.");
        if (!(headroom > 1f) || float.IsInfinity(headroom))
            throw new ArgumentOutOfRangeException(nameof(headroom), headroom, "Headroom must be greater than 1.");
        if (!(weight >= 0f) || weight > 1f)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weight must be in [0, 1].");

        var width = source.Width;
        var stride = width * 4;
        var destination = PixelBuffer.Rent(width, source.Height, WorkingFormat.Rgba32F, pool);

        var row = new float[stride];
        var gainRow = new float[stride];
        var stops = MathF.Log2(headroom);

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            Pixels.ReadRowRaw(gainMap.Row(y), width, gainMap.Format, gainRow);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                var gain = 0.299f * gainRow[offset] + 0.587f * gainRow[offset + 1] + 0.114f * gainRow[offset + 2];
                var scale = MathF.Pow(2f, gain * stops * weight);

                row[offset] = ColorMath.SrgbToLinear(row[offset]) * scale;
                row[offset + 1] = ColorMath.SrgbToLinear(row[offset + 1]) * scale;
                row[offset + 2] = ColorMath.SrgbToLinear(row[offset + 2]) * scale;
            }

            Pixels.WriteRowRaw(row, width, WorkingFormat.Rgba32F, destination.View.Row(y));
        }

        return destination;
    }

    private static float Map(float value, ToneMapCurve curve, float whitePoint)
    {
        var mapped = curve switch
        {
            ToneMapCurve.None => value,
            ToneMapCurve.Reinhard => value / (1f + value),
            ToneMapCurve.ReinhardExtended => value * (1f + value / (whitePoint * whitePoint)) / (1f + value),
            ToneMapCurve.Hable => Hable(value) / Hable(whitePoint),
            ToneMapCurve.Aces => Aces(value),
            ToneMapCurve.Bt2390 => Bt2390(value, whitePoint),
            _ => value,
        };

        return Math.Clamp(mapped, 0f, 1f);
    }

    /// <summary>Uncharted 2's filmic curve: the constants are the published fit.</summary>
    private static float Hable(float x)
    {
        const float A = 0.15f;
        const float B = 0.50f;
        const float C = 0.10f;
        const float D = 0.20f;
        const float E = 0.02f;
        const float F = 0.30f;

        return (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F) - E / F;
    }

    private static float Aces(float x)
    {
        // Narkowicz's fit to the ACES RRT+ODT; saturates near 1 and keeps shadows linear.
        var numerator = x * (2.51f * x + 0.03f);
        var denominator = x * (2.43f * x + 0.59f) + 0.14f;
        return denominator <= 0f ? 0f : numerator / denominator;
    }

    /// <summary>
    /// BT.2390 EETF: a Hermite spline that stays linear below the knee and lands exactly on the display peak.
    /// It lives in PQ code space, so the linear input is encoded, mapped, and decoded again.
    /// </summary>
    private static float Bt2390(float linear, float displayPeak)
    {
        var peak = ColorMath.PqEncode(Math.Clamp(displayPeak, 0f, 1f));
        var knee = 1.5f * peak - 0.5f;
        if (knee <= 0f)
            return linear;

        // A display peak at the PQ ceiling puts the knee at 1.0; the spline would then divide by zero for
        // anything brighter, so the range is closed by clamping instead.
        if (knee >= 1f)
            return Math.Clamp(linear, 0f, 1f);

        var encoded = Math.Clamp(ColorMath.PqEncode(linear), 0f, 1f);
        if (encoded <= knee)
            return ColorMath.PqDecode(encoded);

        var t = (encoded - knee) / (1f - knee);
        var t2 = t * t;
        var t3 = t2 * t;
        var mapped = (2f * t3 - 3f * t2 + 1f) * knee
                     + (t3 - 2f * t2 + t) * (1f - knee)
                     + (-2f * t3 + 3f * t2) * peak;

        return ColorMath.PqDecode(mapped);
    }

    /// <summary>Geometric mean of the luminance, the exposure base every auto-exposure algorithm uses.</summary>
    private static float LogAverageLuminance(PixelView source)
    {
        var width = source.Width;
        var row = new float[width * 4];
        double sum = 0;

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                var luminance = 0.2126f * row[offset] + 0.7152f * row[offset + 1] + 0.0722f * row[offset + 2];
                sum += Math.Log(Math.Max(luminance, 1e-6f));
            }
        }

        return MathF.Exp((float)(sum / (source.Width * source.Height)));
    }
}
