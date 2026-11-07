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
}
