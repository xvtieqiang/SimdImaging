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
    private readonly List<ImageOp> _pending = [];
    private Image? _current;
    private int _bandRows = ProcessingPlan.DefaultBandRows;
    private bool _finished;

    internal ImagePipeline(Image source) => _source = source;

    /// <summary>Rows per band; smaller bands trade bookkeeping for less working memory.</summary>
    public ImagePipeline BandRows(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        _bandRows = rows;
        return this;
    }

    /// <summary>Resizes. Ends the current band pass and continues with a new one.</summary>
    public ImagePipeline Resize(int width, int height, Resampler resampler = Resampler.Bicubic) =>
        Geometry(src => src.Resize(width, height, resampler));

    /// <summary>Crops to <paramref name="region"/>. Ends the current band pass.</summary>
    public ImagePipeline Crop(PixelRect region) => Geometry(src => src.Crop(region));

    /// <summary>Rotates by a quarter turn. Ends the current band pass.</summary>
    public ImagePipeline Rotate90(bool clockwise = true) => Geometry(src => src.Rotate90(clockwise));

    /// <summary>Brightness by <paramref name="amount"/>.</summary>
    public ImagePipeline Brightness(float amount) => Step(PlanOps.Brightness(amount));

    /// <summary>Contrast about the mid-point.</summary>
    public ImagePipeline Contrast(float amount) => Step(PlanOps.Contrast(amount));

    /// <summary>Gamma correction; above one darkens.</summary>
    public ImagePipeline Gamma(float gamma) => Step(PlanOps.Gamma(gamma));

    /// <summary>Inverts the colour channels.</summary>
    public ImagePipeline Invert() => Step(PlanOps.Invert());

    /// <summary>BT.601 luma.</summary>
    public ImagePipeline Grayscale() => Step(PlanOps.Grayscale());

    /// <summary>Blends towards sepia.</summary>
    public ImagePipeline Sepia(float amount = 1f) => Step(PlanOps.Sepia(amount));

    /// <summary>Scales saturation about luma.</summary>
    public ImagePipeline Saturation(float amount) => Step(PlanOps.Saturation(amount));

    /// <summary>Quantizes each channel to <paramref name="levels"/> steps.</summary>
    public ImagePipeline Posterize(int levels) => Step(PlanOps.Posterize(levels));

    /// <summary>Remaps the tone curve.</summary>
    public ImagePipeline Levels(float black, float white, float gamma) => Step(PlanOps.Levels(black, white, gamma));

    /// <summary>Gaussian blur.</summary>
    public ImagePipeline GaussianBlur(float sigma) => Step(PlanOps.GaussianBlur(sigma));

    /// <summary>Box blur of the given radius.</summary>
    public ImagePipeline BoxBlur(int radius) => Step(PlanOps.BoxBlur(radius));

    /// <summary>Unsharp mask.</summary>
    public ImagePipeline Sharpen(float amount = 0.5f, float sigma = 1f) => Step(PlanOps.Sharpen(amount, sigma));

    /// <summary>3x3 emboss.</summary>
    public ImagePipeline Emboss() => Step(PlanOps.Emboss());

    /// <summary>3x3 Sobel magnitude.</summary>
    public ImagePipeline Sobel() => Step(PlanOps.Sobel());

    /// <summary>3x3 median.</summary>
    public ImagePipeline Median3x3() => Step(PlanOps.Median3x3());

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
    public ImagePipeline Vignette(float radius = 0.75f, float strength = 1f) =>
        Step(PlanOps.Vignette(radius, strength));

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
    /// </remarks>
    public Image ToImage()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;
        var result = Current();
        _current = null;
        return result;
    }

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
        _current = reshape(input);
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
        var next = input.Apply(plan);
        if (!ReferenceEquals(input, _source))
            input.Dispose();

        _current = next;
        return next;
    }
}

/// <summary>Starts a pipeline.</summary>
public static class ImagePipelineExtensions
{
    /// <summary>Chains operations into passes, splitting automatically where the size changes.</summary>
    public static ImagePipeline Pipe(this Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new ImagePipeline(image);
    }
}
