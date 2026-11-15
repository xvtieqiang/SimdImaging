using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>
/// Chains same-size operations so the image is walked once instead of once per step.
/// </summary>
/// <remarks>
/// <para>
/// The plain call is <c>image.A()</c> then <c>b.A()</c> then <c>c.A()</c>, and each link allocates a whole new
/// image — three of them alive at 8K is 396 MiB. A pipeline walks the picture in horizontal bands instead: read a
/// band, run every step on it, write it out, move on. No intermediate image exists, so a five-step chain at 8K
/// costs 72 KiB instead of 381 MiB.
/// </para>
/// <para>
/// Everything the executor needs to know about an operator — how far it reads beyond the band, whether it needs
/// the whole picture — travels with the operator. That is why none of it appears here: <see cref="Sharpen"/>
/// knows it blurs internally, and <see cref="Pixelate"/> knows it cannot be walked in bands at all.
/// </para>
/// <para>
/// Steps must keep the image size. A resize, crop or rotate changes the dimensions and belongs between two
/// pipelines, not inside one.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var result = image.Pipe()
///     .Brightness(0.05f)
///     .Sharpen()
///     .Run();
/// </code>
/// </example>
public sealed class ImagePipeline
{
    private readonly Image _source;
    private readonly ProcessingPlan _plan = new();

    internal ImagePipeline(Image source) => _source = source;

    /// <summary>Rows per band; smaller bands mean less working memory and more bookkeeping.</summary>
    public ImagePipeline BandRows(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        return new ImagePipeline(_source, rows);
    }

    private ImagePipeline(Image source, int bandRows)
    {
        _source = source;
        _plan = new ProcessingPlan { BandRows = bandRows };
    }

    /// <summary>Brightness by <paramref name="amount"/>.</summary>
    public ImagePipeline Brightness(float amount) => Step(ImageOps.Brightness(amount));

    /// <summary>Contrast about the mid-point.</summary>
    public ImagePipeline Contrast(float amount) => Step(ImageOps.Contrast(amount));

    /// <summary>Gamma correction; above one darkens.</summary>
    public ImagePipeline Gamma(float gamma) => Step(ImageOps.Gamma(gamma));

    /// <summary>Inverts the colour channels.</summary>
    public ImagePipeline Invert() => Step(ImageOps.Invert());

    /// <summary>BT.601 luma.</summary>
    public ImagePipeline Grayscale() => Step(ImageOps.Grayscale());

    /// <summary>Blends towards sepia.</summary>
    public ImagePipeline Sepia(float amount = 1f) => Step(ImageOps.Sepia(amount));

    /// <summary>Scales saturation about luma.</summary>
    public ImagePipeline Saturation(float amount) => Step(ImageOps.Saturation(amount));

    /// <summary>Quantizes each channel to <paramref name="levels"/> steps.</summary>
    public ImagePipeline Posterize(int levels) => Step(ImageOps.Posterize(levels));

    /// <summary>Remaps the tone curve.</summary>
    public ImagePipeline Levels(float black, float white, float gamma) => Step(ImageOps.Levels(black, white, gamma));

    /// <summary>Gaussian blur.</summary>
    public ImagePipeline GaussianBlur(float sigma) => Step(ImageOps.GaussianBlur(sigma));

    /// <summary>Box blur of the given radius.</summary>
    public ImagePipeline BoxBlur(int radius) => Step(ImageOps.BoxBlur(radius));

    /// <summary>Unsharp mask.</summary>
    public ImagePipeline Sharpen(float amount = 0.5f, float sigma = 1f) => Step(ImageOps.Sharpen(amount, sigma));

    /// <summary>3x3 emboss.</summary>
    public ImagePipeline Emboss() => Step(ImageOps.Emboss());

    /// <summary>3x3 Sobel magnitude.</summary>
    public ImagePipeline Sobel() => Step(ImageOps.Sobel());

    /// <summary>Block mosaic.</summary>
    public ImagePipeline Pixelate(int blockSize) => Step(ImageOps.Pixelate(blockSize));

    /// <summary>Histogram equalisation.</summary>
    public ImagePipeline Equalize() => Step(ImageOps.Equalize());

    /// <summary>Runs the chain and returns a new image; the source is not modified.</summary>
    public Image Run() => _source.Apply(_plan);

    private ImagePipeline Step(ImageOp op)
    {
        _plan.Add(op);
        return this;
    }
}

/// <summary>Starts a pipeline.</summary>
public static class ImagePipelineExtensions
{
    /// <summary>Chains same-size operations into a single pass over the image.</summary>
    public static ImagePipeline Pipe(this Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new ImagePipeline(image);
    }
}
