using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Internal;

namespace SimdImaging;

/// <summary>Tile execution: rectangles in, rectangles out, through the same compiled sections the other backends use.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Runs <paramref name="plan"/> over the whole image described by <paramref name="source"/> one
    /// <paramref name="tileWidth"/> × <paramref name="tileHeight"/> rectangle at a time, returning a new buffer.
    /// </summary>
    /// <remarks>
    /// The tile size is the memory knob for the walk: one tile plus its halo window is resident at a time. The
    /// returned frame is a buffer this overload rents, so it is reserved for the duration of the walk as well —
    /// use the sink form when the result must not be held whole. A plan with a step a rectangle cannot run
    /// (a whole-image step, a composite, a block-aligned step or a format change) falls back to whole-image
    /// execution instead of failing the caller's goal; the fallback is visible in the returned buffer's size and
    /// in the working-set account, and an explicit <see cref="ExecuteOptions.MaxWorkingBytes"/> still applies.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The whole-image fallback does not fit
    /// <see cref="ExecuteOptions.MaxWorkingBytes"/>.</exception>
    /// <summary>Runs the walk and returns the planner's decision in <paramref name="report"/>.</summary>
    public static WorkingBuffer ProcessTiles(IRegionSource source, ProcessingPlan plan, int tileWidth, int tileHeight,
        ExecuteOptions options, IPixelPool? pool, out ExecutionReport report) =>
        ProcessTilesCore(source, plan, tileWidth, tileHeight, options, pool, out report);

    private static WorkingBuffer ProcessTilesCore(IRegionSource source, ProcessingPlan plan, int tileWidth,
        int tileHeight, ExecuteOptions? options, IPixelPool? pool, out ExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        var execute = options ?? new ExecuteOptions();
        var targetBytes = TargetBuffer.Bytes(source.Width, source.Height, source.Format);
        // The planner decides before the target is rented: tile if the chain supports it, whole-image otherwise.
        // The full target is extra only for the tile candidate; the frame estimate already includes its destination.
        var decision = ExecutionPlanner.Plan(TileRequest(source, plan, tileWidth, tileHeight, execute,
            backend => backend == ExecutionBackend.Tile ? targetBytes : 0));
        report = decision.ToReport(OutputStrategy.Buffered);
        var compiled = RequireDecision(decision);

        if (compiled.Backend == ExecutionBackend.Frame)
            return ExecuteWhole(compiled, source, execute, pool);

        if (compiled.Backend != ExecutionBackend.Tile)
            throw new InvalidOperationException(
                $"The planner selected {compiled.Backend}, which the tile entry cannot execute.");

        var tileOptions = execute.MaxWorkingBytes > 0
            ? execute with { MaxWorkingBytes = execute.MaxWorkingBytes - targetBytes }
            : execute;
        using var targetLease = TargetBuffer.Reserve(execute, source.Width, source.Height, source.Format,
            "tiled destination");
        var destination = WorkingBuffer.Rent(source.Width, source.Height, source.Format, pool);
        try
        {
            CompiledPlanExecutor.ExecuteTiles(compiled, source, new BufferRegionSink(destination), tileWidth,
                tileHeight, tileOptions, pool);
            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The sink form of <c>ProcessTiles(..., out ExecutionReport)</c>:
    /// each finished tile is handed to <paramref name="sink"/> at its absolute coordinates.
    /// </summary>
    /// <summary>Runs the walk and returns the planner's decision in <paramref name="report"/>.</summary>
    public static void ProcessTiles(IRegionSource source, IRegionSink sink, ProcessingPlan plan, int tileWidth,
        int tileHeight, ExecuteOptions options, IPixelPool? pool, out ExecutionReport report) =>
        ProcessTilesCore(source, sink, plan, tileWidth, tileHeight, options, pool, out report);

    private static void ProcessTilesCore(IRegionSource source, IRegionSink sink, ProcessingPlan plan,
        int tileWidth, int tileHeight, ExecuteOptions? options, IPixelPool? pool, out ExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(plan);

        var execute = options ?? new ExecuteOptions();
        var decision = ExecutionPlanner.Plan(TileRequest(source, plan, tileWidth, tileHeight, execute, null));
        report = decision.ToReport(CompiledBackendOutput(decision));
        var compiled = RequireDecision(decision);
        if (compiled.Backend == ExecutionBackend.Tile)
        {
            CompiledPlanExecutor.ExecuteTiles(compiled, source, sink, tileWidth, tileHeight, execute, pool);
            return;
        }

        if (compiled.Backend != ExecutionBackend.Frame)
            throw new InvalidOperationException(
                $"The planner selected {compiled.Backend}, which the tile entry cannot execute.");

        // The fallback result is delivered in bands so an encoder sink still receives rows in order. The result
        // stays reserved while it is alive: ExecuteFrame releases its plan lease before returning.
        using var result = ExecuteWhole(compiled, source, execute, pool);
        var resultBytes = checked((long)result.Width * result.Height * result.Format.BytesPerPixel());
        using var resultLease = execute.Workspace?.Reserve(resultBytes, "tile fallback result");
        WriteBands(result, sink, tileHeight);
    }

    /// <summary>The planner's Frame candidate, executed over a source read once into a full frame.</summary>
    private static WorkingBuffer ExecuteWhole(CompiledExecutionPlan compiled, IRegionSource source,
        ExecuteOptions options, IPixelPool? pool)
    {
        var sourceBytes = checked((long)source.Width * source.Height * source.Format.BytesPerPixel());
        if (options.MaxWorkingBytes > 0 && sourceBytes > options.MaxWorkingBytes)
            throw new InvalidOperationException(
                $"A whole-image fallback needs {sourceBytes} bytes for the source, above " +
                $"ExecuteOptions.MaxWorkingBytes = {options.MaxWorkingBytes}. Raise the ceiling or use a tileable plan.");

        using var sourceLease = options.Workspace?.Reserve(sourceBytes, "tile fallback source");
        using var frame = WorkingBuffer.Rent(source.Width, source.Height, source.Format, pool);
        source.ReadRegion(new PixelRect(0, 0, source.Width, source.Height), frame.View);
        return CompiledPlanExecutor.ExecuteFrame(compiled, frame.View, pool, options);
    }

    private static void WriteBands(WorkingBuffer result, IRegionSink sink, int bandRows)
    {
        if (sink.Width != result.Width || sink.Height != result.Height || sink.Format != result.Format)
            throw new InvalidOperationException(
                $"The plan fell back to whole-image execution and produced {result.Width}x{result.Height} " +
                $"{result.Format}, but the sink is {sink.Width}x{sink.Height} {sink.Format}. Use the buffer form " +
                "or a sink sized for the plan's output.");

        var rowsPerBand = Math.Max(1, Math.Min(bandRows, result.Height));
        for (var top = 0; top < result.Height; top += rowsPerBand)
        {
            var rows = Math.Min(rowsPerBand, result.Height - top);
            var band = new PixelRect(0, top, result.Width, rows);
            sink.WriteRegion(band, result.View.Slice(band));
        }
    }

    private static OutputStrategy CompiledBackendOutput(ExecutionDecision decision) =>
        decision.Succeeded && decision.SelectedBackend == ExecutionBackend.Tile
            ? OutputStrategy.Region
            : OutputStrategy.Banded;

    private static ExecutionRequest TileRequest(IRegionSource source, ProcessingPlan plan, int tileWidth,
        int tileHeight, ExecuteOptions execute, Func<ExecutionBackend, long>? additionalBytes) =>
        new()
        {
            Recipe = RecipeFor(plan),
            Width = source.Width,
            Height = source.Height,
            Format = source.Format,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion |
                     SourceCapabilities.StableContent,
            Sink = SinkCapabilities.RandomRegionWrite,
            TileRequested = true,
            TileWidth = Math.Min(tileWidth, source.Width),
            TileHeight = Math.Min(tileHeight, source.Height),
            Preferred = ExecutionBackend.Tile,
            Policy = execute.Policy,
            Execute = execute,
            BandRows = plan.BandRows,
            AdditionalBytesFor = additionalBytes,
        };

    private static CompiledExecutionPlan RequireDecision(ExecutionDecision decision)
    {
        if (decision.Succeeded)
            return decision.Plan!;

        // The preferred candidate explains why the tile path was not taken; the terminal candidate explains why
        // the fallback did not produce a result. Report both, but choose the exception by the terminal blocker.
        var terminal = decision.TerminalRefusal ?? decision.Refusal!;
        var primary = decision.Refusal;
        var message = primary is not null && !ReferenceEquals(primary, terminal)
            ? $"{primary.Message} {terminal.Message}"
            : terminal.Message ?? "The plan exceeds the execution constraints.";

        throw terminal.Reason switch
        {
            ExecutionRefusalReason.Capability or ExecutionRefusalReason.InvalidPlan =>
                new NotSupportedException(message),
            _ => new InvalidOperationException(message),
        };
    }

    private static PipelineRecipe RecipeFor(ProcessingPlan plan)
    {
        var recipe = new PipelineRecipeBuilder();
        foreach (var step in plan.Steps)
            recipe.Append(step.Definition, step.Lut, step.Secondary);
        if (plan.ResizeSpec is { } resize)
            recipe.Append(GeometrySteps.Resize(resize.Width, resize.Height, resize.Resampler));
        return recipe.Build();
    }

    /// <summary>
    /// Runs <paramref name="plan"/> tile by tile and encodes the result as it arrives: the encoder-side
    /// <see cref="IRegionSink"/> the tile backend was missing. No whole-image buffer is allocated; the sink holds one
    /// band of <paramref name="tileHeight"/> rows because an encoder takes rows in order and tiles arrive per band.
    /// </summary>
    /// <remarks>
    /// A tile run keeps the source's size; when the planner falls back to whole-image execution, the result may be
    /// resized and the report says <see cref="ProcessMode.Materialized"/>. Metadata travels only through
    /// <paramref name="save"/>, because a region read does not decode the file's metadata blocks.
    /// </remarks>
    /// <exception cref="NotSupportedException">The plan has no executable form.</exception>
    /// <exception cref="InvalidOperationException">The selected form does not fit the working-set ceiling.</exception>
    public static ProcessReport ProcessTilesTo(IRegionSource source, Stream destination, ImageFormat format,
        ProcessingPlan plan, int tileWidth, int tileHeight, ExecuteOptions? options = null,
        SaveOptions? save = null, IPixelPool? pool = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(plan);
        if (format == ImageFormat.Unknown)
            throw new ArgumentException("Unknown is not an encodable format.", nameof(format));

        save ??= SaveOptions.Default;
        var saved = options ?? new ExecuteOptions();
        var workspace = saved.Workspace ?? new WorkspaceManager(saved.MaxWorkingBytes);
        var execute = saved with { Workspace = workspace };
        tileWidth = Math.Min(tileWidth, source.Width);
        tileHeight = Math.Min(tileHeight, source.Height);
        var encoderBandRows = Math.Min(tileHeight, source.Height);
        var encoderBandBytes = checked((long)source.Width * encoderBandRows * source.Format.BytesPerPixel()
            + (long)encoderBandRows * sizeof(int));

        // The planner admits the encoder band together with the plan's workspace. A tile candidate keeps only a
        // band; the whole-image fallback keeps the full frame plus the band, and both are checked before any
        // encoder header is written.
        var decision = ExecutionPlanner.Plan(TileRequest(source, plan, tileWidth, tileHeight, execute,
            _ => encoderBandBytes));
        var compiled = RequireDecision(decision);
        if (compiled.Backend == ExecutionBackend.Frame)
            return WriteWhole(compiled, decision, source, destination, format, encoderBandRows, execute, save, pool,
                cancellationToken);

        if (compiled.Backend != ExecutionBackend.Tile)
            throw new InvalidOperationException(
                $"The planner selected {compiled.Backend}, which the tile encoder entry cannot execute.");

        var tileOptions = execute.MaxWorkingBytes > 0
            ? execute with { MaxWorkingBytes = execute.MaxWorkingBytes - encoderBandBytes }
            : execute;

        var predictedTotalBytes = checked(compiled.Workspace.BoundedBytes + encoderBandBytes);
        if (workspace.LimitBytes > 0 &&
            checked(workspace.OutstandingBytes + predictedTotalBytes) > workspace.LimitBytes)
            throw new InvalidOperationException(
                $"The tiled pipeline needs {predictedTotalBytes} bytes in addition to " +
                $"{workspace.OutstandingBytes} already reserved, above the WorkspaceManager limit of " +
                $"{workspace.LimitBytes} bytes.");

        // Allocate and reserve the encoder band before opening the destination encoder. This catches both an
        // account-limit refusal and a band allocation failure without leaving a partial output header.
        using (var sink = new TileEncoderSink(source.Width, source.Height, source.Format,
                   Math.Min(tileHeight, source.Height), pool, execute, cancellationToken))
        {
            var encodeOptions = BuildEncodeOptions(format, save);
            using var session = ImageCodecs.CreateEncoder(format, destination, encodeOptions, leaveOpen: true);
            var metadata = save.Metadata ?? ImageMetadata.Empty;
            var frame = session.AddFrame(source.Width, source.Height, source.Format.ToPixelFormat(),
                ColorEncoding.AssumedSrgb, metadata, null);
            sink.Attach(frame);

            CompiledPlanExecutor.ExecuteTiles(compiled, source, sink, tileWidth, tileHeight, tileOptions, pool);
            sink.Complete();
            session.Commit(cancellationToken);

            return new ProcessReport(
                decision.ToReport(OutputStrategy.Banded) with
                {
                    PeakWorkingBytes = workspace.PeakBytes,
                    PredictedWorkingBytes = predictedTotalBytes,
                },
                StreamRefusal.None, ReaderIsStreaming: false, EncoderIsStreaming: session.IsStreaming,
                source.Width, source.Height, source.Height, MetadataCarried: save.Metadata is not null);
        }
    }

    /// <summary>
    /// Encodes the whole-image result when the plan cannot tile. The result is produced before the encoder is
    /// opened, so a plan or budget failure still leaves the destination untouched.
    /// </summary>
    private static ProcessReport WriteWhole(CompiledExecutionPlan compiled, ExecutionDecision decision,
        IRegionSource source, Stream destination, ImageFormat format, int bandRows, ExecuteOptions execute,
        SaveOptions save, IPixelPool? pool, CancellationToken cancellationToken)
    {
        using var result = ExecuteWhole(compiled, source, execute, pool);
        var resultBytes = checked((long)result.Width * result.Height * result.Format.BytesPerPixel());
        using var resultLease = execute.Workspace?.Reserve(resultBytes, "whole-image fallback result");
        var computedPredicted = decision.ToReport(OutputStrategy.Banded).PredictedWorkingBytes;
        var encodeOptions = BuildEncodeOptions(format, save);
        using var session = ImageCodecs.CreateEncoder(format, destination, encodeOptions, leaveOpen: true);
        using var sink = new TileEncoderSink(result.Width, result.Height, result.Format,
            Math.Max(1, Math.Min(bandRows, result.Height)), pool, execute, cancellationToken);
        var metadata = save.Metadata ?? ImageMetadata.Empty;
        var frame = session.AddFrame(result.Width, result.Height, result.Format.ToPixelFormat(),
            ColorEncoding.AssumedSrgb, metadata, null);
        sink.Attach(frame);

        WriteBands(result, sink, bandRows);
        sink.Complete();
        session.Commit(cancellationToken);
        return new ProcessReport(
            decision.ToReport(OutputStrategy.Banded) with
            {
                PeakWorkingBytes = execute.Workspace?.PeakBytes ?? 0,
                PredictedWorkingBytes = computedPredicted,
            },
            StreamRefusal.None, ReaderIsStreaming: false, EncoderIsStreaming: session.IsStreaming,
            result.Width, result.Height, result.Height, MetadataCarried: save.Metadata is not null);
    }

    /// <summary>
    /// The path form of <see cref="ProcessTilesTo(IRegionSource, Stream, ImageFormat, ProcessingPlan, int, int, ExecuteOptions?, SaveOptions?, IPixelPool?, CancellationToken)"/>:
    /// opens a region-readable TIFF and the destination named by its extension. The page, working format and segment
    /// cap come from <see cref="TranscodeOptions.Region"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The file is not a region-readable TIFF, or the plan has no
    /// executable form.</exception>
    public static ProcessReport ProcessTilesTo(string sourcePath, string destinationPath, ProcessingPlan plan,
        int tileWidth, int tileHeight, TranscodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(plan);

        options ??= TranscodeOptions.Default;
        return WriteTileOutputAtomically(destinationPath, temporaryPath =>
        {
            using var source = TiffRegionSource.Open(sourcePath, options.Region?.PageIndex ?? 0,
                options.Region?.Format, options.Region?.MaxSegmentBytes ?? (64L << 20));
            using var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None);
            return ProcessTilesTo(source, destination, FormatFromExtension(Path.GetExtension(destinationPath)),
                plan, tileWidth, tileHeight, options.Execute, options.Save, options.Region?.Pool, cancellationToken);
        });
    }


    /// <summary>Preserves an existing destination when tile compilation, workspace admission or encoding fails.</summary>
    private static T WriteTileOutputAtomically<T>(string destinationPath, Func<string, T> write)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        var extension = Path.GetExtension(fullPath);
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var temporaryPath = Path.Combine(directory, $".{name}.{Guid.NewGuid():N}{extension}");
        try
        {
            var result = write(temporaryPath);
            File.Move(temporaryPath, fullPath, overwrite: true);
            return result;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>The in-memory sink behind the buffer-returning overload; it owns nothing.</summary>
    private sealed class BufferRegionSink(WorkingBuffer buffer) : IRegionSink
    {
        public int Width => buffer.Width;
        public int Height => buffer.Height;
        public WorkingFormat Format => buffer.Format;

        public void WriteRegion(PixelRect region, PixelView source)
        {
            var bytes = Format.BytesPerPixel();
            if (region.Width != source.Width || region.Height != source.Height)
                throw new ArgumentException("The source view is not the region's size.", nameof(source));

            for (var y = 0; y < region.Height; y++)
            {
                source.Row(y).CopyTo(buffer.View.Row(region.Y + y)
                    .Slice(region.X * bytes, region.Width * bytes));
            }
        }
    }

    /// <summary>
    /// Buffers one band of rows and writes it to the encoder frame once every column has arrived. Rows must be
    /// committed top to bottom (an encoder cannot reorder them); tiles inside a band may arrive in any order, so the
    /// sink counts filled columns per row instead of assuming a traversal order.
    /// </summary>
    private sealed class TileEncoderSink : IRegionSink, IDisposable
    {
        private StreamFrameEncoder? _frame;
        private readonly WorkingBuffer _band;
        private int[] _filled;
        private readonly CancellationToken _cancellation;
        private readonly IDisposable? _lease;
        private readonly int _bandCapacity;
        private int _bandTop;
        private int _bandRows;
        private bool _disposed;

        internal TileEncoderSink(int width, int height, WorkingFormat format, int bandRows, IPixelPool? pool,
            ExecuteOptions options, CancellationToken cancellationToken)
        {
            Width = width;
            Height = height;
            Format = format;
            _bandCapacity = bandRows;
            _bandRows = Math.Min(bandRows, height);
            _cancellation = cancellationToken;

            var bytes = checked((long)width * _bandRows * format.BytesPerPixel()
                + (long)bandRows * sizeof(int));
            if (options.MaxWorkingBytes > 0 && bytes > options.MaxWorkingBytes)
                throw new InvalidOperationException(
                    $"The encoder band ({width}x{_bandRows} {format}) needs {bytes} bytes, above " +
                    $"ExecuteOptions.MaxWorkingBytes = {options.MaxWorkingBytes}.");
            _lease = options.Workspace?.Reserve(bytes, "tile encoder band");
            try
            {
                _filled = new int[bandRows];
                _band = WorkingBuffer.Rent(width, _bandRows, format, pool);
            }
            catch
            {
                _filled = Array.Empty<int>();
                _lease?.Dispose();
                throw;
            }
        }

        public int Width { get; }
        public int Height { get; }
        public WorkingFormat Format { get; }

        internal void Attach(StreamFrameEncoder frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frame is not null)
                throw new InvalidOperationException("The tile encoder sink already has a frame.");
            _frame = frame;
        }

        public void WriteRegion(PixelRect region, PixelView source)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!region.FitsIn(Width, Height) || region.Width <= 0 || region.Height <= 0)
                throw new ArgumentException($"The region {region} does not lie inside {Width}x{Height}.", nameof(region));
            if (region.Width != source.Width || region.Height != source.Height)
                throw new ArgumentException("The source view is not the region's size.", nameof(source));
            if (source.Format != Format)
                throw new ArgumentException($"The source view is {source.Format}; the sink is {Format}.", nameof(source));

            if (region.Y < _bandTop || region.Y + region.Height > _bandTop + _bandRows)
                throw new InvalidOperationException(
                    "Tiles must arrive one band at a time, top to bottom: an encoder writes rows in order. " +
                    $"Got rows {region.Y}..{region.Y + region.Height - 1} while the band covers " +
                    $"{_bandTop}..{_bandTop + _bandRows - 1}.");

            var bytes = Format.BytesPerPixel();
            var localTop = region.Y - _bandTop;
            for (var y = 0; y < region.Height; y++)
            {
                source.Row(y).CopyTo(_band.View.Row(localTop + y).Slice(region.X * bytes, region.Width * bytes));
                _filled[localTop + y] += region.Width;
                if (_filled[localTop + y] > Width)
                    throw new InvalidOperationException(
                        $"Row {region.Y + y} received more than the {Width} columns the image has; a tile overlapped.");
            }

            for (var y = 0; y < _bandRows; y++)
            {
                if (_filled[y] != Width)
                    return;
            }

            FlushBand();
        }

        /// <summary>Writes the last band and checks that every row reached the encoder.</summary>
        internal void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bandTop != Height)
                throw new InvalidOperationException(
                    $"The tile walk covered {_bandTop} of {Height} rows; the encoder would have written a truncated image.");
        }

        private void FlushBand()
        {
            (_frame ?? throw new InvalidOperationException("The tile encoder sink has no attached frame."))
                .WriteRows(_band.View.Span, _bandRows, Width * Format.BytesPerPixel(), _cancellation);
            _bandTop += _bandRows;
            _bandRows = Math.Min(_bandCapacity, Height - _bandTop);
            Array.Clear(_filled);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _band.Dispose();
            }
            finally
            {
                _filled = Array.Empty<int>();
                _lease?.Dispose();
            }
        }
    }
}
