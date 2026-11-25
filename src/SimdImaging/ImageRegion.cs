using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>
/// Region-specific knobs for <see cref="ImageTranscode.Region"/>: which page, what working format, how large a
/// strip or tile the caller will let the codec hold, and where buffers come from. Read, write and execution options
/// live in <see cref="TranscodeOptions"/>, not here — one bag, not two.
/// </summary>
public sealed record RegionOptions
{
    /// <summary>Defaults: first page, native depth, 64 MiB per segment, shared pool.</summary>
    public static RegionOptions Default { get; } = new();

    /// <summary>
    /// TIFF page to read. 0 (the default) is the first page; the result is that page alone, as one frame —
    /// where <see cref="Image.Load(string, LoadOptions?, CancellationToken)"/> would return every page as a frame.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to a negative value.</exception>
    public int PageIndex
    {
        get => _pageIndex;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _pageIndex = value;
        }
    }

    private readonly int _pageIndex;

    /// <summary>
    /// The working format to produce. Null follows the page's sample width: 8-bit → <see cref="WorkingFormat.Rgba8"/>,
    /// 16-bit → <see cref="WorkingFormat.Rgba16"/>. <see cref="WorkingFormat.Rgba32F"/> is refused — an integer TIFF
    /// sample has no float projection without a tone policy.
    /// </summary>
    public WorkingFormat? Format { get; init; }

    /// <summary>
    /// The largest single strip or tile this call may hold, in bytes (default 64 MiB). The codec decodes a segment as
    /// a unit, so a page stored as one strip larger than this is refused by name instead of being allocated: a
    /// 20 000² single-strip page is not region-readable, whatever rectangle is asked for.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to a non-positive value.</exception>
    public long MaxSegmentBytes
    {
        get => _maxSegmentBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxSegmentBytes = value;
        }
    }

    private readonly long _maxSegmentBytes = 64L << 20;

    /// <summary>Where the result buffer is rented. Null uses <see cref="ArrayPixelPool.Shared"/>.</summary>
    public IPixelPool? Pool { get; init; }
}

/// <summary>What a region read of a given file will cost, read from the header alone.</summary>
/// <param name="PageIndex">The page described.</param>
/// <param name="ImageWidth">Page width.</param>
/// <param name="ImageHeight">Page height.</param>
/// <param name="Format">The working format the read would produce.</param>
/// <param name="BitDepth">Bits of real precision per sample (1, 2, 4, 8 or 16).</param>
/// <param name="Orientation">The stored Orientation tag; the pixels are never turned by this API.</param>
/// <param name="IsTiled">True when the page is stored as tiles rather than strips.</param>
/// <param name="SegmentsAcross">Tiles across (1 for strips).</param>
/// <param name="SegmentsDown">Tiles down, or strips down.</param>
/// <param name="MaxSegmentBytes">Bytes the largest segment needs — what <see cref="RegionOptions.MaxSegmentBytes"/>
/// is checked against.</param>
public readonly record struct RegionCapabilities(int PageIndex, int ImageWidth, int ImageHeight,
    WorkingFormat Format, int BitDepth, ImageOrientation Orientation, bool IsTiled, int SegmentsAcross,
    int SegmentsDown, long MaxSegmentBytes);

public sealed partial class Image
{
    /// <summary>
    /// Describes what a region read of <paramref name="path"/> supports, without decoding a pixel.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="NotSupportedException">The file is not a TIFF, or its page layout is one this build does
    /// not read as a region (see <see cref="TiffRegionSource"/> for the list).</exception>
    /// <exception cref="InvalidDataException">The bytes are not a recognised image.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page index is past the last page.</exception>
    public static RegionCapabilities DescribeRegion(string path, int pageIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var source = TiffRegionSource.Open(path, pageIndex, format: null, maxSegmentBytes: long.MaxValue);
        return Describe(source);
    }

    /// <summary>
    /// The stream form of <c>DescribeRegion</c>. The stream must be seekable and hold
    /// the TIFF header at position 0 — TIFF offsets are absolute.
    /// </summary>
    public static RegionCapabilities DescribeRegion(Stream seekableTiff, int pageIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(seekableTiff);
        using var source = TiffRegionSource.Open(seekableTiff, pageIndex, format: null, maxSegmentBytes: long.MaxValue);
        return Describe(source);
    }

    /// <summary>
    /// Opens a file as a random-region source for <see cref="ProcessTiles(IRegionSource, ProcessingPlan, int, int, ExecuteOptions?, IPixelPool?)"/>
    /// or <see cref="ProcessTilesTo(IRegionSource, Stream, ImageFormat, ProcessingPlan, int, int, ExecuteOptions?, SaveOptions?, IPixelPool?, CancellationToken)"/>.
    /// The caller disposes it to close the file. TIFF only: it is the one layout this build reads as arbitrary
    /// rectangles, and the same layout refusals as <see cref="DescribeRegion(string, int)"/> apply.
    /// </summary>
    /// <exception cref="NotSupportedException">The file is not a TIFF, or its page layout is not region-readable.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a recognised image.</exception>
    public static ImageRegionSource OpenRegionSource(string path, RegionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= RegionOptions.Default;
        return new ImageRegionSource(TiffRegionSource.Open(path, options.PageIndex, options.Format,
            options.MaxSegmentBytes));
    }

    private static RegionCapabilities Describe(TiffRegionSource source)
    {
        var layout = source.Layout;
        return new RegionCapabilities(layout.PageIndex, layout.Width, layout.Height, source.Format,
            layout.StoredBitsPerSample, (ImageOrientation)layout.Orientation, layout.IsTiled,
            layout.SegmentsAcross, layout.SegmentsDown, source.MaxSegmentBytes);
    }

    /// <summary>
    /// Decodes only <paramref name="region"/> of a TIFF, runs <paramref name="plan"/> over it and returns it as an
    /// image. Byte-identical to <c>Load(whole).Apply(plan).Crop(region)</c> on the same pixels.
    /// <para>This entry point never falls back to reading the file whole: a layout it cannot read natively throws
    /// <see cref="NotSupportedException"/> naming the reason. Use <see cref="Load(string, LoadOptions?, CancellationToken)"/>
    /// for the whole-file path — a region is an optimisation with a contract, not a silent alternative.</para>
    /// <para>Size, real bit depth and the Orientation tag come from the page layout. Exif / XMP / ICC and the colour
    /// encoding do <b>not</b>: the codec documents that metadata needs the whole file read, and reading it here would
    /// defeat the point of a region. Attach them with <see cref="SaveOptions.Metadata"/> if the output needs them.
    /// Coordinates are the <b>stored</b> ones: an Orientation tag is reported, not applied, exactly as <c>Load</c>
    /// does by default.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="plan"/> is null.</exception>
    /// <exception cref="ArgumentException">The region is empty or does not lie inside the page.</exception>
    /// <exception cref="InvalidOperationException">The region exceeds <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="NotSupportedException">The file is not a TIFF, its layout is not readable as a region, or the
    /// plan cannot run over a rectangle (the message names the step).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal static Image ProcessRegion(string path, ProcessingPlan plan, PixelRect region,
        TranscodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= TranscodeOptions.Default;

        using var source = TiffRegionSource.Open(path, options.Region?.PageIndex ?? 0, options.Region?.Format, options.Region?.MaxSegmentBytes ?? (64L << 20));
        return ProcessCore(source, plan, region, options, cancellationToken);
    }

    /// <summary>
    /// The stream form of <c>ProcessRegion</c>.
    /// The stream is read from position 0 (TIFF offsets are absolute) and left open.
    /// </summary>
    internal static Image ProcessRegion(Stream seekableTiff, ProcessingPlan plan, PixelRect region,
        TranscodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seekableTiff);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= TranscodeOptions.Default;

        using var source = TiffRegionSource.Open(seekableTiff, options.Region?.PageIndex ?? 0, options.Region?.Format,
            options.Region?.MaxSegmentBytes ?? (64L << 20));
        return ProcessCore(source, plan, region, options, cancellationToken);
    }

    /// <summary>
    /// Decodes only <paramref name="region"/> of a TIFF and resizes it to <paramref name="width"/> ×
    /// <paramref name="height"/>, holding one band of the region rather than the region: the geometric counterpart of
    /// <c>ProcessRegion</c>.
    /// <para>Byte-identical to <c>Load(whole).Crop(region).Resize(width, height, resampler)</c>: the region is the
    /// source image, so its edges clamp exactly as the crop's would.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">The region is empty or does not lie inside the page.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="InvalidOperationException">The region or the destination exceeds
    /// <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="NotSupportedException">The file is not a TIFF, or its layout is not one this build reads as a
    /// region (see <see cref="TiffRegionSource"/>).</exception>
    internal static Image ResizeRegion(string path, PixelRect region, int width, int height,
        Resampler resampler = Resampler.Bicubic, TranscodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= TranscodeOptions.Default;
        using var source = TiffRegionSource.Open(path, options.Region?.PageIndex ?? 0, options.Region?.Format, options.Region?.MaxSegmentBytes ?? (64L << 20));
        return ResizeCore(source, plan: null, region, width, height, resampler, options, cancellationToken);
    }

    /// <summary>
    /// Decodes only <paramref name="region"/>, runs <paramref name="plan"/> over it and resizes the result, holding one
    /// band of the region: <c>Load → Apply(plan) → Crop(region) → Resize</c> as one pass, without the cropped
    /// intermediate.
    /// <para>The plan must be one the region path can run (no <c>Global</c> / <c>Composite</c> / whole-image step);
    /// it is refused by name otherwise, never materialised.</para>
    /// </summary>
    internal static Image ResizeRegion(string path, ProcessingPlan plan, PixelRect region, int width, int height,
        Resampler resampler = Resampler.Bicubic, TranscodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= TranscodeOptions.Default;
        using var source = TiffRegionSource.Open(path, options.Region?.PageIndex ?? 0, options.Region?.Format, options.Region?.MaxSegmentBytes ?? (64L << 20));
        return ResizeCore(source, plan, region, width, height, resampler, options, cancellationToken);
    }

    /// <summary>Stream form of the plan-taking <c>ResizeRegion</c>.</summary>
    internal static Image ResizeRegion(Stream seekableTiff, ProcessingPlan plan, PixelRect region, int width,
        int height, Resampler resampler = Resampler.Bicubic, TranscodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seekableTiff);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= TranscodeOptions.Default;
        using var source = TiffRegionSource.Open(seekableTiff, options.Region?.PageIndex ?? 0, options.Region?.Format,
            options.Region?.MaxSegmentBytes ?? (64L << 20));
        return ResizeCore(source, plan, region, width, height, resampler, options, cancellationToken);
    }

    /// <summary>The stream form of <c>ResizeRegion</c>;
    /// the stream must be seekable with the TIFF header at position 0.</summary>
    internal static Image ResizeRegion(Stream seekableTiff, PixelRect region, int width, int height,
        Resampler resampler = Resampler.Bicubic, TranscodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seekableTiff);
        options ??= TranscodeOptions.Default;
        using var source = TiffRegionSource.Open(seekableTiff, options.Region?.PageIndex ?? 0, options.Region?.Format,
            options.Region?.MaxSegmentBytes ?? (64L << 20));
        return ResizeCore(source, plan: null, region, width, height, resampler, options, cancellationToken);
    }

    private static Image ResizeCore(TiffRegionSource source, ProcessingPlan? plan, PixelRect region, int width,
        int height, Resampler resampler, TranscodeOptions options, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        cancellationToken.ThrowIfCancellationRequested();

        if (!region.FitsIn(source.Width, source.Height) || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"The region {region} does not lie inside {source.Width}x{source.Height}.",
                nameof(region));

        if ((long)region.Width * region.Height > options.Load.MaxPixels)
            throw new InvalidOperationException(
                $"The region would be {region.Width}x{region.Height} = {(long)region.Width * region.Height} pixels, "
                + $"above the {options.Load.MaxPixels} pixel limit.");
        if ((long)width * height > options.Load.MaxPixels)
            throw new InvalidOperationException(
                $"The destination would be {width}x{height} = {(long)width * height} pixels, above the "
                + $"{options.Load.MaxPixels} pixel limit.");

        if (plan is not null)
        {
            var refusal = RegionExecutor.Classify(plan.Steps, plan.HasResize, out var refusingStep);
            if (refusal != RegionRefusal.None)
                throw new NotSupportedException(
                    $"This plan cannot run over a region: {refusal} at step '{refusingStep}'. Run it over a whole "
                    + "image with Load + Apply + Resize instead.");
        }

        var layout = source.Layout;
        var buffer = WorkingBuffer.Rent(width, height, source.Format, options.Region?.Pool);
        try
        {
            var sink = new BufferRowSink(buffer.View);

            if (plan is null)
            {
                // One band of the region at a time: the resize pulls rows, so the region never exists whole.
                var bandRows = Math.Min(256, region.Height);
                using var regionRows = new RegionRowSource(source, region, bandRows, options.Region?.Pool);
                using var resized = new ResizeRowSource(regionRows, width, height, resampler, options.Execute);
                Pipeline.Copy(resized, sink, cancellationToken);
            }
            else
            {
                // A plan needs its halo: read the region widened by ΣHaloX/ΣHaloY, run the plan over that window,
                // then keep only the inner rectangle. Widening the read is what makes interior pixels identical to
                // Apply(whole).Crop(region).
                var haloX = 0;
                var haloY = 0;
                foreach (var step in plan.Steps)
                {
                    haloX += step.HaloX;
                    haloY += step.HaloY;
                }

                var expanded = new PixelRect(
                    Math.Max(0, region.X - haloX),
                    Math.Max(0, region.Y - haloY),
                    Math.Min(source.Width, region.X + region.Width + haloX) - Math.Max(0, region.X - haloX),
                    Math.Min(source.Height, region.Y + region.Height + haloY) - Math.Max(0, region.Y - haloY));

                using var regionRows = new RegionRowSource(source, expanded, Math.Min(256, expanded.Height), options.Region?.Pool);
                using var planned = new PlanRowSource(plan, regionRows, options.Execute);
                using var cropped = new CropRowSource(planned, region.X - expanded.X, region.Y - expanded.Y,
                    region.Width, region.Height, options.Region?.Pool);
                using var resized = new ResizeRowSource(cropped, width, height, resampler, options.Execute);
                Pipeline.Copy(resized, sink, cancellationToken);
            }

            var info = new ImageInfo(width, height, 4, source.Format.ToSampleFormat())
            {
                Format = ImageFormat.Tiff,
                BitDepth = layout.StoredBitsPerSample,
                Orientation = (ImageOrientation)layout.Orientation,
                FrameCount = 1,
                ColorEncoding = ColorEncoding.AssumedSrgb,
            };
            return new Image([new ImageFrame(buffer, null)], info, source.Format, ColorEncoding.AssumedSrgb,
                ImageMetadata.Empty);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Collects destination rows into the result buffer the caller will hand to an <see cref="Image"/>.</summary>
    private sealed class BufferRowSink(PixelView view) : IRowSink
    {
        private int _row;

        public void WriteRows(ReadOnlySpan<byte> rows, int stride, int rowCount)
        {
            var rowBytes = view.Width * view.Format.BytesPerPixel();
            if (stride == 0)
                stride = rowBytes;
            if (_row + rowCount > view.Height)
                throw new InvalidOperationException(
                    $"The resize wrote {_row + rowCount} rows into a {view.Height}-row destination.");

            for (var y = 0; y < rowCount; y++)
                rows.Slice(y * stride, rowBytes).CopyTo(view.Row(_row + y));
            _row += rowCount;
        }
    }

    private static Image ProcessCore(TiffRegionSource source, ProcessingPlan plan, PixelRect region,
        TranscodeOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!region.FitsIn(source.Width, source.Height) || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"The region {region} does not lie inside {source.Width}x{source.Height}.",
                nameof(region));

        var pixels = (long)region.Width * region.Height;
        if (pixels > options.Load.MaxPixels)
            throw new InvalidOperationException(
                $"The region would be {region.Width}x{region.Height} = {pixels} pixels, above the "
                + $"{options.Load.MaxPixels} pixel limit.");

        // One compile for the region: backend choice, refusal reason, region cost and per-step source regions all
        // come from the same plan the explanation reports.
        var compiled = PipelineCompiler.Compile(BuildRecipe(plan), new CompileOptions
        {
            SourceWidth = source.Width,
            SourceHeight = source.Height,
            SourceFormat = source.Format,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion |
                     SourceCapabilities.StableContent,
            Sink = SinkCapabilities.None,
            BandRows = plan.BandRows,
            Region = region,
            Execute = options.Execute ?? new ExecuteOptions(),
        });

        if (compiled.Backend != ExecutionBackend.Region)
            throw new NotSupportedException(
                $"This plan cannot run over a region: {compiled.RegionRefusal}. Run it over a whole image with " +
                "Load + Apply instead.");

        var layout = source.Layout;
        var buffer = WorkingBuffer.Rent(region.Width, region.Height, source.Format, options.Region?.Pool);
        try
        {
            var result = CompiledPlanExecutor.ExecuteRegion(compiled, source, region, buffer.View,
                options.Execute, options.Region?.Pool);
            if (!result.Succeeded)
                throw new NotSupportedException(
                    $"The plan refused the region at step '{result.RefusingStep}' ({result.Refusal}); the compiled " +
                    "backend said it could. That is a bug in the capability check, not in the caller.");

            var output = new ImageInfo(region.Width, region.Height, 4, source.Format.ToSampleFormat())
            {
                Format = ImageFormat.Tiff,
                BitDepth = layout.StoredBitsPerSample,
                Orientation = (ImageOrientation)layout.Orientation,
                FrameCount = 1,
                ColorEncoding = ColorEncoding.AssumedSrgb,
            };
            return new Image([new ImageFrame(buffer, null)], output, source.Format, ColorEncoding.AssumedSrgb,
                ImageMetadata.Empty);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}

/// <summary>
/// A file opened as a random-region source: the public form of <see cref="Image.OpenRegionSource"/>. It reads only
/// the strips or tiles that intersect a requested rectangle, and owns the file until disposed.
/// </summary>
public sealed class ImageRegionSource : IRegionSource, IDisposable
{
    private readonly TiffRegionSource _source;

    internal ImageRegionSource(TiffRegionSource source) => _source = source;

    /// <inheritdoc />
    public int Width => _source.Width;

    /// <inheritdoc />
    public int Height => _source.Height;

    /// <inheritdoc />
    public WorkingFormat Format => _source.Format;

    /// <inheritdoc />
    public void ReadRegion(PixelRect region, PixelView destination) => _source.ReadRegion(region, destination);

    /// <summary>Closes the file.</summary>
    public void Dispose() => _source.Dispose();
}
