using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
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
/// The chain is one <see cref="PipelineRecipe"/>, the same object <see cref="ImagePipeline"/> builds; the compiler
/// decides whether it runs as rows or has to be materialised, and why. <see cref="Run(string, CancellationToken)"/>
/// routes the compiled result to the existing executors.
/// <para>Without <see cref="Region(PixelRect)"/> every stage streams or materialises as one chain, holding a band and
/// a ring. With a region only the rectangle is decoded (TIFF), and <see cref="ToImage"/> materialises the result by
/// nature.</para>
/// <para>A chain is single use over a stream: the source stream is read once and destination streams are written once.
/// A path source reopens, so it can be run more than once.</para>
/// </summary>
public sealed class ImageTranscode : ImageOpPipeline<ImageTranscode>
{
    private readonly string? _path;
    private readonly Stream? _stream;
    private readonly PipelineRecipeBuilder _recipe = new();
    private PixelRect? _region;
    private TranscodeOptions _options = TranscodeOptions.Default;
    private long _predictedBytes;
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
    /// <exception cref="InvalidOperationException">A region is already set.</exception>
    public ImageTranscode Crop(PixelRect rect)
    {
        if (_region is not null)
            throw new InvalidOperationException(
                "Region already selects the rectangle; a crop on top of it is not supported.");
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException($"The crop {rect} is empty.", nameof(rect));
        _recipe.Append(GeometrySteps.Crop(rect));
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
        _recipe.Append(GeometrySteps.Pad(left, top, right, bottom, background));
        return this;
    }

    /// <summary>Mirrors every row left to right. Row-local: streamable (vertical flips are not).</summary>
    public ImageTranscode FlipHorizontal()
    {
        _recipe.Append(GeometrySteps.FlipHorizontal());
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
        foreach (var step in plan.Steps)
            _recipe.Append(step.Definition);
        return this;
    }

    /// <summary>Appends one plan step at the current position.</summary>
    protected override ImageTranscode Step(ImageOp op) => Append(op);

    private ImageTranscode Append(ImageOp op)
    {
        _recipe.Append(op);
        return this;
    }

    /// <summary>Ends the chain with a resize; later operator calls run at the new size.</summary>
    /// <exception cref="InvalidOperationException">The chain already has a resize.</exception>
    public ImageTranscode Resize(int width, int height, Resampler resampler = Resampler.Bicubic)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_recipe.Steps.Any(step => step is GeometryStep { Kind: GeometryKind.Resize }))
            throw new InvalidOperationException("The chain already has a resize; a second one is a second call.");

        _recipe.Append(GeometrySteps.Resize(width, height, resampler));
        return this;
    }

    /// <summary>Replaces the read / write / execution options. The chain itself is unchanged.</summary>
    public ImageTranscode WithOptions(TranscodeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>The output size with every stage applied, given the decoded size.</summary>
    private (int Width, int Height) SizeOf(int sourceWidth, int sourceHeight)
    {
        var width = sourceWidth;
        var height = sourceHeight;
        foreach (var step in _recipe.Steps)
        {
            if (step is not GeometryStep geometry)
                continue;

            if (!geometry.Geometry.SizeKnown)
                throw new NotSupportedException(
                    $"The geometry step '{geometry.Name}' derives its size from the content; this path needs the " +
                    "output size before it can encode.");

            (width, height) = geometry.Geometry.Map(width, height);
        }

        return (width, height);
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
    /// Runs the chain, writes the result and reports how it ran. Without a region this streams when the compiler says
    /// the stream can run the whole recipe; with one the region is materialised and encoded.
    /// </summary>
    public ProcessReport Run(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destinationPath);
        EnsureSingleUse();
        return Execute(destinationPath, destination: null,
            Image.FormatFromExtension(Path.GetExtension(destinationPath)), cancellationToken);
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
        return Execute(null, destination, format, cancellationToken);
    }

    private ProcessReport Execute(string? destinationPath, Stream? destination, ImageFormat format,
        CancellationToken cancellationToken)
    {
        // Each run accounts for its reservations; a caller-supplied account is used as-is, otherwise one is created
        // with the same ceiling so the report can carry the peak.
        var saved = _options;
        var workspace = saved.Execute.Workspace ?? new WorkspaceManager(saved.Execute.MaxWorkingBytes);
        _predictedBytes = 0;
        _options = saved with { Execute = saved.Execute with { Workspace = workspace } };
        try
        {
            var report = ExecuteCore(destinationPath, destination, format, cancellationToken);
            return report with
            {
                PeakWorkingBytes = workspace.PeakBytes,
                PredictedWorkingBytes = _predictedBytes,
            };
        }
        finally
        {
            _options = saved;
        }
    }

    private ProcessReport ExecuteCore(string? destinationPath, Stream? destination, ImageFormat format,
        CancellationToken cancellationToken)
    {
        var recipe = _recipe.Build();

        if (_region is { } region)
        {
            using var image = ToRegionCore(region, cancellationToken);
            using var materialized = ReserveImage(image);
            return Write(image, destinationPath, destination, format, StreamRefusal.None, cancellationToken);
        }

        // The plan-level refusals first: they keep the reason the caller sees, and materialising is the only thing a
        // whole-image step can do.
        var refusal = PipelineCompiler.CanStream(recipe, PreflightOptions(), out _);
        if (refusal != StreamRefusal.None)
        {
            using var image = ToImageCore(cancellationToken);
            using var materialized = ReserveImage(image);
            return Write(image, destinationPath, destination, format, refusal, cancellationToken);
        }

        // A forward-only source cannot be rewound, so a whole-input decode has to happen here: ToImageCore reads
        // the stream exactly once, which is all such a source allows. ProcessChain would have to refuse it.
        if (_stream is not null && !_stream.CanSeek && Image.RequiresWholeInput(_options.Load))
        {
            using var image = ToImageCore(cancellationToken);
            using var materialized = ReserveImage(image);
            return Write(image, destinationPath, destination, format, StreamRefusal.DecodeNeedsWholeInput,
                cancellationToken);
        }

        // Source first: a missing input must not truncate an existing destination.
        if (destinationPath is not null)
        {
            if (_path is not null)
            {
                using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                    FileShare.None);
                return Image.ProcessChain(input, output, format, SizeOf, BuildChain, ProcessOptions(),
                    cancellationToken);
            }

            using var destinationFile = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                FileShare.None);
            return Image.ProcessChain(_stream!, destinationFile, format, SizeOf, BuildChain, ProcessOptions(),
                cancellationToken);
        }

        if (_path is not null)
        {
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Image.ProcessChain(input, destination!, format, SizeOf, BuildChain, ProcessOptions(),
                cancellationToken);
        }

        return Image.ProcessChain(_stream!, destination!, format, SizeOf, BuildChain, ProcessOptions(),
            cancellationToken);
    }

    private IDisposable? ReserveImage(Image image) => _options.Execute.Workspace?.Reserve(
        (long)image.Width * image.Height * image.Format.BytesPerPixel(), "materialized image");

    private ProcessReport Write(Image image, string? destinationPath, Stream? destination, ImageFormat format,
        StreamRefusal refusal, CancellationToken cancellationToken)
    {
        if (destinationPath is not null)
            Image.Save(image, destinationPath, _options.Save, cancellationToken);
        else
        {
            var bytes = Image.Encode(image, format, _options.Save, cancellationToken);
            destination!.Write(bytes, 0, bytes.Length);
        }

        return MaterializedReport(image, refusal);
    }

    /// <summary>What the compiler needs to know before the source is decoded. Sizes do not affect the stream
    /// decision (only the sections do), so they are placeholders here.</summary>
    private CompileOptions PreflightOptions() => new()
    {
        SourceWidth = 1,
        SourceHeight = 1,
        SourceFormat = WorkingFormat.Rgba8,
        Source = SourceCapabilitiesFor(),
        Sink = SinkCapabilities.RowWritable,
        Execute = _options.Execute,
    };

    private SourceCapabilities SourceCapabilitiesFor()
    {
        if (_path is not null)
            return SourceCapabilities.Seekable | SourceCapabilities.Replayable | SourceCapabilities.StableContent;
        return _stream!.CanSeek
            ? SourceCapabilities.Seekable | SourceCapabilities.StableContent
            : SourceCapabilities.ForwardOnly;
    }

    /// <summary>The pull chain the compiled sections describe, with every stage's buffers owned by the returned
    /// source.</summary>
    private IRowSource BuildChain(IRowSource source)
    {
        var recipe = _recipe.Build();
        var compiled = PipelineCompiler.Compile(recipe, new CompileOptions
        {
            SourceWidth = source.Width,
            SourceHeight = source.Height,
            SourceFormat = source.Format,
            Source = SourceCapabilitiesFor(),
            Sink = SinkCapabilities.RowWritable,
            Execute = _options.Execute,
        });

        if (compiled.Backend != ExecutionBackend.Stream)
            throw new InvalidOperationException(
                $"The stream pre-flight accepted a plan the compiler refused ({compiled.StreamRefusal}).");

        _predictedBytes = compiled.Workspace.BoundedBytes;
        return CompiledPlanExecutor.CreateStreamChain(compiled, source, _options.Execute, _options.Region?.Pool);
    }

    /// <summary>
    /// Runs the chain into memory. With a region this decodes the rectangle only; without one it loads the source,
    /// then executes the compiled sections: operator runs as plans, geometry as the reshape it used to call.
    /// </summary>
    public Image ToImage(CancellationToken cancellationToken = default)
    {
        EnsureSingleUse();
        return ToImageCore(cancellationToken);
    }

    private Image ToImageCore(CancellationToken cancellationToken)
    {
        if (_region is { } region)
            return ToRegionCore(region, cancellationToken);

        var recipe = _recipe.Build();
        var current = _path is not null
            ? Image.Load(_path, _options.Load, cancellationToken)
            : Image.Load(_stream!, _options.Load, cancellationToken);

        try
        {
            if (recipe.Count == 0)
                return current;

            var compiled = PipelineCompiler.Compile(recipe, new CompileOptions
            {
                SourceWidth = current.Width,
                SourceHeight = current.Height,
                SourceFormat = current.Format,
                Source = SourceCapabilitiesFor(),
                Sink = SinkCapabilities.None,
                Execute = _options.Execute,
            });

            _predictedBytes = compiled.Workspace.BoundedBytes;
            var buffer = CompiledPlanExecutor.ExecuteFrame(compiled, current[0].Pixels.View,
                _options.Region?.Pool, _options.Execute);
            var result = current.ReplaceBuffer(buffer);
            current.Dispose();
            return result;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>The region path: operator steps, then at most one trailing resize. Anything else would silently
    /// produce a different image from the whole-file path.</summary>
    private Image ToRegionCore(PixelRect region, CancellationToken cancellationToken)
    {
        var recipe = _recipe.Build();
        var steps = new List<PlanStep>(recipe.Count);
        GeometryStep? resize = null;
        foreach (var step in recipe.Steps)
        {
            if (step is OperatorStep op)
            {
                if (resize is not null)
                    throw new NotSupportedException(
                        "A region run supports operator steps before the resize only; run the rest on the returned image.");
                steps.Add(new PlanStep(op.Operator, op.Lut));
                continue;
            }

            var geometry = (GeometryStep)step;
            if (geometry.Kind == GeometryKind.Resize && step.Index == recipe.Count - 1)
            {
                resize = geometry;
                continue;
            }

            throw new NotSupportedException(
                "Region already selects the rectangle; crop/pad/flip and other geometry stages on top of it are not " +
                "supported. Use Region for the rectangle and run the rest on the returned image.");
        }

        var plan = ProcessingPlan.FromCompiled(steps, ProcessingPlan.DefaultBandRows);
        if (resize is not null)
        {
            var (width, height) = resize.Geometry.Map(region.Width, region.Height);
            var resampler = resize.Resampler ?? Resampler.Bicubic;
            return _path is not null
                ? Image.ResizeRegion(_path, plan, region, width, height, resampler, _options, cancellationToken)
                : Image.ResizeRegion(_stream!, plan, region, width, height, resampler, _options, cancellationToken);
        }

        return _path is not null
            ? Image.ProcessRegion(_path, plan, region, _options, cancellationToken)
            : Image.ProcessRegion(_stream!, plan, region, _options, cancellationToken);
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
