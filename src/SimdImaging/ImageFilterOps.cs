using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Separable low-pass filters.</summary>
public sealed partial class Image
{
    /// <summary>A box blur of the given radius, in pixels.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative.</exception>
    public Image BoxBlur(int radius, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.BoxBlur(this[0].Pixels.View, radius, pool));

    /// <summary>A true Gaussian blur with the given sigma.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is not finite and positive.</exception>
    public Image GaussianBlur(float sigma, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.GaussianBlur(this[0].Pixels.View, sigma, pool));

    /// <summary>A 3×3 median per channel, edges replicated.</summary>
    public Image Median3x3(IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Median3x3(this[0].Pixels.View, pool));

    /// <summary>Unsharp mask; <paramref name="amount"/> 0 is identity.</summary>
    public Image Sharpen(float amount = 0.5f, float sigma = 1f, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Sharpen(this[0].Pixels.View, amount, sigma, pool));

    /// <summary>Gaussian sharpen: negative Gaussian shell around a boosted centre, applied as one kernel.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is not finite and positive.</exception>
    public Image GaussianSharpen(float amount = 0.5f, float sigma = 1f, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.GaussianSharpen(this[0].Pixels.View, amount, sigma, pool));

    /// <summary>3×3 convolution on colour channels; alpha is preserved.</summary>
    /// <exception cref="ArgumentException">The kernel is not nine values.</exception>
    public Image Convolve3x3(ReadOnlySpan<float> kernel, float bias = 0f, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Convolve3x3(this[0].Pixels.View, kernel, bias, pool));

    /// <summary>Emboss relief; flat areas become mid-grey.</summary>
    public Image Emboss(float strength = 1f, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Emboss(this[0].Pixels.View, strength, pool));

    /// <summary>Motion blur along a line; <paramref name="length"/> 1 is a no-op.</summary>
    public Image MotionBlur(int length = 9, float angleDegrees = 0f, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.MotionBlur(this[0].Pixels.View, length, angleDegrees, pool));

    /// <summary>Bokeh (disc) blur of the given radius.</summary>
    public Image BokehBlur(int radius = 4, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.BokehBlur(this[0].Pixels.View, radius, pool));

    /// <summary>Edge-preserving bilateral filter; <paramref name="sigmaSpace"/> 0 derives <c>radius / 2</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The radius or a sigma is not positive.</exception>
    public Image Bilateral(int radius = 2, float sigmaSpace = 0f, float sigmaRange = 0.1f, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Bilateral(this[0].Pixels.View, radius, sigmaSpace, sigmaRange, pool));

    /// <summary>3×3 erosion: per-channel minimum.</summary>
    public Image Erode3x3(IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Erode3x3(this[0].Pixels.View, pool));

    /// <summary>3×3 dilation: per-channel maximum.</summary>
    public Image Dilate3x3(IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Dilate3x3(this[0].Pixels.View, pool));

    /// <summary>Erosion then dilation: removes small bright specks.</summary>
    public Image Open3x3(IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Open3x3(this[0].Pixels.View, pool));

    /// <summary>Dilation then erosion: fills small dark holes.</summary>
    public Image Close3x3(IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Close3x3(this[0].Pixels.View, pool));

    /// <summary>Sobel gradient magnitude of the luma, written to RGB; alpha is preserved.</summary>
    public Image Sobel(bool bt709 = false, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Sobel(this[0].Pixels.View, bt709, pool));

    /// <summary>Pixelates into square blocks of the given edge length; 1 is a copy.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not positive.</exception>
    public Image Pixelate(int blockSize = 8, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Pixelate(this[0].Pixels.View, blockSize, pool));

    /// <summary>Radial darkening toward the corners.</summary>
    /// <param name="radius">Where the falloff starts, as a fraction of the half-diagonal (0..1).</param>
    /// <param name="strength">Corner darkening, 0..1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The radius or strength is outside its range.</exception>
    public Image Vignette(float radius = 0.75f, float strength = 1f, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Vignette(this[0].Pixels.View, radius, strength, pool));

    /// <summary>Highlights above <paramref name="threshold"/> are blurred and added back as a halo.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The threshold, sigma or amount is out of range.</exception>
    public Image Glow(float threshold = 0.7f, float sigma = 8f, float amount = 0.6f, IPixelPool? pool = null) =>
        ReplaceBuffer(FilterOps.Glow(this[0].Pixels.View, threshold, sigma, amount, pool));

    /// <summary>Oil paint: each pixel takes the mean colour of its neighbourhood's dominant intensity.</summary>
    /// <param name="radius">Neighbourhood radius in pixels.</param>
    /// <param name="levels">Intensity bins; fewer bins means broader patches.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The radius or level count is out of range.</exception>
    public Image OilPaint(int radius = 2, int levels = 20, IPixelPool? pool = null) =>
        ReplaceBuffer(OilPaintOps.OilPaint(this[0].Pixels.View, radius, levels, pool));

    /// <summary>Contrast-limited adaptive histogram equalisation.</summary>
    /// <param name="tilesX">Tiles across.</param>
    /// <param name="tilesY">Tiles down.</param>
    /// <param name="clipLimit">Histogram clip as a multiple of the tile's average bin count.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A tile count or the clip limit is out of range.</exception>
    public Image Clahe(int tilesX = 8, int tilesY = 8, float clipLimit = 3f, IPixelPool? pool = null) =>
        ReplaceBuffer(ClaheOps.Clahe(this[0].Pixels.View, tilesX, tilesY, clipLimit, pool));

    /// <summary>Adds reproducible noise; the same seed always produces the same image.</summary>
    /// <param name="amount">Amplitude in normalised units, 0..1.</param>
    /// <param name="kind">Uniform or Gaussian.</param>
    /// <param name="seed">Sequence seed.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public Image Noise(float amount = 0.05f, NoiseKind kind = NoiseKind.Gaussian, uint seed = 1,
        IPixelPool? pool = null) =>
        ReplaceBuffer(NoiseOps.Noise(this[0].Pixels.View, amount, kind, seed, pool));

    /// <summary>Edge-preserving smoothing: flat areas average, edges stay.</summary>
    /// <param name="radius">Box radius in pixels.</param>
    /// <param name="epsilon">Variance regulariser; larger smooths more.</param>
    /// <param name="bandRows">Rows per band; bounds the working set.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The radius, epsilon or band size is out of range.</exception>
    public Image GuidedFilter(int radius = 4, float epsilon = 1e-4f, int bandRows = 128, IPixelPool? pool = null) =>
        ReplaceBuffer(GuidedFilterOps.GuidedFilter(this[0].Pixels.View, radius, epsilon, bandRows, pool));

    /// <summary>Guided filter with a separate guide: the guide's structure decides where edges are kept, the
    /// image supplies the colours.</summary>
    /// <param name="guide">Same size as this image; channel <c>c</c> of the guide filters channel <c>c</c>, so a
    /// grey guide acts as one scalar guide for all three.</param>
    /// <param name="radius">Box radius in pixels.</param>
    /// <param name="epsilon">Variance regulariser; larger smooths more.</param>
    /// <param name="bandRows">Rows per band; bounds the working set.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentNullException"><paramref name="guide"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="guide"/> is a different size.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The radius, epsilon or band size is out of range.</exception>
    public Image GuidedFilter(Image guide, int radius = 4, float epsilon = 1e-4f, int bandRows = 128,
        IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(guide);
        return ReplaceBuffer(GuidedFilterOps.GuidedFilter(this[0].Pixels.View, guide[0].Pixels.View,
            radius, epsilon, bandRows, pool));
    }

    /// <summary>Non-local means denoising: exact patch-similarity average, no pruning.</summary>
    /// <param name="searchRadius">Half-width of the search window.</param>
    /// <param name="patchRadius">Half-width of the comparison patch.</param>
    /// <param name="h">Filtering strength in sample units; 0.1 ≈ 10% of the range.</param>
    /// <param name="sigma">Expected noise standard deviation for the <c>2σ²</c> weight correction; 0 disables it.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A radius is negative, or <paramref name="h"/> /
    /// <paramref name="sigma"/> is out of range.</exception>
    public Image NonLocalMeans(int searchRadius = 3, int patchRadius = 1, float h = 0.1f, float sigma = 0f,
        IPixelPool? pool = null) =>
        ReplaceBuffer(NonLocalMeansOps.NonLocalMeans(this[0].Pixels.View, searchRadius, patchRadius, h, sigma, pool));

    /// <summary>Removes haze with the dark-channel prior.</summary>
    /// <param name="patchRadius">Patch the dark channel and transmission are taken over.</param>
    /// <param name="omega">How much haze to remove; 0.95 keeps a little depth, 1 removes it all.</param>
    /// <param name="tMin">Floor on transmission, which bounds the amplification to <c>1/tMin</c>.</param>
    /// <param name="refineRadius">Box radius that smooths the transmission map; 0 leaves the blocky map.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public Image Dehaze(int patchRadius = 7, float omega = 0.95f, float tMin = 0.1f, int refineRadius = 20,
        IPixelPool? pool = null) =>
        ReplaceBuffer(DehazeOps.Dehaze(this[0].Pixels.View, patchRadius, omega, tMin, refineRadius, pool));

    /// <summary>Local Laplacian detail enhancement; <c>alpha = beta = 1</c> is the identity.</summary>
    /// <param name="levels">Laplacian levels; clamped so the coarsest level stays usable.</param>
    /// <param name="samples">How many intensities the remap is evaluated at.</param>
    /// <param name="alpha">Detail exponent inside <c>±sigmaR</c>; below 1 amplifies detail.</param>
    /// <param name="beta">Edge amplitude beyond <c>sigmaR</c>.</param>
    /// <param name="sigmaR">Range split between detail and edge.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of range.</exception>
    public Image LocalLaplacian(int levels = 5, int samples = 32, float alpha = 0.25f, float beta = 1f,
        float sigmaR = 0.15f, IPixelPool? pool = null) =>
        ReplaceBuffer(LocalLaplacianOps.LocalLaplacian(this[0].Pixels.View, levels, samples, alpha, beta, sigmaR, pool));

    /// <summary>Richardson–Lucy deconvolution of a Gaussian blur; runs on straight RGB and leaves alpha
    /// untouched.</summary>
    /// <param name="sigma">Standard deviation of the blur being undone, in pixels.</param>
    /// <param name="iterations">Iterations; the default is the measured PSNR peak for typical content.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is not finite and positive, or
    /// <paramref name="iterations"/> is negative.</exception>
    public Image RichardsonLucy(float sigma, int iterations = 10, IPixelPool? pool = null) =>
        ReplaceBuffer(RichardsonLucyOps.RichardsonLucy(this[0].Pixels.View, sigma, iterations, pool));

    /// <summary>Richardson–Lucy deconvolution of a linear motion blur.</summary>
    /// <param name="length">Trail length in pixels; 1 is the identity.</param>
    /// <param name="angleDegrees">Trail direction, clockwise from +x.</param>
    /// <param name="iterations">Iterations.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive or the angle is
    /// not finite.</exception>
    public Image RichardsonLucyMotion(int length, float angleDegrees, int iterations = 10,
        IPixelPool? pool = null) =>
        ReplaceBuffer(RichardsonLucyOps.RichardsonLucyMotion(this[0].Pixels.View, length, angleDegrees,
            iterations, pool));
}
