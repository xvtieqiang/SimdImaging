using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>
/// The operators that compile to plan steps, shared by the in-memory <see cref="ImagePipeline"/> and the file
/// <see cref="ImageTranscode"/>: one declaration, so the two surfaces cannot drift apart.
/// <para>Each method appends one step and returns the derived pipeline, which is why the base is generic in its own
/// type. Geometry and composite operators stay on <see cref="ImagePipeline"/>; they do not fit a same-size plan.</para>
/// </summary>
/// <typeparam name="TSelf">The concrete pipeline, returned for chaining.</typeparam>
public abstract class ImageOpPipeline<TSelf> where TSelf : ImageOpPipeline<TSelf>
{
    /// <summary>Appends one plan step and returns this pipeline.</summary>
    protected abstract TSelf Step(ImageOp op);

    /// <summary>Appends one plan step with its injected second input. Only the in-memory pipeline carries one today;
    /// the file-side transcode has no overlay surface, so the default refuses instead of dropping the view.</summary>
    protected virtual TSelf Step(ImageOp op, PixelView second) =>
        throw new NotSupportedException($"{GetType().Name} cannot carry a second input.");

    /// <summary>Brightness by <paramref name="shift"/>.</summary>
    public TSelf Brightness(float shift = 0f) => Step(PlanOps.Brightness(shift));

    /// <summary>Contrast about the mid-point.</summary>
    public TSelf Contrast(float gain = 1f) => Step(PlanOps.Contrast(gain));

    /// <summary>Gamma correction; above one darkens.</summary>
    public TSelf Gamma(float gamma) => Step(PlanOps.Gamma(gamma));

    /// <summary>Inverts the colour channels.</summary>
    public TSelf Invert() => Step(PlanOps.Invert());

    /// <summary>BT.601 luma.</summary>
    public TSelf Grayscale(bool bt709 = false) => Step(PlanOps.Grayscale(bt709));

    /// <summary>Blends towards sepia.</summary>
    public TSelf Sepia(float blend = 1f) => Step(PlanOps.Sepia(blend));

    /// <summary>Scales saturation about luma.</summary>
    public TSelf Saturation(float gain = 1f, bool bt709 = false) => Step(PlanOps.Saturation(gain, bt709));

    /// <summary>Quantizes each channel to <paramref name="levels"/> steps.</summary>
    public TSelf Posterize(int levels) => Step(PlanOps.Posterize(levels));

    /// <summary>Remaps the tone curve.</summary>
    public TSelf Levels(float inputBlack, float inputWhite, float gamma = 1f,
        float outputBlack = 0f, float outputWhite = 1f) =>
        Step(PlanOps.Levels(inputBlack, inputWhite, gamma, outputBlack, outputWhite));

    /// <summary>Gaussian blur.</summary>
    public TSelf GaussianBlur(float sigma) => Step(PlanOps.GaussianBlur(sigma));

    /// <summary>Box blur of the given radius.</summary>
    public TSelf BoxBlur(int radius) => Step(PlanOps.BoxBlur(radius));

    /// <summary>Unsharp mask.</summary>
    public TSelf Sharpen(float gain = 0.5f, float sigma = 1f) => Step(PlanOps.Sharpen(gain, sigma));

    /// <summary>3x3 emboss.</summary>
    public TSelf Emboss(float strength = 1f) => Step(PlanOps.Emboss(strength));

    /// <summary>3x3 Sobel magnitude.</summary>
    public TSelf Sobel() => Step(PlanOps.Sobel());

    /// <summary>3x3 median.</summary>
    public TSelf Median3x3() => Step(PlanOps.Median3x3());

    /// <summary>Convolution with an arbitrary odd kernel and any border mode.</summary>
    /// <param name="kernel">Weights, row-major, <c>(2r+1)²</c> of them.</param>
    /// <param name="radius">Kernel radius.</param>
    /// <param name="border">How to read outside the image.</param>
    /// <param name="bias">Added after the sum, in sample units.</param>
    public TSelf Convolve(ReadOnlySpan<float> kernel, int radius,
        BorderMode border = BorderMode.Clamp, float bias = 0f, float constantValue = 0f) =>
        Step(PlanOps.Convolve(kernel, radius, border, bias, constantValue));

    /// <summary>Warm or cool shift.</summary>
    /// <param name="shift">Positive warms, negative cools.</param>
    public TSelf Temperature(float shift = 0f) => Step(PlanOps.Temperature(shift));

    /// <summary>Tone curve through the given points.</summary>
    /// <param name="points">Control points, at least two.</param>
    public TSelf Curves(params CurvePoint[] points) => Step(PlanOps.Curves(points));

    /// <summary>Saturation weighted towards already-saturated colours.</summary>
    /// <param name="lift">0 is unchanged, 0.5 is a clear lift, 1 is already strong. Leaves saturated colours alone.</param>
    /// <param name="bt709">Use BT.709 luma instead of BT.601.</param>
    public TSelf Vibrance(float lift = 0f, bool bt709 = false) => Step(PlanOps.Vibrance(lift, bt709));

    /// <summary>Seeded grain; whole-image because the generator advances per pixel.</summary>
    /// <param name="amplitude">Amplitude in normalised units.</param>
    /// <param name="kind">Uniform or Gaussian.</param>
    /// <param name="seed">Sequence seed.</param>
    public TSelf Noise(float amplitude = 0.05f, NoiseKind kind = NoiseKind.Gaussian, uint seed = 1) =>
        Step(PlanOps.Noise(amplitude, kind, seed));

    /// <summary>Unsharp mask built from a negative Gaussian.</summary>
    /// <param name="gain">Strength.</param>
    /// <param name="sigma">Gaussian sigma.</param>
    public TSelf GaussianSharpen(float gain = 0.5f, float sigma = 1f) =>
        Step(PlanOps.GaussianSharpen(gain, sigma));

    /// <summary>Erosion or dilation with a square window of odd side <c>2r+1</c>.</summary>
    /// <param name="radius">Window radius; 1 is 3×3.</param>
    /// <param name="kind">Erode or dilate.</param>
    public TSelf Morph(int radius, Morphology kind = Morphology.Erode) =>
        Step(PlanOps.Morph(radius, kind));

    /// <summary>Applies a pre-built point chain table.</summary>
    public TSelf PointChain(PointChainTable table) => Step(PlanOps.PointChain(table));

    /// <summary>
    /// Several per-channel point transforms collapsed into one table and one pass (8-bit).
    /// </summary>
    /// <param name="transforms">The transforms, applied in order.</param>
    public TSelf PointChain(params PointOps.RowTransform[] transforms) =>
        Step(PlanOps.PointChain(transforms));

    /// <summary>Median with radius <paramref name="radius"/> (1 is 3×3, up to 8).</summary>
    /// <param name="radius">Window radius.</param>
    public TSelf Median(int radius) => Step(PlanOps.Median(radius));

    /// <summary>3x3 erosion.</summary>
    public TSelf Erode3x3() => Step(PlanOps.Erode3x3());

    /// <summary>3x3 dilation.</summary>
    public TSelf Dilate3x3() => Step(PlanOps.Dilate3x3());

    /// <summary>Erosion then dilation.</summary>
    public TSelf Open3x3() => Step(PlanOps.Open3x3());

    /// <summary>Dilation then erosion.</summary>
    public TSelf Close3x3() => Step(PlanOps.Close3x3());

    /// <summary>Directional blur.</summary>
    public TSelf MotionBlur(int length, float angleDegrees = 0f) =>
        Step(PlanOps.MotionBlur(length, angleDegrees));

    /// <summary>Disc-shaped bokeh.</summary>
    public TSelf BokehBlur(int radius) => Step(PlanOps.BokehBlur(radius));

    /// <summary>Non-local means.</summary>
    public TSelf NonLocalMeans(int searchRadius, int patchRadius = 1, float h = 0.1f) =>
        Step(PlanOps.NonLocalMeans(searchRadius, patchRadius, h));

    /// <summary>Oil paint.</summary>
    public TSelf OilPaint(int radius, int levels = 20) => Step(PlanOps.OilPaint(radius, levels));

    /// <summary>Vignette. Runs whole-image; costs one intermediate.</summary>
    public TSelf Vignette(float radius = 0.75f, float darkening = 1f) =>
        Step(PlanOps.Vignette(radius, darkening));

    /// <summary>CLAHE. Runs whole-image; costs one intermediate.</summary>
    public TSelf Clahe(int tilesX = 8, int tilesY = 8, float clipLimit = 3f) =>
        Step(PlanOps.Clahe(tilesX, tilesY, clipLimit));

    /// <summary>Per-channel histogram stretch. Runs whole-image.</summary>
    public TSelf Normalize() => Step(PlanOps.Normalize());

    /// <summary>Adaptive threshold. Runs whole-image.</summary>
    public TSelf AdaptiveThreshold(int radius = 8, float offset = 0.05f) =>
        Step(PlanOps.AdaptiveThreshold(radius, offset));

    /// <summary>Block mosaic.</summary>
    public TSelf Pixelate(int blockSize) => Step(PlanOps.Pixelate(blockSize));

    /// <summary>Removes haze. Runs whole-image; costs two single-channel planes.</summary>
    public TSelf Dehaze(int patchRadius = 7, float omega = 0.95f, float tMin = 0.1f, int refineRadius = 20) =>
        Step(PlanOps.Dehaze(patchRadius, omega, tMin, refineRadius));

    /// <summary>Histogram equalisation.</summary>
    public TSelf Equalize() => Step(PlanOps.Equalize());
}
