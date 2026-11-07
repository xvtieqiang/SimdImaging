using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Colour-space conversion and chroma subsampling.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Converts the image into <paramref name="target"/>. The source is read as sRGB; the result is a float
    /// image in the target's own units (L 0..100 for Lab, chroma centred on 0.5 for YCbCr, and so on).
    /// </summary>
    /// <param name="target">The colour space to express the result in.</param>
    /// <param name="ycbcr">Luma coefficients when the target is YCbCr.</param>
    /// <param name="limitedRange">Use the 16..235 video range instead of 0..255.</param>
    /// <param name="white">Reference white for XYZ, xyY, Lab, Lch, Luv, Lchuv and Hunter Lab.</param>
    /// <param name="adaptation">How XYZ moves from D65 to <paramref name="white"/>.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public Image ConvertColorSpace(ColorSpace target, YCbCrStandard ycbcr = YCbCrStandard.Bt709,
        bool limitedRange = false, WhitePoint white = WhitePoint.D65,
        ChromaticAdaptation adaptation = ChromaticAdaptation.Bradford, IPixelPool? pool = null) =>
        ReplaceBuffer(ColorConversionOps.Convert(this[0].Pixels.View, target, ycbcr, limitedRange, white, adaptation, pool));

    /// <summary>
    /// Converts the pixels between ICC profiles. Both must be RGB matrix/TRC profiles; an empty profile means
    /// sRGB, so <c>IccTransform(profile)</c> brings a file's colours into sRGB.
    /// </summary>
    /// <param name="sourceProfile">The profile the pixels are encoded with; empty means sRGB.</param>
    /// <param name="destinationProfile">The profile to convert to; empty means sRGB.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="InvalidDataException">A profile is malformed.</exception>
    /// <exception cref="NotSupportedException">A profile is not RGB or describes colour with a lookup table.</exception>
    public Image IccTransform(ReadOnlySpan<byte> sourceProfile, ReadOnlySpan<byte> destinationProfile = default,
        IPixelPool? pool = null) =>
        ReplaceBuffer(IccTransformOps.Transform(this[0].Pixels.View, sourceProfile, destinationProfile, pool));

    /// <summary>Applies 4:2:2 or 4:2:0 chroma subsampling; <see cref="ChromaSubsampling.Yuv444"/> is a copy.</summary>
    /// <param name="mode">Subsampling layout.</param>
    /// <param name="ycbcr">Luma coefficients for the intermediate YCbCr.</param>
    /// <param name="limitedRange">Use the 16..235 video range.</param>
    /// <param name="bandRows">Rows per band; bounds the working set.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public Image ChromaResample(ChromaSubsampling mode, YCbCrStandard ycbcr = YCbCrStandard.Bt601,
        bool limitedRange = false, int bandRows = 128, IPixelPool? pool = null) =>
        ReplaceBuffer(ColorConversionOps.ChromaResample(this[0].Pixels.View, mode, ycbcr, limitedRange, bandRows, pool));
}
