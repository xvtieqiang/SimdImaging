using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>
/// Chains operations so the image is walked once per run of same-size steps instead of once per step.
/// </summary>
/// <remarks>
/// <para>
/// Calling <c>image.A()</c> then <c>b.B()</c> then <c>c.C()</c> allocates a whole image per link; three alive at
/// 8K is 396 MiB. A pipeline walks the picture in horizontal bands instead — read a band, run every same-size
/// step on it, write it out, move on — so no intermediate image exists and a five-step chain costs 72 KiB.
/// </para>
/// <para>
/// A size-changing step is handled here rather than pushed onto the caller. <c>Resize</c>, <c>Crop</c> and
/// <c>Rotate90</c> end the current band pass, run it, apply themselves, and start a new pass; the chain reads
/// the same either way. That split is the only thing bands cannot cross, and it is the library's problem.
/// </para>
/// <para>
/// What the executor needs to know about an operator — how far past the band it reads, whether it needs the
/// whole picture — travels with the operator, which is why none of it appears in this API.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var result = image.Pipe()
///     .Resize(1920, 1080)
///     .Brightness(0.05f)
///     .Sharpen()
///     .ToImage();
/// </code>
/// </example>
public sealed class ImagePipeline
{
    private readonly Image _source;
    private readonly IPixelPool? _pool;
    private readonly List<ImageOp> _pending = [];
    private Image? _current;
    private int _bandRows = ProcessingPlan.DefaultBandRows;
    private bool _finished;

    internal ImagePipeline(Image source, IPixelPool? pool = null)
    {
        _source = source;
        _pool = pool;
    }

    /// <summary>Rows per band; smaller bands trade bookkeeping for less working memory.</summary>
    public ImagePipeline BandRows(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        _bandRows = rows;
        return this;
    }

    /// <summary>Resizes. Ends the current band pass and continues with a new one.</summary>
    public ImagePipeline Resize(int width, int height, Resampler resampler = Resampler.Bicubic) =>
        Geometry(src => src.Resize(width, height, resampler, _pool));

    /// <summary>Crops to <paramref name="region"/>. Ends the current band pass.</summary>
    public ImagePipeline Crop(PixelRect region) => Geometry(src => src.Crop(region, _pool));

    /// <summary>Rotates by a quarter turn. Ends the current band pass.</summary>
    public ImagePipeline Rotate90(bool clockwise = true) => Geometry(src => src.Rotate90(clockwise, _pool));

    /// <summary>Rotates by a half turn. Ends the current band pass.</summary>
    public ImagePipeline Rotate180() => Geometry(src => src.Rotate180(_pool));

    /// <summary>Mirrors left to right. Ends the current band pass.</summary>
    public ImagePipeline FlipHorizontal() => Geometry(src => src.FlipHorizontal(_pool));

    /// <summary>Mirrors top to bottom. Ends the current band pass.</summary>
    public ImagePipeline FlipVertical() => Geometry(src => src.FlipVertical(_pool));

    /// <summary>Brightness by <paramref name="shift"/>.</summary>
    public ImagePipeline Brightness(float shift = 0f) => Step(PlanOps.Brightness(shift));

    /// <summary>Contrast about the mid-point.</summary>
    public ImagePipeline Contrast(float gain = 1f) => Step(PlanOps.Contrast(gain));

    /// <summary>Gamma correction; above one darkens.</summary>
    public ImagePipeline Gamma(float gamma) => Step(PlanOps.Gamma(gamma));

    /// <summary>Inverts the colour channels.</summary>
    public ImagePipeline Invert() => Step(PlanOps.Invert());

    /// <summary>BT.601 luma.</summary>
    public ImagePipeline Grayscale(bool bt709 = false) => Step(PlanOps.Grayscale(bt709));

    /// <summary>Blends towards sepia.</summary>
    public ImagePipeline Sepia(float blend = 1f) => Step(PlanOps.Sepia(blend));

    /// <summary>Scales saturation about luma.</summary>
    public ImagePipeline Saturation(float gain = 1f, bool bt709 = false) => Step(PlanOps.Saturation(gain, bt709));

    /// <summary>Quantizes each channel to <paramref name="levels"/> steps.</summary>
    public ImagePipeline Posterize(int levels) => Step(PlanOps.Posterize(levels));

    /// <summary>Remaps the tone curve.</summary>
    public ImagePipeline Levels(float inputBlack, float inputWhite, float gamma = 1f,
        float outputBlack = 0f, float outputWhite = 1f) =>
        Step(PlanOps.Levels(inputBlack, inputWhite, gamma, outputBlack, outputWhite));

    /// <summary>Gaussian blur.</summary>
    public ImagePipeline GaussianBlur(float sigma) => Step(PlanOps.GaussianBlur(sigma));

    /// <summary>Box blur of the given radius.</summary>
    public ImagePipeline BoxBlur(int radius) => Step(PlanOps.BoxBlur(radius));

    /// <summary>Unsharp mask.</summary>
    public ImagePipeline Sharpen(float gain = 0.5f, float sigma = 1f) => Step(PlanOps.Sharpen(gain, sigma));

    /// <summary>3x3 emboss.</summary>
    public ImagePipeline Emboss() => Step(PlanOps.Emboss());

    /// <summary>3x3 Sobel magnitude.</summary>
    public ImagePipeline Sobel() => Step(PlanOps.Sobel());

    /// <summary>3x3 median.</summary>
    public ImagePipeline Median3x3() => Step(PlanOps.Median3x3());

    /// <summary>Convolution with an arbitrary odd kernel and any border mode.</summary>
    /// <param name="kernel">Weights, row-major, <c>(2r+1)²</c> of them.</param>
    /// <param name="radius">Kernel radius.</param>
    /// <param name="border">How to read outside the image.</param>
    /// <param name="bias">Added after the sum, in sample units.</param>
    public ImagePipeline Convolve(ReadOnlySpan<float> kernel, int radius,
        BorderMode border = BorderMode.Clamp, float bias = 0f, float constantValue = 0f) =>
        Step(PlanOps.Convolve(kernel, radius, border, bias, constantValue));

    /// <summary>Warm or cool shift.</summary>
    /// <param name="shift">Positive warms, negative cools.</param>
    public ImagePipeline Temperature(float shift = 0f) => Step(PlanOps.Temperature(shift));

    /// <summary>Tone curve through the given points.</summary>
    /// <param name="points">Control points, at least two.</param>
    public ImagePipeline Curves(params CurvePoint[] points) => Step(PlanOps.Curves(points));

    /// <summary>Saturation weighted towards already-saturated colours.</summary>
    /// <param name="lift">0 is unchanged, 0.5 is a clear lift, 1 is already strong. Leaves saturated colours alone.</param>
    /// <param name="bt709">Use BT.709 luma instead of BT.601.</param>
    public ImagePipeline Vibrance(float lift = 0f, bool bt709 = false) => Step(PlanOps.Vibrance(lift, bt709));

    /// <summary>Seeded grain; whole-image because the generator advances per pixel.</summary>
    /// <param name="amplitude">Amplitude in normalised units.</param>
    /// <param name="kind">Uniform or Gaussian.</param>
    /// <param name="seed">Sequence seed.</param>
    public ImagePipeline Noise(float amplitude = 0.05f, NoiseKind kind = NoiseKind.Gaussian, uint seed = 1) =>
        Step(PlanOps.Noise(amplitude, kind, seed));

    /// <summary>Unsharp mask built from a negative Gaussian.</summary>
    /// <param name="gain">Strength.</param>
    /// <param name="sigma">Gaussian sigma.</param>
    public ImagePipeline GaussianSharpen(float gain = 0.5f, float sigma = 1f) =>
        Step(PlanOps.GaussianSharpen(gain, sigma));

    /// <summary>Erosion or dilation with a square window of odd side <c>2r+1</c>.</summary>
    /// <param name="radius">Window radius; 1 is 3×3.</param>
    /// <param name="kind">Erode or dilate.</param>
    public ImagePipeline Morph(int radius, Morphology kind = Morphology.Erode) =>
        Step(PlanOps.Morph(radius, kind));

    /// <summary>Several per-channel point transforms collapsed into one table and one pass (8-bit).</summary>
    /// <param name="transforms">The transforms, applied in order.</param>
    public ImagePipeline PointChain(params PointOps.RowTransform[] transforms) =>
        Step(PlanOps.PointChain(transforms));

    /// <summary>Median with radius <paramref name="radius"/> (1 is 3×3, up to 8).</summary>
    /// <param name="radius">Window radius.</param>
    public ImagePipeline Median(int radius) => Step(PlanOps.Median(radius));

    /// <summary>3x3 erosion.</summary>
    public ImagePipeline Erode3x3() => Step(PlanOps.Erode3x3());

    /// <summary>3x3 dilation.</summary>
    public ImagePipeline Dilate3x3() => Step(PlanOps.Dilate3x3());

    /// <summary>Erosion then dilation.</summary>
    public ImagePipeline Open3x3() => Step(PlanOps.Open3x3());

    /// <summary>Dilation then erosion.</summary>
    public ImagePipeline Close3x3() => Step(PlanOps.Close3x3());

    /// <summary>Edge-preserving bilateral filter.</summary>
    public ImagePipeline Bilateral(int radius, float sigmaRange = 0.1f) =>
        Step(PlanOps.Bilateral(radius, sigmaRange));

    /// <summary>Directional blur.</summary>
    public ImagePipeline MotionBlur(int length, float angleDegrees = 0f) =>
        Step(PlanOps.MotionBlur(length, angleDegrees));

    /// <summary>Disc-shaped bokeh.</summary>
    public ImagePipeline BokehBlur(int radius) => Step(PlanOps.BokehBlur(radius));

    /// <summary>Guided filter.</summary>
    public ImagePipeline GuidedFilter(int radius, float epsilon = 1e-4f) =>
        Step(PlanOps.GuidedFilter(radius, epsilon));

    /// <summary>Non-local means.</summary>
    public ImagePipeline NonLocalMeans(int searchRadius, int patchRadius = 1, float h = 0.1f) =>
        Step(PlanOps.NonLocalMeans(searchRadius, patchRadius, h));

    /// <summary>Oil paint.</summary>
    public ImagePipeline OilPaint(int radius, int levels = 20) => Step(PlanOps.OilPaint(radius, levels));

    /// <summary>Vignette. Runs whole-image; costs one intermediate.</summary>
    public ImagePipeline Vignette(float radius = 0.75f, float darkening = 1f) =>
        Step(PlanOps.Vignette(radius, darkening));

    /// <summary>CLAHE. Runs whole-image; costs one intermediate.</summary>
    public ImagePipeline Clahe(int tilesX = 8, int tilesY = 8, float clipLimit = 3f) =>
        Step(PlanOps.Clahe(tilesX, tilesY, clipLimit));

    /// <summary>Local Laplacian. Runs whole-image; costs one intermediate.</summary>
    public ImagePipeline LocalLaplacian(int levels = 5, int samples = 32) =>
        Step(PlanOps.LocalLaplacian(levels, samples));

    /// <summary>Per-channel histogram stretch. Runs whole-image.</summary>
    public ImagePipeline Normalize() => Step(PlanOps.Normalize());

    /// <summary>Adaptive threshold. Runs whole-image.</summary>
    public ImagePipeline AdaptiveThreshold(int radius = 8, float offset = 0.05f) =>
        Step(PlanOps.AdaptiveThreshold(radius, offset));

    /// <summary>Block mosaic.</summary>
    public ImagePipeline Pixelate(int blockSize) => Step(PlanOps.Pixelate(blockSize));

    /// <summary>Removes haze. Runs whole-image; costs two single-channel planes.</summary>
    public ImagePipeline Dehaze(int patchRadius = 7, float omega = 0.95f, float tMin = 0.1f, int refineRadius = 20) =>
        Step(PlanOps.Dehaze(patchRadius, omega, tMin, refineRadius));

    /// <summary>Histogram equalisation.</summary>
    public ImagePipeline Equalize() => Step(PlanOps.Equalize());

    /// <summary>
    /// Runs the chain and returns a new image. The source is not modified; the result belongs to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A terminal call is unavoidable: the chain has to stay lazy until the last step is added, and only the
    /// caller knows when that is. It is named for what it returns rather than for what it does, the way
    /// <c>ToArray</c> ends a LINQ query — <c>Run</c> reads like it returns nothing.
    /// </para>
    /// <para>
    /// <b>Dispose the result.</b> It holds a buffer leased from a pool, and nothing in this library has a
    /// finalizer, so <c>Dispose</c> is the only thing that gives that buffer back. An image that is dropped
    /// instead is still collected by the GC, but the pool never sees it again: the next call rents a fresh
    /// buffer, and at 8K that is 132 MiB a time.
    /// </para>
    /// <para>
    /// The result is always an image of its own, including when no steps were added — an identity pipeline copies.
    /// Returning the source would make the documented dispose destroy the image the caller started from.
    /// </para>
    /// </remarks>
    public Image ToImage()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;

        // A pipeline with nothing to do still owes the caller a picture of their own. Returning the source made
        // `using var copy = image.Pipe().ToImage()` hand back the very image it was copying — and the dispose at
        // the end of that using then returned the source's buffer to the pool while the caller kept using it.
        // A copy costs one frame; an aliased one costs the caller their image.
        var result = HasWork ? Current() : _source.Clone();
        _current = null;
        return result;
    }

    /// <summary>Whether any step has been recorded or run; false means the pipeline is the source.</summary>
    internal bool HasWork => _current is not null || _pending.Count > 0;

    private ImagePipeline Step(ImageOp op)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _pending.Add(op);
        return this;
    }

    /// <summary>Ends the current pass, applies geometry, and starts a fresh pass on the result.</summary>
    private ImagePipeline Geometry(Func<Image, Image> reshape)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var input = Current();
        var reshaped = reshape(input);

        // The pass just ended: its output is no longer reachable and is not the caller's source, so it goes back
        // to the pool now rather than at the next collection. Reshape returns a new image, never its input.
        if (!ReferenceEquals(input, _source))
            input.Dispose();

        _current = reshaped;
        return this;
    }

    /// <summary>The image as of the last completed pass, running the pending plan first.</summary>
    private Image Current()
    {
        if (_pending.Count == 0)
            return _current ?? _source;

        var plan = new ProcessingPlan { BandRows = _bandRows };
        foreach (var op in _pending)
            plan.Add(op);
        _pending.Clear();

        var input = _current ?? _source;
        var next = input.Apply(plan, _pool);
        if (!ReferenceEquals(input, _source))
            input.Dispose();

        _current = next;
        return next;
    }
}

/// <summary>Starts a pipeline.</summary>
public static class ImagePipelineExtensions
{
    /// <summary>
    /// Starts a pipeline over <paramref name="image"/>. Operations chain into passes, splitting automatically where
    /// the size changes.
    /// </summary>
    /// <param name="image">The first image; the pipeline reads it lazily.</param>
    /// <param name="pool">Where the pipeline's buffers are rented from; null uses the shared pool.</param>
    public static ImagePipeline Pipe(this Image image, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new ImagePipeline(image, pool);
    }
}
