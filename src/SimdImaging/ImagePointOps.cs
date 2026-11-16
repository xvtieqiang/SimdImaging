using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Colour point operators. Alpha is never modified: adjusting colour must not change coverage.</summary>
public sealed partial class Image
{
    /// <summary>Inverts the colour channels.</summary>
    public Image Invert(IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Invert(this[0].Pixels.View, pool));

    /// <summary>Adds <paramref name="amount"/> in sample units (0.5 = half the range).</summary>
    public Image Brightness(float shift = 0f, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Brightness(this[0].Pixels.View, shift, pool));

    /// <summary>Scales contrast around mid-grey; 1 is identity.</summary>
    /// <summary>
    /// Several per-channel point transforms collapsed into one table and applied in a single pass (8-bit only).
    /// </summary>
    /// <param name="transforms">The transforms, applied in order.</param>
    public Image PointChain(params PointOps.RowTransform[] transforms) =>
        ReplaceBuffer(FilterOps.PointChain(this[0].Pixels.View, transforms));

    /// <summary>Applies a pre-built chain table; the same table can be reused across images and threads.</summary>
    /// <param name="table">The table.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public Image PointChain(PointChainTable table, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Apply(this[0].Pixels.View, table, pool));

    /// <summary>Scales contrast about mid-grey.</summary>
    /// <param name="amount">1 is unchanged, below 1 flattens towards grey, above 1 increases it. 0 collapses the
    /// picture to a flat mid-grey; 2 doubles the distance from mid-grey. Typical range 0.5–1.5.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public Image Contrast(float gain = 1f, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Contrast(this[0].Pixels.View, gain, pool));

    /// <summary>Scales saturation around luma.</summary>
    /// <param name="bt709">Use BT.709 luma instead of BT.601.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <remarks>0 is greyscale, 1 is identity, 1.6 is a strong pop, above 2 clips fast.</remarks>
    public Image Saturation(float gain = 1f, bool bt709 = false, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Saturation(this[0].Pixels.View, gain, bt709, pool));

    /// <summary>Replaces colour with its luma.</summary>
    public Image Grayscale(bool bt709 = false, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Grayscale(this[0].Pixels.View, bt709, pool));

    /// <summary>Applies <c>v^(1/gamma)</c> to the colour channels.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gamma"/> is not finite and positive.</exception>
    public Image Gamma(float gamma, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Gamma(this[0].Pixels.View, gamma, pool));

    /// <summary>Applies a 3×4 colour matrix; alpha is untouched.</summary>
    /// <exception cref="ArgumentException">The matrix is not 12 floats.</exception>
    public Image ColorMatrix(ReadOnlySpan<float> matrix, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.ColorMatrix(this[0].Pixels.View, matrix, pool));

    /// <summary>Rotates hue by <paramref name="degrees"/>.</summary>
    public Image HueRotate(float degrees, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.HueRotate(this[0].Pixels.View, degrees, pool));

    /// <summary>Blends towards sepia; 0 is identity, 1 is full sepia.</summary>
    public Image Sepia(float blend = 1f, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Sepia(this[0].Pixels.View, blend, pool));

    /// <summary>Levels: input range, gamma, output range.</summary>
    /// <remarks>
    /// Every value is normalised: 0..1 corresponds to 0..255 on an 8-bit image, so a black point of 0.05 is what a
    /// levels dialog calls "input 13".
    /// </remarks>
    /// <param name="inputBlack">Input level mapped to black, 0..1.</param>
    /// <param name="inputWhite">Input level mapped to white, 0..1.</param>
    /// <param name="gamma">Midtone bias; 1 is linear, below 1 brightens the mids, above 1 darkens them.</param>
    /// <param name="outputBlack">Output level for black, 0..1. Above 0 is the faded-film look.</param>
    /// <param name="outputWhite">Output level for white, 0..1. Below 1 is the faded-film look.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The input range is empty or gamma is not positive.</exception>
    public Image Levels(float inputBlack, float inputWhite, float gamma = 1f, float outputBlack = 0f, float outputWhite = 1f,
        IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Levels(this[0].Pixels.View, inputBlack, inputWhite, gamma, outputBlack, outputWhite, pool));

    /// <summary>Makes alpha fully transparent or fully opaque by a threshold.</summary>
    public Image AlphaThreshold(float threshold = 0.5f, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.AlphaThreshold(this[0].Pixels.View, threshold, pool));

    /// <summary>Stretches each colour channel's min..max to the full range.</summary>
    public Image Normalize(IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Normalize(this[0].Pixels.View, pool));

    /// <summary>Vibrance: boosts muted colours more than already-saturated ones.</summary>
    /// <remarks>0 is unchanged, 0.5 is a clear lift on skin and sky, 1 is already strong. Unlike
    /// <see cref="Saturation"/> it leaves saturated colours alone, so it rarely clips.</remarks>
    /// <param name="amount">Strength.</param>
    /// <param name="bt709">Use BT.709 luma instead of BT.601.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public Image Vibrance(float lift = 0f, bool bt709 = false, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Vibrance(this[0].Pixels.View, lift, bt709, pool));

    /// <summary>White balance shift.</summary>
    /// <remarks>0 is neutral. Positive warms (orange), negative cools (blue). ±1 is the practical limit: beyond it
    /// the opposite channel starts clipping on ordinary photographs. Typical range ±0.35.</remarks>
    /// <param name="amount">Signed shift, positive warms.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public Image Temperature(float shift = 0f, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Temperature(this[0].Pixels.View, shift, pool));

    /// <summary>Blends the colour towards <paramref name="tint"/>.</summary>
    public Image Tint(RgbaColor tint, float amount = 0.5f, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Tint(this[0].Pixels.View, tint, amount, pool));

    /// <summary>Colour balance across shadows, midtones and highlights.</summary>
    public Image ColorBalance(ColorBalanceShifts shadows, ColorBalanceShifts midtones, ColorBalanceShifts highlights,
        bool bt709 = false, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.ColorBalance(this[0].Pixels.View, shadows, midtones, highlights, bt709, pool));

    /// <summary>Reorders colour channels; 0 = R, 1 = G, 2 = B.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A source index is outside 0..2.</exception>
    public Image ChannelSwap(int redSource, int greenSource, int blueSource, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.ChannelSwap(this[0].Pixels.View, redSource, greenSource, blueSource, pool));

    /// <summary>Hard black/white by luma.</summary>
    public Image BlackWhite(float threshold = 0.5f, bool bt709 = false, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.BlackWhite(this[0].Pixels.View, threshold, bt709, pool));

    /// <summary>Clamps the colour channels into [<paramref name="low"/>, <paramref name="high"/>].</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is inverted or outside 0..1.</exception>
    public Image Clamp(float low, float high, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Clamp(this[0].Pixels.View, low, high, pool));

    /// <summary>Multiplies alpha by <paramref name="opacity"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is outside 0..1.</exception>
    public Image Opacity(float opacity, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Opacity(this[0].Pixels.View, opacity, pool));

    /// <summary>Tonal transfer function through control points (monotone cubic).</summary>
    /// <exception cref="ArgumentException">Fewer than two points, or X is not strictly increasing.</exception>
    public Image Curves(ReadOnlySpan<CurvePoint> points, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Curves(this[0].Pixels.View, points, pool));

    /// <summary>Multiplies the colour channels; 1 is identity, above 1 brightens.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is negative.</exception>
    public Image Lightness(float factor, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Lightness(this[0].Pixels.View, factor, pool));

    /// <summary>Reduces each colour channel to <paramref name="levels"/> evenly spaced values.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="levels"/> is less than 2.</exception>
    public Image Posterize(int levels, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Posterize(this[0].Pixels.View, levels, pool));

    /// <summary>Black or white by luma against <paramref name="threshold"/> (0..1).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0..1.</exception>
    public Image Threshold(float threshold, bool bt709 = false, IPixelPool? pool = null) =>
        ReplaceBuffer(PointOps.Threshold(this[0].Pixels.View, threshold, bt709, pool));

    /// <summary>Decodes the samples into linear light with <paramref name="curve"/>.</summary>
    public Image Linearize(TransferCurve curve = TransferCurve.Srgb, IPixelPool? pool = null) =>
        ReplaceBuffer(TransferOps.Linearize(this[0].Pixels.View, curve, pool));

    /// <summary>Encodes linear light into <paramref name="curve"/>.</summary>
    public Image Delinearize(TransferCurve curve = TransferCurve.Srgb, IPixelPool? pool = null) =>
        ReplaceBuffer(TransferOps.Delinearize(this[0].Pixels.View, curve, pool));

    /// <summary>Binarises against the local mean, so uneven illumination does not swallow the subject.</summary>
    /// <param name="radius">Window radius in pixels.</param>
    /// <param name="offset">How far below the local mean the threshold sits, 0..1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The radius or offset is out of range.</exception>
    public Image AdaptiveThreshold(int radius = 8, float offset = 0.05f, IPixelPool? pool = null) =>
        ReplaceBuffer(ThresholdOps.AdaptiveThreshold(this[0].Pixels.View, radius, offset, pool));

    /// <summary>Swaps a background colour for another, with a tolerance and a feather band.</summary>
    /// <param name="background">The colour to remove.</param>
    /// <param name="replacement">The colour to put in its place.</param>
    /// <param name="tolerance">Distance below which a pixel is fully replaced.</param>
    /// <param name="feather">Band above the tolerance over which the replacement fades in.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A distance is negative.</exception>
    public Image ReplaceBackground(RgbaColor background, RgbaColor replacement, float tolerance = 0.1f,
        float feather = 0.05f, IPixelPool? pool = null) =>
        ReplaceBuffer(BackgroundOps.ReplaceBackground(this[0].Pixels.View, background, replacement, tolerance, feather, pool));

    /// <summary>Simulates a colour vision deficiency, or daltonises to compensate for one.</summary>
    /// <param name="kind">Which deficiency.</param>
    /// <param name="severity">0 is normal vision, 1 is full dichromacy.</param>
    /// <param name="correct">Push the lost difference into channels the viewer still sees.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is outside 0..1.</exception>
    public Image ColorBlindness(ColorVisionDeficiency kind = ColorVisionDeficiency.Protanopia, float severity = 1f,
        bool correct = false, IPixelPool? pool = null) =>
        ReplaceBuffer(ColorBlindnessOps.ColorBlindness(this[0].Pixels.View, kind, severity, correct, pool));
}
