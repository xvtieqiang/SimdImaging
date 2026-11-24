using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>Everything a <see cref="ImageTranscode"/> needs beyond the chain itself: read, write and execution
/// options, plus the region knobs when <see cref="ImageTranscode.Region"/> is used.</summary>
public sealed record TranscodeOptions
{
    /// <summary>Defaults: whole file, default load/save options, default execution, no region.</summary>
    public static TranscodeOptions Default { get; } = new();

    /// <summary>Decode options shared with <see cref="Image.Load(string, LoadOptions?, CancellationToken)"/>.</summary>
    public LoadOptions Load { get; init; } = LoadOptions.Default;

    /// <summary>Encode options shared with <see cref="Image.Save(Image, string, SaveOptions?, CancellationToken)"/>.</summary>
    public SaveOptions Save { get; init; } = SaveOptions.Default;

    /// <summary>Band height, degree, working-set ceiling and cancellation.</summary>
    public ExecuteOptions Execute { get; init; } = new();

    /// <summary>Carry the source's Exif / XMP / ICC into the output; costs a full pass over the source.</summary>
    public bool PreserveMetadata { get; init; }

    /// <summary>Page, working format, segment ceiling and pool for the region path. Null means the first page and the
    /// native format.</summary>
    public RegionOptions? Region { get; init; }
}

/// <summary>
/// A file (or stream) described as a pipeline: <c>ImageTranscode.From(path).GaussianBlur(1f).Resize(w, h).Save(dest)</c>.
/// It is the file side of <c>Image.Pipe()</c>, where there is no <see cref="Image"/> to pipe; the chain is described
/// with the same <see cref="ProcessingPlan"/> the in-memory pipeline compiles to, and <see cref="Run(string, CancellationToken)"/>
/// routes it to the streaming entry points, so the whole thing runs one pass.
/// <para>Without <see cref="Region(PixelRect)"/> every stage streams: decode → plan → resize → encode, holding a band
/// and a ring. With a region only the rectangle is decoded (TIFF), and <see cref="ToImage"/> materialises the result
/// by nature.</para>
/// <para>A chain is single use: the source stream is read once and destination streams are written once.</para>
/// </summary>
public sealed class ImageTranscode : ImageOpPipeline<ImageTranscode>
{
    private readonly string? _path;
    private readonly Stream? _stream;
    private readonly ProcessingPlan _before = new();
    private readonly ProcessingPlan _after = new();

    private bool _hasResize;
    private int _resizeWidth;
    private int _resizeHeight;
    private Resampler _resampler = Resampler.Bicubic;
    private PixelRect? _region;
    private readonly List<PreStage> _preStages = [];
    private TranscodeOptions _options = TranscodeOptions.Default;
    private bool _ran;

    private ImageTranscode(string? path, Stream? stream)
    {
        _path = path;
        _stream = stream;
    }

    /// <summary>Opens a file by path. Nothing is read until the chain is run or materialised.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static ImageTranscode From(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new ImageTranscode(path, null);
    }

    /// <summary>Opens a source stream. It is read once, from its current position, and left open.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static ImageTranscode From(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ImageTranscode(null, stream);
    }

    /// <summary>
    /// Restricts the chain to one rectangle of a TIFF page. The rectangle is the source image: it decodes only the
    /// strips or tiles it intersects, and the plan and resize see it at its own size.
    /// </summary>
    /// <exception cref="InvalidOperationException">A region is already set.</exception>
    public ImageTranscode Region(PixelRect region)
    {
        if (_region is not null)
            throw new InvalidOperationException("The region is already set.");
        _region = region;
        return this;
    }

    /// <summary>
    /// Starts the chain from a rectangle of the source. It runs before every other stage, so the plan and the resize
    /// see the crop as their image; with <see cref="Region"/> the rectangle is already chosen, and this is refused.
    /// </summary>
    /// <exception cref="InvalidOperationException">A crop or a region is already set.</exception>
    public ImageTranscode Crop(PixelRect rect)
    {
        if (_region is not null)
            throw new InvalidOperationException(
                "Region already selects the rectangle; a crop on top of it is not supported.");
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException($"The crop {rect} is empty.", nameof(rect));
        _preStages.Add(new CropStage(rect));
        return this;
    }

    /// <summary>Adds an even margin around every row, and background rows above and below. Row-local: streamable.</summary>
    public ImageTranscode Pad(int margin, RgbaColor background = default) =>
        Pad(margin, margin, margin, margin, background);

    /// <summary>Adds the given margins. Row-local: streamable.</summary>
    public ImageTranscode Pad(int left, int top, int right, int bottom, RgbaColor background = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(left);
        ArgumentOutOfRangeException.ThrowIfNegative(top);
        ArgumentOutOfRangeException.ThrowIfNegative(right);
        ArgumentOutOfRangeException.ThrowIfNegative(bottom);
        _preStages.Add(new PadStage(left, top, right, bottom, background));
        return this;
    }

    /// <summary>Mirrors every row left to right. Row-local: streamable (vertical flips are not).</summary>
    public ImageTranscode FlipHorizontal()
    {
        _preStages.Add(new FlipHorizontalStage());
        return this;
    }

    /// <summary>Appends a plan at the current position. Internal: the public surface is the operator methods, not
    /// the compiled form — a file-side chain never asks a caller to build a plan.</summary>
    internal ImageTranscode Append(ProcessingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.HasResize)
            throw new InvalidOperationException(
                "A plan that ends with a resize cannot be a stage; call Resize on the transcode instead.");
        if (_hasResize)
        {
            _after.Append(plan);
            return this;
        }

        _before.Append(plan);
        return this;
    }

    /// <summary>Ends the chain with a resize; later operator calls run at the new size.</summary>
    /// <exception cref="InvalidOperationException">The chain already has a resize.</exception>
    public ImageTranscode Resize(int width, int height, Resampler resampler = Resampler.Bicubic)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_hasResize)
            throw new InvalidOperationException("The chain already has a resize; a second one is a second call.");

        _hasResize = true;
        _resizeWidth = width;
        _resizeHeight = height;
        _resampler = resampler;
        return this;
    }

    /// <summary>Appends one plan step at the current position (before or after the resize).</summary>
    protected override ImageTranscode Step(ImageOp op) => Append(new ProcessingPlan().Add(op));

    /// <summary>Replaces the read / write / execution options. The chain itself is unchanged.</summary>
    public ImageTranscode WithOptions(TranscodeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>The output size with every stage applied, given the decoded size.</summary>
    private (int Width, int Height) SizeOf(int sourceWidth, int sourceHeight)
    {
        if (_hasResize)
            return (_resizeWidth, _resizeHeight);

        int? width = sourceWidth;
        int? height = sourceHeight;
        foreach (var stage in _preStages)
            (width, height) = stage.Size(width, height);
        return (width!.Value, height!.Value);
    }

    /// <summary>Runs the chain and writes the result to <paramref name="destinationPath"/>.</summary>
    public void Save(string destinationPath, CancellationToken cancellationToken = default) =>
        Run(destinationPath, cancellationToken);

    /// <summary>The stream form of <see cref="Save(string, CancellationToken)"/>.</summary>
    public void Save(Stream destination, ImageFormat format, CancellationToken cancellationToken = default) =>
        Run(destination, format, cancellationToken);

    /// <summary>
    /// The asynchronous form of <see cref="Run(string, CancellationToken)"/>.
    /// <para>Honest about what it is: the codec's row reader and encoder are synchronous, so the work is offloaded to
    /// the thread pool rather than overlapped with the IO. It keeps a UI thread free and is cancellable between
    /// bands; it does not make a slow disk faster. <see cref="Image.LoadAsync(Stream, LoadOptions?, CancellationToken)"/>
    /// is the one entry where the read itself is awaited.</para>
    /// </summary>
    public Task<ProcessReport> RunAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(destinationPath, cancellationToken), cancellationToken);

    /// <summary>The asynchronous form of <see cref="Run(Stream, ImageFormat, CancellationToken)"/>; same caveat as
    /// <see cref="RunAsync(string, CancellationToken)"/>.</summary>
    public Task<ProcessReport> RunAsync(Stream destination, ImageFormat format,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(destination, format, cancellationToken), cancellationToken);

    /// <summary>The asynchronous form of <see cref="Save(string, CancellationToken)"/>.</summary>
    public async Task SaveAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        await RunAsync(destinationPath, cancellationToken).ConfigureAwait(false);

    /// <summary>The asynchronous form of <see cref="Save(Stream, ImageFormat, CancellationToken)"/>.</summary>
    public async Task SaveAsync(Stream destination, ImageFormat format,
        CancellationToken cancellationToken = default) =>
        await RunAsync(destination, format, cancellationToken).ConfigureAwait(false);

    /// <summary>The asynchronous form of <see cref="ToImage(CancellationToken)"/>.</summary>
    public Task<Image> ToImageAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ToImage(cancellationToken), cancellationToken);

    /// <summary>
    /// Runs the chain, writes the result and reports how it ran. Without a region this streams end to end; with one
    /// the region is materialised and encoded.
    /// </summary>
    public ProcessReport Run(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destinationPath);
        EnsureSingleUse();

        if (_region is { } region)
        {
            using var image = ToImageCore(cancellationToken);
            Image.Save(image, destinationPath, _options.Save, cancellationToken);
            return MaterializedReport(image, StreamRefusal.None);
        }

        var refusal = _before.CanStream();
        if (refusal == StreamRefusal.None && _after.Steps.Count > 0)
            refusal = _after.CanStream();
        if (refusal != StreamRefusal.None)
        {
            using var materialisedImage = ToImageCore(cancellationToken);
            Image.Save(materialisedImage, destinationPath, _options.Save, cancellationToken);
            return MaterializedReport(materialisedImage, refusal);
        }

        if (_path is not null)
        {
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            return Image.ProcessChain(input, output, Image.FormatFromExtension(Path.GetExtension(destinationPath)),
                SizeOf, BuildChain, ProcessOptions(), cancellationToken);
        }

        using var materialised = ToImageCore(cancellationToken);
        Image.Save(materialised, destinationPath, _options.Save, cancellationToken);
        return MaterializedReport(materialised, StreamRefusal.None);
    }

    /// <summary>The stream form of <see cref="Run(string, CancellationToken)"/>; the container is
    /// <paramref name="format"/>.</summary>
    public ProcessReport Run(Stream destination, ImageFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (format == ImageFormat.Unknown)
            throw new ArgumentException("Unknown is not an encodable format.", nameof(format));
        if (_stream is not null && ReferenceEquals(_stream, destination))
            throw new ArgumentException(
                "The source and the destination are the same stream; writing while reading would corrupt both. " +
                "Use two streams, a path, or write to a separate destination.", nameof(destination));
        EnsureSingleUse();

        if (_region is { } region)
        {
            using var image = ToImageCore(cancellationToken);
            var bytes = Image.Encode(image, format, _options.Save, cancellationToken);
            destination.Write(bytes, 0, bytes.Length);
            return MaterializedReport(image, StreamRefusal.None);
        }

        var refusal = _before.CanStream();
        if (refusal == StreamRefusal.None && _after.Steps.Count > 0)
            refusal = _after.CanStream();
        if (refusal != StreamRefusal.None)
        {
            using var materialisedImage = ToImageCore(cancellationToken);
            var bytes = Image.Encode(materialisedImage, format, _options.Save, cancellationToken);
            destination.Write(bytes, 0, bytes.Length);
            return MaterializedReport(materialisedImage, refusal);
        }

        if (_path is not null)
        {
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Image.ProcessChain(input, destination, format, SizeOf, BuildChain, ProcessOptions(),
                cancellationToken);
        }

        return Image.ProcessChain(_stream!, destination, format, SizeOf, BuildChain, ProcessOptions(),
            cancellationToken);
    }

    /// <summary>The pull chain the stages describe, with every stage's buffers owned by the returned source.</summary>
    private IRowSource BuildChain(IRowSource source)
    {
        var owned = new List<IDisposable>();
        IRowSource current = source;

        foreach (var preStage in _preStages)
        {
            var next = preStage.Wrap(current, _options.Region?.Pool);
            if (next is IDisposable disposable)
                owned.Add(disposable);
            current = next;
        }

        if (_before.Steps.Count > 0)
        {
            var stage = new PlanRowSource(_before, current, _options.Execute);
            owned.Add(stage);
            current = stage;
        }

        if (_hasResize)
        {
            var stage = new ResizeRowSource(current, _resizeWidth, _resizeHeight, _resampler, _options.Execute);
            owned.Add(stage);
            current = stage;
        }

        if (_after.Steps.Count > 0)
        {
            var stage = new PlanRowSource(_after, current, _options.Execute);
            owned.Add(stage);
            current = stage;
        }

        return owned.Count == 0 ? current : new OwnedChain(current, owned);
    }

    private sealed class OwnedChain(IRowSource inner, List<IDisposable> owned) : IRowSource, IDisposable
    {
        public int Width => inner.Width;
        public int Height => inner.Height;
        public WorkingFormat Format => inner.Format;
        public int ReadRows(Span<byte> destination, int stride, int maxRows) =>
            inner.ReadRows(destination, stride, maxRows);
        public void Dispose()
        {
            foreach (var disposable in owned)
                disposable.Dispose();
        }
    }

    /// <summary>
    /// Runs the chain into memory. With a region this decodes the rectangle only; without one it is
    /// <c>Load → Apply → Resize → Apply</c>.
    /// </summary>
    public Image ToImage(CancellationToken cancellationToken = default)
    {
        EnsureSingleUse();
        return ToImageCore(cancellationToken);
    }

    private Image ToImageCore(CancellationToken cancellationToken)
    {
        if (_region is { } region)
        {
            if (_preStages.Count > 0)
                throw new NotSupportedException(
                    "Region already selects the rectangle; crop/pad/flip stages on top of it are not supported. " +
                    "Use Region for the rectangle and run the rest on the returned image.");

            if (_path is not null)
            {
                return _hasResize
                    ? Image.ResizeRegion(_path, _before, region, _resizeWidth, _resizeHeight, _resampler, _options,
                        cancellationToken)
                    : Image.ProcessRegion(_path, _before, region, _options, cancellationToken);
            }

            return _hasResize
                ? Image.ResizeRegion(_stream!, _before, region, _resizeWidth, _resizeHeight, _resampler, _options,
                    cancellationToken)
                : Image.ProcessRegion(_stream!, _before, region, _options, cancellationToken);
        }

        var current = _path is not null
            ? Image.Load(_path, _options.Load, cancellationToken)
            : Image.Load(_stream!, _options.Load, cancellationToken);

        try
        {
            foreach (var preStage in _preStages)
            {
                var next = preStage.Apply(current);
                current.Dispose();
                current = next;
            }

            if (_before.Steps.Count > 0)
            {
                var filtered = current.ApplyWithOptions(_before, _options.Execute);
                current.Dispose();
                current = filtered;
            }

            if (_hasResize)
            {
                var resized = current.Pipe().Resize(_resizeWidth, _resizeHeight, _resampler).ToImage();
                current.Dispose();
                current = resized;
            }

            if (_after.Steps.Count > 0)
            {
                var finished = current.ApplyWithOptions(_after, _options.Execute);
                current.Dispose();
                current = finished;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>One options bag for the pipeline; the region-specific execution knobs are unified here so a caller
    /// never has to remember which of the two carries the degree.</summary>
    private ProcessOptions ProcessOptions() => new()
    {
        Load = _options.Load,
        Save = _options.Save,
        Execute = _options.Execute,
        PreserveMetadata = _options.PreserveMetadata,
    };

    /// <summary>One row-local geometry stage: crop, pad or horizontal flip.</summary>
    private abstract record PreStage
    {
        public abstract (int? Width, int? Height) Size(int? width, int? height);

        public abstract IRowSource Wrap(IRowSource source, IPixelPool? pool);

        public abstract Image Apply(Image image);
    }

    private sealed record CropStage(PixelRect Rect) : PreStage
    {
        public override (int? Width, int? Height) Size(int? width, int? height) => (Rect.Width, Rect.Height);

        public override IRowSource Wrap(IRowSource source, IPixelPool? pool) =>
            new CropRowSource(source, Rect.X, Rect.Y, Rect.Width, Rect.Height, pool);

        public override Image Apply(Image image) => image.Pipe().Crop(Rect).ToImage();
    }

    private sealed record PadStage(int Left, int Top, int Right, int Bottom, RgbaColor Color) : PreStage
    {
        public override (int? Width, int? Height) Size(int? width, int? height) =>
            (width + Left + Right, height + Top + Bottom);

        public override IRowSource Wrap(IRowSource source, IPixelPool? pool) =>
            new PadRowSource(source, Left, Top, Right, Bottom, Color);

        public override Image Apply(Image image) => image.Pipe().Pad(Left, Top, Right, Bottom, Color).ToImage();
    }

    private sealed record FlipHorizontalStage : PreStage
    {
        public override (int? Width, int? Height) Size(int? width, int? height) => (width, height);

        public override IRowSource Wrap(IRowSource source, IPixelPool? pool) => new FlipRowSource(source);

        public override Image Apply(Image image) => image.Pipe().FlipHorizontal().ToImage();
    }

    /// <summary>A path source reopens for each call; a stream has only one pass in it, and reading it twice would
    /// silently produce a truncated image. Say so instead.</summary>
    private void EnsureSingleUse()
    {
        if (_stream is null)
            return;

        if (_ran)
            throw new InvalidOperationException(
                "A transcode over a stream runs once: the stream has already been read to its end. Build a new " +
                "ImageTranscode over a fresh stream, or use a path source, which reopens.");

        _ran = true;
    }

    private ProcessReport MaterializedReport(Image image, StreamRefusal refusal) =>
        new(ProcessMode.Materialized, refusal, ReaderIsStreaming: false, EncoderIsStreaming: false,
            image.Width, image.Height, image.Height, _options.Save.Metadata is not null);
}
