using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>
/// The operators a <see cref="ProcessingPlan"/> can run, each carrying its own support radius.
/// </summary>
/// <remarks>
/// <para>
/// Every factory here knows how far its operator reads, because that follows from the operator's own parameters:
/// a Gaussian's support is <c>ceil(3σ)</c>, a box blur's is its radius, a point operator reads one pixel. None of
/// that is the caller's business, and none of it can be got wrong.
/// </para>
/// <para>
/// Coverage is explicit rather than reflective: an operator appears here once its support has been measured.
/// <c>HaloDeclarationTests</c> derives the smallest halo that reproduces the whole-image result and compares it
/// with what this class claims. Operators not listed here cannot be planned yet, which is honest — nobody has
/// established what they read.
/// </para>
/// </remarks>
public static class ImageOps
{
    private static ImageOp Point(string name, Func<PixelView, WorkingBuffer> op) =>
        new(name, OpCategory.Point, 0, 0, PlanSteps.Point(op));

    private static ImageOp Neighborhood(string name, int halo, Func<PixelView, WorkingBuffer> op) =>
        new(name, OpCategory.Neighborhood, halo, halo, PlanSteps.Neighborhood(op, halo, halo));

    private static ImageOp Global(string name, Func<PixelView, WorkingBuffer> op) =>
        new(name, OpCategory.Global, 0, 0, PlanSteps.Global(op));

    /// <summary>Brightness by <paramref name="amount"/>.</summary>
    public static ImageOp Brightness(float amount) => Point("brightness", v => PointOps.Brightness(v, amount));

    /// <summary>Contrast about the mid-point.</summary>
    public static ImageOp Contrast(float amount) => Point("contrast", v => PointOps.Contrast(v, amount));

    /// <summary>Gamma correction; above one darkens.</summary>
    public static ImageOp Gamma(float gamma) => Point("gamma", v => PointOps.Gamma(v, gamma));

    /// <summary>Inverts the colour channels, leaving alpha.</summary>
    public static ImageOp Invert() => Point("invert", v => PointOps.Invert(v));

    /// <summary>BT.601 luma, alpha untouched.</summary>
    public static ImageOp Grayscale() => Point("grayscale", v => PointOps.Grayscale(v));

    /// <summary>Blends towards a sepia tone; zero is identity.</summary>
    public static ImageOp Sepia(float amount = 1f) => Point("sepia", v => PointOps.Sepia(v, amount));

    /// <summary>Scales saturation about luma.</summary>
    public static ImageOp Saturation(float amount) => Point("saturation", v => PointOps.Saturation(v, amount));

    /// <summary>Quantizes each channel to <paramref name="levels"/> steps.</summary>
    public static ImageOp Posterize(int levels) => Point("posterize", v => PointOps.Posterize(v, levels));

    /// <summary>Remaps the tone curve between a black and a white point.</summary>
    public static ImageOp Levels(float black, float white, float gamma) =>
        Point("levels", v => PointOps.Levels(v, black, white, gamma));

    /// <summary>Gaussian blur; support is <c>ceil(3σ)</c>, which is where the halo comes from.</summary>
    public static ImageOp GaussianBlur(float sigma) =>
        Neighborhood("blur", Math.Max(1, (int)MathF.Ceiling(sigma * 3f)), v => FilterOps.GaussianBlur(v, sigma));

    /// <summary>Box blur of the given radius; support is the radius.</summary>
    public static ImageOp BoxBlur(int radius) =>
        Neighborhood("box", radius, v => FilterOps.BoxBlur(v, radius));

    /// <summary>
    /// Unsharp mask. It blurs internally, so its support follows the Gaussian (<c>ceil(3σ)</c>), not the single
    /// row a 3x3 kernel would need — the mistake this type exists to prevent.
    /// </summary>
    public static ImageOp Sharpen(float amount = 0.5f, float sigma = 1f) =>
        Neighborhood("sharpen", Math.Max(1, (int)MathF.Ceiling(sigma * 3f)), v => FilterOps.Sharpen(v, amount, sigma));

    /// <summary>3x3 emboss; one row and one column.</summary>
    public static ImageOp Emboss() => Neighborhood("emboss", 1, v => FilterOps.Emboss(v));

    /// <summary>3x3 Sobel magnitude; one row and one column.</summary>
    public static ImageOp Sobel() => Neighborhood("sobel", 1, v => FilterOps.Sobel(v));

    /// <summary>
    /// Block-aligned mosaic. A block spans rows that a band boundary can cut anywhere, so no halo is correct —
    /// whole-image is the only category that reproduces the single-image result.
    /// </summary>
    public static ImageOp Pixelate(int blockSize) => Global("pixelate", v => FilterOps.Pixelate(v, blockSize));

    /// <summary>Histogram equalisation needs the whole image to build its transfer curve.</summary>
    public static ImageOp Equalize() => Global("equalize", v => HistogramOps.Equalize(v));
}
