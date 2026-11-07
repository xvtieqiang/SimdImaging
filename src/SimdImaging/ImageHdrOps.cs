using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>High dynamic range: tone mapping down, gain maps up.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Compresses linear light into 0..1. The image is read as linear; call <see cref="Linearize"/> first for
    /// an sRGB source and <see cref="Delinearize"/> afterwards for a display.
    /// </summary>
    /// <param name="curve">The tone curve.</param>
    /// <param name="exposure">Exposure in stops, applied before the curve.</param>
    /// <param name="whitePoint">Scene value that should map to white; the display peak for BT.2390.</param>
    /// <param name="autoExposure">Expose for the log-average luminance instead of an absolute level.</param>
    /// <param name="targetLuminance">Middle grey the auto exposure aims for.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The white point or target is out of range.</exception>
    public Image ToneMap(ToneMapCurve curve = ToneMapCurve.Aces, float exposure = 0f, float whitePoint = 1f,
        bool autoExposure = false, float targetLuminance = 0.18f, IPixelPool? pool = null) =>
        ReplaceBuffer(HdrOps.ToneMap(this[0].Pixels.View, curve, exposure, whitePoint, autoExposure, targetLuminance, pool));

    /// <summary>
    /// Applies a gain map: <c>linear(base) · 2^(gain · log2(headroom) · weight)</c>. The result is a float image
    /// in linear light and may exceed one.
    /// </summary>
    /// <param name="gainMap">The gain map, same size as this image; its luma is the log2 gain.</param>
    /// <param name="headroom">Largest brightness multiplier, above one.</param>
    /// <param name="weight">Scales the gain, 0..1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentException">The gain map differs in size.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The headroom or weight is out of range.</exception>
    public Image ApplyGainMap(Image gainMap, float headroom = 4f, float weight = 1f, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(gainMap);
        return ReplaceBuffer(HdrOps.ApplyGainMap(this[0].Pixels.View, gainMap[0].Pixels.View, headroom, weight, pool));
    }
}
