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
/// <param name="WorkerTapBytes">Vertical tap-index and weight scratch per worker.</param>
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
        var workers = Math.Max(1, options.MaxDegreeOfParallelism > 0
            ? options.MaxDegreeOfParallelism
            : RowParallel.Degree);

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
        const int StackTapLimit = 256;
        var tapBytesPerWorker = tapsY <= StackTapLimit
            ? (long)StackTapLimit * (sizeof(int) + sizeof(float))
            : (long)tapsY * (sizeof(int) + sizeof(float));
        var workerTapBytes = checked((long)workers * tapBytesPerWorker);
        var fixedBytes = checked(readyBytes + sourceRowBytes + workerRowBytes + workerTapBytes);
        var rowFloats = (long)destStride * sizeof(float);
        var ringBase = (long)(tapsY + 2) * rowFloats;
        var perChunk = rowFloats + sourceRowBytes;
        var shared = budget - fixedBytes;

        if (shared < ringBase)
        {
            throw new InvalidOperationException(
                $"Streaming this resize needs a {(ringBase + fixedBytes) / (1024 * 1024)} MiB working set "
                + $"({ringBase / (1024 * 1024)} MiB ring of {destWidth}-pixel rows, "
                + $"{readyBytes / (1024 * 1024)} MiB output batch, one source row, "
                + $"{workers} worker rows), above the {budget / (1024 * 1024)} MiB this path allows. "
                + "Reduce the downsample ratio, resize in two steps, lower the degree, or raise "
                + "ExecuteOptions.MaxWorkingBytes.");
        }

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
                + $"one source row, {workers} worker rows), above the {budget / (1024 * 1024)} MiB this path allows. "
                + "Reduce the downsample ratio, resize in two steps, lower the degree, or raise "
                + "ExecuteOptions.MaxWorkingBytes.");
        }

        return new ResizeWorkspace(chunkRows, ringRows, readyRows, workers, ringBytes, stagingBytes, readyBytes,
            workerRowBytes, sourceRowBytes, workerTapBytes, budget);
    }
}
