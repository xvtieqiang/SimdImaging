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
public static class PlanOps
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
    public static ImageOp Grayscale(bool bt709 = false) => Point("grayscale", v => PointOps.Grayscale(v, bt709));

    /// <summary>Blends towards a sepia tone; zero is identity.</summary>
    public static ImageOp Sepia(float amount = 1f) => Point("sepia", v => PointOps.Sepia(v, amount));

    /// <summary>Scales saturation about luma.</summary>
    public static ImageOp Saturation(float amount, bool bt709 = false) =>
        Point("saturation", v => PointOps.Saturation(v, amount, bt709));

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

    /// <summary>3x3 median; one row and one column of context.</summary>
    public static ImageOp Median3x3() => Neighborhood("median", 1, v => FilterOps.Median3x3(v));

    /// <summary>3x3 erosion; one row and one column.</summary>
    public static ImageOp Erode3x3() => Neighborhood("erode", 1, v => FilterOps.Erode3x3(v));

    /// <summary>3x3 dilation; one row and one column.</summary>
    public static ImageOp Dilate3x3() => Neighborhood("dilate", 1, v => FilterOps.Dilate3x3(v));

    /// <summary>Erosion then dilation, so two rows and two columns.</summary>
    public static ImageOp Open3x3() => Neighborhood("open", 2, v => FilterOps.Open3x3(v));

    /// <summary>Dilation then erosion, so two rows and two columns.</summary>
    public static ImageOp Close3x3() => Neighborhood("close", 2, v => FilterOps.Close3x3(v));

    /// <summary>Edge-preserving bilateral filter; its window is the radius.</summary>
    public static ImageOp Bilateral(int radius, float sigmaRange = 0.1f) =>
        Neighborhood("bilateral", radius, v => FilterOps.Bilateral(v, radius, 0f, sigmaRange));

    /// <summary>Directional blur; its taps span half the length either side.</summary>
    public static ImageOp MotionBlur(int length, float angleDegrees = 0f) =>
        Neighborhood("motion", Math.Max(1, (length + 1) / 2), v => FilterOps.MotionBlur(v, length, angleDegrees));

    /// <summary>Disc-shaped bokeh; its offsets stay inside the radius.</summary>
    public static ImageOp BokehBlur(int radius) => Neighborhood("bokeh", radius, v => FilterOps.BokehBlur(v, radius));

    /// <summary>Guided filter; its window is the radius.</summary>
    public static ImageOp GuidedFilter(int radius, float epsilon = 1e-4f) =>
        Neighborhood("guided", radius, v => GuidedFilterOps.GuidedFilter(v, radius, epsilon));

    /// <summary>
    /// Non-local means. Each output compares patches of radius <paramref name="patchRadius"/> inside a search
    /// window of <paramref name="searchRadius"/>, so it reads the sum of the two — not either one alone.
    /// </summary>
    public static ImageOp NonLocalMeans(int searchRadius, int patchRadius = 1, float h = 0.1f) =>
        Neighborhood("nlm", searchRadius + patchRadius,
            v => NonLocalMeansOps.NonLocalMeans(v, searchRadius, patchRadius, h));

    /// <summary>Oil paint; its histogram window is the radius.</summary>
    public static ImageOp OilPaint(int radius, int levels = 20) =>
        Neighborhood("oilpaint", radius, v => OilPaintOps.OilPaint(v, radius, levels));

    /// <summary>
    /// Vignette. It darkens by distance from the image centre, so a band would see its own centre instead — the
    /// whole picture is the only correct context. It costs one intermediate image, like calling it directly.
    /// </summary>
    public static ImageOp Vignette(float radius = 0.75f, float strength = 1f) =>
        Global("vignette", v => FilterOps.Vignette(v, radius, strength));

    /// <summary>CLAHE: it builds per-tile histograms, so it reads the whole picture.</summary>
    public static ImageOp Clahe(int tilesX = 8, int tilesY = 8, float clipLimit = 3f) =>
        Global("clahe", v => ClaheOps.Clahe(v, tilesX, tilesY, clipLimit));

    /// <summary>Local Laplacian: a pyramid, so every band would need the whole image anyway.</summary>
    public static ImageOp LocalLaplacian(int levels = 5, int samples = 32) =>
        Global("local-laplacian", v => LocalLaplacianOps.LocalLaplacian(v, levels, samples));

    /// <summary>Per-channel histogram stretch; whole-image statistics.</summary>
    public static ImageOp Normalize() => Global("normalize", v => PointOps.Normalize(v));

    /// <summary>Adaptive threshold: a local mean, but its window is wider than any band worth carrying.</summary>
    public static ImageOp AdaptiveThreshold(int radius = 8, float offset = 0.05f) =>
        Global("adaptive-threshold", v => ThresholdOps.AdaptiveThreshold(v, radius, offset));

    /// <summary>
    /// Block-aligned mosaic. A block spans rows that a band boundary can cut anywhere, so no halo is correct —
    /// whole-image is the only category that reproduces the single-image result.
    /// </summary>
    public static ImageOp Pixelate(int blockSize) => Global("pixelate", v => FilterOps.Pixelate(v, blockSize));

    /// <summary>Histogram equalisation needs the whole image to build its transfer curve.</summary>
    public static ImageOp Equalize() => Global("equalize", v => HistogramOps.Equalize(v));
}
