using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// One resize stage's resident bytes, by the arithmetic that sizes it. The constructor of
/// <see cref="ResizeRowSource"/> and the compiler's workspace report both call this, so the number a budget refuses
/// and the number an explanation prints cannot drift apart.
/// </summary>
/// <param name="ChunkRows">Source rows pulled per horizontal pass; 0 for the nearest path.</param>
/// <param name="RingRows">Rows kept in the horizontally-resized ring; 0 for the nearest path.</param>
/// <param name="ReadyRows">Destination rows in the batch handed to the sink.</param>
/// <param name="Workers">Workers the horizontal and vertical passes may use.</param>
/// <param name="RingBytes">The ring's bytes.</param>
/// <param name="StagingBytes">The staging chunk's bytes.</param>
/// <param name="ReadyBytes">The output batch's bytes.</param>
/// <param name="WorkerRowBytes">One float row per worker.</param>
/// <param name="WorkerTapBytes">Vertical tap-index and weight scratch allocated once per worker.</param>
/// <param name="BudgetBytes">The budget this estimate was fitted to.</param>
internal readonly record struct ResizeWorkspace(
    int ChunkRows,
    int RingRows,
    int ReadyRows,
    int Workers,
    long RingBytes,
    long StagingBytes,
    long ReadyBytes,
    long WorkerRowBytes,
    long SourceRowBytes,
    long WorkerTapBytes,
    long BudgetBytes)
{
    /// <summary>Destination rows produced per batch.</summary>
    internal const int OutputBatchRows = 64;

    /// <summary>The largest chunk a horizontal pass takes; more only delays the first emitted row.</summary>
    internal const int MaxChunkRows = 256;

    /// <summary>Chunk rows below which the one-frame budget cap is not applied: at that size the cap has stopped
    /// saving memory and only adds pull round-trips, so the original budget stands.</summary>
    internal const int MinUsefulChunkRows = 8;

    /// <summary>What the ring and the staging chunk may share, before the fixed buffers are deducted.</summary>
    internal const long DefaultBudgetBytes = 24L << 20;

    internal bool IsNearest => ChunkRows == 0 && RingRows == 0;

    /// <summary>Everything this stage holds at once.</summary>
    internal long TotalBytes => checked(RingBytes + StagingBytes + ReadyBytes + WorkerRowBytes + SourceRowBytes + WorkerTapBytes);

    /// <summary>Fits the stage to the budget, or throws with the parts named.</summary>
    /// <exception cref="InvalidOperationException">The minimum working set does not fit.</exception>
    internal static ResizeWorkspace Estimate(int sourceWidth, int sourceHeight, int destWidth, int destHeight,
        int tapsY, bool nearest, WorkingFormat format, ExecuteOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destHeight);
        options ??= new ExecuteOptions();

        var sourceRowBytes = checked(sourceWidth * format.BytesPerPixel());
        var destStride = checked(destWidth * 4);
        var budget = options.MaxWorkingBytes > 0
            ? Math.Min(options.MaxWorkingBytes, DefaultBudgetBytes)
            : DefaultBudgetBytes;

        // Everything besides the ring and the staging chunk: the batch handed to the sink, the one source row, and
        // one float row per worker. FloatScratch is thread-local, so the row is per worker; the horizontal and
        // vertical passes do not overlap, so one row covers both. Nearest samples on the calling thread.
        var readyRows = Math.Min(OutputBatchRows, destHeight);
        var readyBytes = checked((long)readyRows * destWidth * format.BytesPerPixel());
        // PullAndConvert uses RowParallel.For: even at MaxChunkRows it can use at most one worker per
        // RowsPerWorker rows (and may use fewer because of the pixel threshold). Emit uses ForBands, capped by
        // the ready batch. The passes are sequential, so row scratch uses the larger count; Tap arrays are only
        // live during Emit and use its exact worker cap.
        // Emit's degree is capped by the pixels in one batch (`ResizeRowSource` does the same), because ForBands
        // has no pixel gate of its own: a 160x90 resize ran 1.19x slower on sixteen workers than on one. Below
        // MinWorkPixels the pass is one thread; above it, one worker per RowsPerWorker rows. The estimate must use
        // that figure or it charges rows no worker will ever hold.
        var degree = RowParallel.ResolveDegree(options.MaxDegreeOfParallelism);
        var emitDegree = (long)destWidth * readyRows < RowParallel.MinWorkPixels
            ? 1
            : Math.Max(1, Math.Min(degree, (readyRows + RowParallel.RowsPerWorker - 1) / RowParallel.RowsPerWorker));
        var pullWorkers = RowParallel.ResolveWorkers(destWidth, MaxChunkRows, degree);
        var emitWorkers = Math.Min(emitDegree, readyRows);
        var workers = Math.Max(pullWorkers, emitWorkers);

        if (nearest)
        {
            if (readyBytes + sourceRowBytes > budget)
            {
                throw new InvalidOperationException(
                    $"Streaming this nearest resize needs a {(readyBytes + sourceRowBytes) / (1024 * 1024)} MiB "
                    + $"working set ({readyBytes / (1024 * 1024)} MiB output batch and one source row), above the "
                    + $"{budget / (1024 * 1024)} MiB this path allows. Lower the degree or raise "
                    + "ExecuteOptions.MaxWorkingBytes.");
            }

            return new ResizeWorkspace(0, 0, readyRows, workers, 0, 0, readyBytes, 0, sourceRowBytes, 0, budget);
        }

        var workerRowBytes = checked((long)workers * Math.Max(sourceWidth * 4, destStride) * sizeof(float));
        var tapBytesPerWorker = tapsY <= Resample.VerticalStackTapLimit
            ? (long)Resample.VerticalStackTapLimit * (sizeof(int) + sizeof(float))
            : (long)tapsY * (sizeof(int) + sizeof(float));
        // Tap arrays are used only by Emit; no pull-worker scratch is simultaneously live.
        var tapWorkers = emitWorkers;
        var workerTapBytes = checked((long)tapWorkers * tapBytesPerWorker);
        var fixedBytes = checked(readyBytes + sourceRowBytes + workerRowBytes + workerTapBytes);
        var rowFloats = (long)destStride * sizeof(float);
        var ringBase = (long)(tapsY + 2) * rowFloats;
        var perChunk = rowFloats + sourceRowBytes;

        // The horizontal pass pulls one chunk of source rows at a time, and the ring holds the horizontally
        // resized ones. The chunk cap is a constant (256), so on a short image the stage's floor is set by the
        // cap rather than by the picture: at 640x360 a 256-row chunk is 71% of the height, and the stage held four
        // frames' worth — more memory than the frame it exists to avoid, and slower than the whole-image path.
        // The budget is therefore capped by one source frame, the same comparison `SequentialBands` makes for the
        // row pump.
        // <para>The cap is only worth applying when it still leaves a usable chunk. On a high-bit-depth image the
        // ring is float rows (16 bytes per pixel against an 8-byte source frame), so one frame buys a handful of
        // rows: Rgba16 160x100 → 80x50 would drop from 256 rows to 4, which saves nothing on a stage that small
        // and pays for extra pull round-trips. There the cap is skipped and `MaxWorkingBytes` remains the only
        // limit. Both figures are compared after the same 256-row clamp, because that is the chunk that runs.
        // </para>
        var oneFrame = checked((long)sourceWidth * sourceHeight * format.BytesPerPixel());
        var uncappedChunk = (int)Math.Clamp((budget - fixedBytes - ringBase) / perChunk, 1, MaxChunkRows);
        var frameChunk = (int)Math.Clamp((oneFrame - fixedBytes - ringBase) / perChunk, 0, MaxChunkRows);
        var effectiveBudget = frameChunk >= Math.Min(MinUsefulChunkRows, uncappedChunk)
            ? Math.Min(budget, oneFrame)
            : budget;

        var shared = effectiveBudget - fixedBytes;
        if (shared < ringBase)
        {
            throw new InvalidOperationException(
                $"Streaming this resize needs a {(ringBase + fixedBytes) / (1024 * 1024)} MiB working set "
                + $"({ringBase / (1024 * 1024)} MiB ring of {destWidth}-pixel rows, "
                + $"{readyBytes / (1024 * 1024)} MiB output batch, one source row, "
                + $"{workers} worker rows and {workerTapBytes / (1024 * 1024)} MiB vertical tap scratch), "
                    + $"above the {effectiveBudget / (1024 * 1024)} MiB this path allows. "
                + "Reduce the downsample ratio, resize in two steps, lower the degree, or raise "
                + "ExecuteOptions.MaxWorkingBytes.");
        }

        // The horizontal pass pulls one chunk of source rows at a time, and the ring holds the horizontally
        // resized ones. Both are fixed by the cap rather than by the image, so on a short image the stage's floor
        // can exceed the frame it was built to avoid: at 640x360 the 256-row chunk is 71% of the height, and the
        // stage held four frames' worth while also running slower than the whole-image path. The budget is
        // therefore capped by one source frame — the same comparison `SequentialBands` makes for the row pump —
        // so a short image does not pay a tall image's floor. It is never capped below what the buffers need:
        // that case is a real budget refusal and belongs to `MaxWorkingBytes`, not to this rule.
        var chunkRows = (int)Math.Clamp((shared - ringBase) / perChunk, 1, MaxChunkRows);
        var ringRows = Math.Min(sourceHeight, tapsY + chunkRows + 2);
        var ringBytes = (long)ringRows * rowFloats;
        var stagingBytes = (long)chunkRows * sourceRowBytes;

        if (ringBytes + stagingBytes > shared)
        {
            throw new InvalidOperationException(
                $"Streaming this resize needs a {(ringBytes + stagingBytes + fixedBytes) / (1024 * 1024)} MiB "
                + $"working set ({ringBytes / (1024 * 1024)} MiB ring, "
                + $"{stagingBytes / (1024 * 1024)} MiB staging, {readyBytes / (1024 * 1024)} MiB output batch, "
                + $"one source row, {workers} worker rows, {workerTapBytes / (1024 * 1024)} MiB vertical tap scratch), "
                + $"above the {effectiveBudget / (1024 * 1024)} MiB this path allows. "
                + "Reduce the downsample ratio, resize in two steps, lower the degree, or raise "
                + "ExecuteOptions.MaxWorkingBytes.");
        }

        return new ResizeWorkspace(chunkRows, ringRows, readyRows, workers, ringBytes, stagingBytes, readyBytes,
            workerRowBytes, sourceRowBytes, workerTapBytes, effectiveBudget);
    }
}
