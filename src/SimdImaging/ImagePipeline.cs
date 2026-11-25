using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
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
public sealed class ImagePipeline : ImageOpPipeline<ImagePipeline>
{
    private readonly Image _source;
    private readonly IPixelPool? _pool;
    private PipelineRecipeBuilder _recipe = new();
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
        AppendGeometry(GeometrySteps.Resize(width, height, resampler));

    /// <summary>Resize with an explicit mode, anchor and background.</summary>
    public ImagePipeline Resize(int width, int height, ResizeMode mode, Anchor anchor = Anchor.Centre,
        Resampler resampler = Resampler.Bicubic, RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.Resize(width, height, mode, anchor, resampler, background));

    /// <summary>Crops to <paramref name="region"/>. Ends the current band pass.</summary>
    public ImagePipeline Crop(PixelRect region) => AppendGeometry(GeometrySteps.Crop(region));

    /// <summary>Rotates by a quarter turn. Ends the current band pass.</summary>
    public ImagePipeline Rotate90(bool clockwise = true) => AppendGeometry(GeometrySteps.Rotate90(clockwise));

    /// <summary>Rotates by a half turn. Ends the current band pass.</summary>
    public ImagePipeline Rotate180() => AppendGeometry(GeometrySteps.Rotate180());

    /// <summary>Mirrors left to right. Ends the current band pass.</summary>
    public ImagePipeline FlipHorizontal() => AppendGeometry(GeometrySteps.FlipHorizontal());

    /// <summary>Mirrors top to bottom. Ends the current band pass.</summary>
    public ImagePipeline FlipVertical() => AppendGeometry(GeometrySteps.FlipVertical());






























    /// <summary>Edge-preserving bilateral filter.</summary>
    public ImagePipeline Bilateral(int radius = 2, float sigmaSpace = 0f, float sigmaRange = 0.1f) =>
        Reshape(v => FilterOps.Bilateral(v, radius, sigmaSpace, sigmaRange, _pool));



    /// <summary>Guided filter.</summary>
    public ImagePipeline GuidedFilter(int radius = 4, float epsilon = 1e-4f, int bandRows = 128) =>
        Step(PlanOps.GuidedFilter(radius, epsilon));

    /// <summary>Guided filter with a caller-supplied guide image.</summary>
    public ImagePipeline GuidedFilter(Image guide, int radius = 4, float epsilon = 1e-4f, int bandRows = 128)
    {
        ArgumentNullException.ThrowIfNull(guide);
        return Reshape(v => GuidedFilterOps.GuidedFilter(v, guide[0].Pixels.View, radius, epsilon, bandRows, _pool));
    }





    /// <summary>Local Laplacian. Runs whole-image; costs one intermediate.</summary>
    public ImagePipeline LocalLaplacian(int levels = 5, int samples = 32, float alpha = 0.25f, float beta = 1f,
        float sigmaR = 0.15f) =>
        Reshape(v => LocalLaplacianOps.LocalLaplacian(v, levels, samples, alpha, beta, sigmaR, _pool));






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
        var result = HasWork ? Flush() : _source.Clone();
        _current = null;
        return result;
    }

    /// <summary>Whether any step has been recorded or run; false means the pipeline is the source.</summary>
    internal bool HasWork => _current is not null || !_recipe.IsEmpty;

    protected override ImagePipeline Step(ImageOp op)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _recipe.Append(op);
        return this;
    }

    /// <summary>Appends a geometry change. It is executed when the chain is flushed, not here; the compiler decides
    /// where the boundary falls, and the two facts it needs (the new size and the row form) travel with the step.</summary>
    private ImagePipeline AppendGeometry(GeometryDescriptor geometry)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _recipe.Append(geometry);
        return this;
    }

    /// <summary>
    /// Ends the current pass, runs a whole-image operator over its result, and starts a fresh pass on that. The
    /// counterpart of <see cref="Geometry"/> for operators that take a view and return a buffer: it is what lets the
    /// pipeline call the operator directly instead of calling back into the facade.
    /// </summary>
    private ImagePipeline Reshape(Func<PixelView, WorkingBuffer> reshape)
    {
        ObjectDisposedException.ThrowIf(_finished, this);

        // A whole-image operator that is not a plan step is an explicit barrier: flush the recipe into an image,
        // run the operator, and continue from its result. Geometry steps do not come through here any more — they
        // stay in the recipe until ToImage, so the compiler decides the boundary.
        var input = Flush();
        var reshaped = input.ReplaceBuffer(reshape(input[0].Pixels.View));
        if (!ReferenceEquals(input, _source))
            input.Dispose();

        _current = reshaped;
        return this;
    }

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline DistanceTransform(float threshold = 0.5f) =>
        Reshape(v => DistanceTransformOps.SquaredEuclidean(v, threshold, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ResizeCanvas(int width, int height, Anchor anchor = Anchor.Centre, RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.ResizeCanvas(width, height, anchor, background));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Tile(int width, int height, bool mirror = false) =>
        AppendGeometry(GeometrySteps.Tile(width, height, mirror));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Trim(float tolerance = 0f) =>
        AppendGeometry(GeometrySteps.Trim(tolerance));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline RoundCorners(float radius) =>
        AppendGeometry(GeometrySteps.RoundCorners(radius));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Transpose(bool anti = false) =>
        AppendGeometry(GeometrySteps.Transpose(anti));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline RotateFlip(int quarterTurns, bool flipHorizontal = false) =>
        AppendGeometry(GeometrySteps.RotateFlip(quarterTurns, flipHorizontal));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline EntropyCrop(float threshold = 0.25f) =>
        AppendGeometry(GeometrySteps.EntropyCrop(threshold));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline RemoveSeams(SeamAxis axis, int count) =>
        AppendGeometry(GeometrySteps.RemoveSeams(axis, count));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline InsertSeams(SeamAxis axis, int count) =>
        AppendGeometry(GeometrySteps.InsertSeams(axis, count));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline SeamCarve(int width, int height) =>
        AppendGeometry(GeometrySteps.SeamCarve(width, height));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ConvertColorSpace(ColorSpace target, YCbCrStandard ycbcr = YCbCrStandard.Bt709, bool limitedRange = false, WhitePoint white = WhitePoint.D65, ChromaticAdaptation adaptation = ChromaticAdaptation.Bradford) =>
        Reshape(v => ColorConversionOps.Convert(v, target, ycbcr, limitedRange, white, adaptation, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline IccTransform(ReadOnlySpan<byte> sourceProfile, ReadOnlySpan<byte> destinationProfile = default)
    {
        var from = sourceProfile.ToArray();
        var to = destinationProfile.ToArray();
        return Reshape(v => IccTransformOps.Transform(v, from, to, _pool));
    }

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ChromaResample(ChromaSubsampling mode, YCbCrStandard ycbcr = YCbCrStandard.Bt601, bool limitedRange = false, int bandRows = 128) =>
        Reshape(v => ColorConversionOps.ChromaResample(v, mode, ycbcr, limitedRange, bandRows, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Convolve3x3(ReadOnlySpan<float> kernel, float bias = 0f)
    {
        var copy = kernel.ToArray();
        return Reshape(v => FilterOps.Convolve3x3(v, copy, bias, _pool));
    }

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Prewitt(bool bt709 = false) =>
        Reshape(v => FilterOps.Prewitt(v, bt709, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Scharr(bool bt709 = false) =>
        Reshape(v => FilterOps.Scharr(v, bt709, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Laplacian(bool bt709 = false) =>
        Reshape(v => FilterOps.Laplacian(v, bt709, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Glow(float threshold = 0.7f, float sigma = 8f, float amount = 0.6f) =>
        Reshape(v => FilterOps.Glow(v, threshold, sigma, amount, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline RichardsonLucy(float sigma, int iterations = 10) =>
        Reshape(v => RichardsonLucyOps.RichardsonLucy(v, sigma, iterations, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline RichardsonLucyMotion(int length, float angleDegrees, int iterations = 10) =>
        Reshape(v => RichardsonLucyOps.RichardsonLucyMotion(v, length, angleDegrees, iterations, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Perspective(ReadOnlySpan<float> homography, int width, int height, RgbaColor background = default)
    {
        var copy = homography.ToArray();
        return Reshape(v => Mapping.Perspective(v, copy, width, height, background, _pool));
    }

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline PerspectiveWithOptions(ReadOnlySpan<float> homography, int width, int height,
        Resampler resampler, SampleOptions options, RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.PerspectiveWithOptions(homography, width, height, resampler, options, background));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Affine(AffineMatrix forward, int width, int height, RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.Affine(forward, width, height, background));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline AffineWithOptions(AffineMatrix forward, int width, int height, Resampler resampler, SampleOptions options, RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.AffineWithOptions(forward, width, height, resampler, options, background));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ToneMap(ToneMapCurve curve = ToneMapCurve.Aces, float exposure = 0f, float whitePoint = 1f, bool autoExposure = false, float targetLuminance = 0.18f) =>
        Reshape(v => HdrOps.ToneMap(v, curve, exposure, whitePoint, autoExposure, targetLuminance, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline EqualizeHistogram() =>
        Reshape(v => HistogramOps.Equalize(v, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline AutoOrient() =>
        Geometry(image => image.ReplaceBuffer(
            Orientation.AutoOrient(image[0].Pixels.View, image.Info.Orientation, _pool),
            image.Metadata,
            ImageOrientation.Normal));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Pad(int margin, RgbaColor background) =>
        AppendGeometry(GeometrySteps.Pad(margin, background));

    public ImagePipeline Pad(int left, int top, int right, int bottom, RgbaColor background) =>
        AppendGeometry(GeometrySteps.Pad(left, top, right, bottom, background));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Warp(int width, int height, PixelWarp map, Resampler resampler = Resampler.Bilinear, RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.Warp(width, height, map, resampler, background));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline WarpWithOptions(int width, int height, PixelWarp map, Resampler resampler, SampleOptions options, RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.WarpWithOptions(width, height, map, resampler, options, background));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ResizeWithOptions(int width, int height, Resampler resampler, SampleOptions options, RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.ResizeWithOptions(width, height, resampler, options, background));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Premultiply() =>
        Reshape(v => AlphaOps.Premultiply(v, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Unpremultiply() =>
        Reshape(v => AlphaOps.Unpremultiply(v, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ColorMatrix(ReadOnlySpan<float> matrix)
    {
        var copy = matrix.ToArray();
        return Reshape(v => PointOps.ColorMatrix(v, copy, _pool));
    }

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline HueRotate(float degrees) =>
        Reshape(v => PointOps.HueRotate(v, degrees, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline AlphaThreshold(float threshold = 0.5f) =>
        Reshape(v => PointOps.AlphaThreshold(v, threshold, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Tint(RgbaColor tint, float amount = 0.5f) =>
        Reshape(v => PointOps.Tint(v, tint, amount, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ColorBalance(ColorBalanceShifts shadows, ColorBalanceShifts midtones, ColorBalanceShifts highlights, bool bt709 = false) =>
        Reshape(v => PointOps.ColorBalance(v, shadows, midtones, highlights, bt709, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ChannelSwap(int redSource, int greenSource, int blueSource) =>
        Reshape(v => PointOps.ChannelSwap(v, redSource, greenSource, blueSource, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline BlackWhite(float threshold = 0.5f, bool bt709 = false) =>
        Reshape(v => PointOps.BlackWhite(v, threshold, bt709, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Clamp(float low, float high) =>
        Reshape(v => PointOps.Clamp(v, low, high, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Opacity(float opacity) =>
        Reshape(v => PointOps.Opacity(v, opacity, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Lightness(float factor) =>
        Reshape(v => PointOps.Lightness(v, factor, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Threshold(float threshold, bool bt709 = false) =>
        Reshape(v => PointOps.Threshold(v, threshold, bt709, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Linearize(TransferCurve curve = TransferCurve.Srgb) =>
        Reshape(v => TransferOps.Linearize(v, curve, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Delinearize(TransferCurve curve = TransferCurve.Srgb) =>
        Reshape(v => TransferOps.Delinearize(v, curve, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ReplaceBackground(RgbaColor background, RgbaColor replacement, float tolerance = 0.1f, float feather = 0.05f) =>
        Reshape(v => BackgroundOps.ReplaceBackground(v, background, replacement, tolerance, feather, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline ColorBlindness(ColorVisionDeficiency kind = ColorVisionDeficiency.Protanopia, float severity = 1f, bool correct = false) =>
        Reshape(v => ColorBlindnessOps.ColorBlindness(v, kind, severity, correct, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline Dither(int bits, DitherKernel kernel = DitherKernel.FloydSteinberg) =>
        Reshape(v => DitherOps.Dither(v, bits, kernel, _pool));

    /// <summary>The same operator as the facade, as a pipeline step.</summary>
    public ImagePipeline QuantizeWebSafe() =>
        Reshape(v => PaletteQuantizer.Apply(v, PaletteQuantizer.WebSafe, _pool));

    // The block-bodied operators. They are written against the view where they can be and delegate where the logic
    // needs the image's own size or metadata; deleting the facade methods means finishing this list.

    /// <summary>Alpha-composites another image over this one.</summary>
    public ImagePipeline Composite(Image overlay, int x, int y, PorterDuff mode = PorterDuff.SourceOver)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return Reshape(v =>
        {
            if (v.Format != overlay.Format)
                throw new ArgumentException($"The overlay is {overlay.Format} but this image is {v.Format}.", nameof(overlay));

            return mode == PorterDuff.SourceOver
                ? AlphaOps.Composite(v, overlay[0].Pixels.View, x, y, _pool)
                : AlphaOps.Composite(v, overlay[0].Pixels.View, x, y, mode, _pool);
        });
    }

    /// <summary>Blends another image over this one at an offset.</summary>
    public ImagePipeline Blend(Image overlay, int x, int y, BlendMode mode) =>
        Reshape(v => Processing.Blend.Apply(v, overlay[0].Pixels.View, x, y, mode, _pool));

    /// <summary>Applies a gain map, as an HDR image carries alongside its base.</summary>
    public ImagePipeline ApplyGainMap(Image gainMap, float headroom = 4f, float weight = 1f) =>
        Reshape(v => HdrOps.ApplyGainMap(v, gainMap[0].Pixels.View, headroom, weight, _pool));

    /// <summary>Maps to a caller-supplied palette.</summary>
    public ImagePipeline MapToPalette(ColorPalette palette) =>
        Reshape(v => PaletteQuantizer.Apply(v, palette, _pool));

    /// <summary>Maps to a palette with dithering.</summary>
    public ImagePipeline DitherToPalette(ColorPalette palette, DitherKernel kernel = DitherKernel.FloydSteinberg,
        float maxError = 16f / 255f) =>
        Reshape(v => PaletteQuantizer.Dither(v, palette, kernel, maxError, _pool));

    /// <summary>Resize preserving the aspect ratio, by width.</summary>
    public ImagePipeline ResizeToWidth(int width, Resampler resampler = Resampler.Bicubic) =>
        AppendGeometry(GeometrySteps.ResizeToWidth(width, resampler));

    /// <summary>Resize preserving the aspect ratio, by height.</summary>
    public ImagePipeline ResizeToHeight(int height, Resampler resampler = Resampler.Bicubic) =>
        AppendGeometry(GeometrySteps.ResizeToHeight(height, resampler));

    /// <summary>Thumbnail: fit inside a box, then sharpen what the downscale softened.</summary>
    public ImagePipeline Thumbnail(int maxWidth, int maxHeight, Resampler resampler = Resampler.Lanczos3,
        float sharpenAmount = 0.4f) =>
        AppendGeometry(GeometrySteps.Thumbnail(maxWidth, maxHeight, resampler, sharpenAmount));

    /// <summary>Rotation with an explicit resampler and sampling options.</summary>
    public ImagePipeline RotateWithOptions(float degrees, Resampler resampler, SampleOptions options,
        RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.RotateWithOptions(degrees, resampler, options, background));

    /// <summary>Rotate by an arbitrary angle.</summary>
    public ImagePipeline Rotate(float degrees, RgbaColor background = default) =>
        AppendGeometry(GeometrySteps.Rotate(degrees, background));

    /// <summary>Reduces to at most <paramref name="maxColors"/> colours.</summary>
    public ImagePipeline Quantize(int maxColors, Quantizer quantizer = Quantizer.MedianCut) =>
        Reshape(v => PaletteQuantizer.Apply(v, BuildPalette(v, maxColors, quantizer), _pool));

    /// <summary>Quantizes with dithering.</summary>
    public ImagePipeline ReduceColors(int maxColors, Quantizer quantizer = Quantizer.MedianCut,
        DitherKernel kernel = DitherKernel.FloydSteinberg, float maxError = 16f / 255f) =>
        Reshape(v => PaletteQuantizer.Dither(v, BuildPalette(v, maxColors, quantizer), kernel, maxError, _pool));

    /// <summary>Palette construction, dispatched by quantizer; the operators themselves take a palette.</summary>
    private static ColorPalette BuildPalette(PixelView view, int maxColors, Quantizer quantizer) => quantizer switch
    {
        Quantizer.MedianCut => PaletteQuantizer.Build(view, maxColors),
        Quantizer.Wu => WuQuantizer.Build(view, maxColors),
        Quantizer.Octree => OctreeQuantizer.Build(view, maxColors),
        _ => throw new ArgumentOutOfRangeException(nameof(quantizer), quantizer, "Unknown quantizer."),
    };

    /// <summary>Draws another image into a destination rectangle.</summary>
    public ImagePipeline DrawImage(Image source, PixelRect destination, float opacity = 1f) =>
        Reshape(v =>
        {
            using var scaled = Canvas.Resize(source[0].Pixels.View, destination.Width, destination.Height,
                Resampler.Bilinear, ResizeMode.Stretch, Anchor.Centre, default, _pool);

            if (opacity >= 1f)
                return AlphaOps.Composite(v, scaled.View, destination.X, destination.Y, _pool);

            using var faded = PointOps.Opacity(scaled.View, opacity, _pool);
            return AlphaOps.Composite(v, faded.View, destination.X, destination.Y, _pool);
        });

    /// <summary>Stamps a watermark at one of the usual positions.</summary>
    public ImagePipeline Watermark(Image mark, WatermarkPosition position = WatermarkPosition.BottomRight,
        int margin = 0, float opacity = 0.5f) =>
        Geometry(image =>
        {
            var x = position switch
            {
                WatermarkPosition.TopLeft or WatermarkPosition.BottomLeft => margin,
                WatermarkPosition.TopRight or WatermarkPosition.BottomRight => image.Width - mark.Width - margin,
                _ => (image.Width - mark.Width) / 2,
            };
            var y = position switch
            {
                WatermarkPosition.TopLeft or WatermarkPosition.TopRight => margin,
                WatermarkPosition.BottomLeft or WatermarkPosition.BottomRight => image.Height - mark.Height - margin,
                _ => (image.Height - mark.Height) / 2,
            };
            return image.Pipe(_pool).Composite(mark, x, y, PorterDuff.SourceOver).ToImage();
        });

    /// <summary>Ends the current pass, applies geometry, and starts a fresh pass on the result.</summary>
    private ImagePipeline Geometry(Func<Image, Image> reshape)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var input = Flush();
        var reshaped = reshape(input);

        // The pass just ended: its output is no longer reachable and is not the caller's source, so it goes back
        // to the pool now rather than at the next collection. Reshape returns a new image, never its input.
        if (!ReferenceEquals(input, _source))
            input.Dispose();

        _current = reshaped;
        return this;
    }

    /// <summary>
    /// Runs the recipe so far and returns the image it produced. Geometry steps are applied in recipe order; a run
    /// of operators between them is one compiled section, executed through the same <see cref="ProcessingPlan"/>
    /// the caller would have built by hand.
    /// </summary>
    private Image Flush()
    {
        if (_recipe.IsEmpty)
            return _current ?? _source;

        var recipe = _recipe.Build();
        _recipe = new PipelineRecipeBuilder();

        var input = _current ?? _source;

        // One step has nothing to fuse and nothing to gain from banding: the operator already parallelises over the
        // image itself. Running it directly is the same call the facade used to make.
        if (recipe.Count == 1 && recipe.Steps[0] is OperatorStep only &&
            only.Operator.Kernels.WholeImage is { } direct)
        {
            var directResult = input.ReplaceBuffer(direct(input[0].Pixels.View));
            if (!ReferenceEquals(input, _source))
                input.Dispose();

            _current = directResult;
            return directResult;
        }

        var options = new CompileOptions
        {
            SourceWidth = input.Width,
            SourceHeight = input.Height,
            SourceFormat = input.Format,
            Source = SourceCapabilities.Seekable | SourceCapabilities.Replayable |
                     SourceCapabilities.StableContent,
            Sink = SinkCapabilities.None,
            BandRows = _bandRows,
        };

        var compiled = PipelineCompiler.Compile(recipe, options);
        foreach (var section in compiled.Sections)
        {
            if (section.Kind == CompiledSectionKind.OperatorRun)
            {
                var steps = new PlanStep[section.StepCount];
                for (var i = 0; i < steps.Length; i++)
                    steps[i] = compiled.Steps[section.FirstStep + i];

                var plan = ProcessingPlan.FromCompiled(steps, _bandRows);
                var next = input.Apply(plan, _pool);
                if (!ReferenceEquals(input, _source))
                    input.Dispose();
                input = next;
                continue;
            }

            var geometry = (GeometryStep)recipe.Steps[section.RecipeIndex];
            var reshaped = input.ReplaceBuffer(geometry.Reshape(input[0].Pixels.View, _pool));
            if (!ReferenceEquals(input, _source))
                input.Dispose();
            input = reshaped;
        }

        _current = input;
        return input;
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
