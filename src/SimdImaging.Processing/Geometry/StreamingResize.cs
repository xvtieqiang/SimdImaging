using System.Buffers;
using System.Runtime.CompilerServices;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// A resize presented as rows: destination rows are produced on demand from a forward source, so a resize can sit
/// between two plans (or between a region and a sink) without an intermediate image.
/// <para>The arithmetic is the separable path <see cref="Resample.Resize"/> uses — the same <c>WeightTable</c>, the
/// same premultiplied float rows, the same vertical convolution — so the bytes are identical.</para>
/// <para>Source rows are pulled in chunks so the horizontal pass can run one row per worker (a 4× downscale spends
/// four times as much work horizontally as vertically), and destination rows are emitted in batches so the vertical
/// pass does too. The budget is one account over everything this stage owns at once — the ring of horizontally
/// resized rows, the staging chunk, the output batch, the single source row, and one float row per worker — and it is
/// enforced by sizing the chunk from what is left, not by checking the ring alone. The reconstruction-weight tables
/// are not part of it: they belong to <see cref="MemoryBudget"/>'s shared weight cache. Nor are the decoder's and
/// encoder's own buffers, which this stage never sees.</para>
/// </summary>
internal sealed class ResizeRowSource : IRowSource, IDisposable
{
    /// <summary>Destination rows produced per batch.</summary>
    private const int OutputBatchRows = ResizeWorkspace.OutputBatchRows;

    private readonly IRowSource _source;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _destWidth;
    private readonly int _destHeight;
    private readonly WorkingFormat _format;
    private readonly int _srcRowBytes;
    private readonly int _destStride;
    private readonly WeightTable _weightsX;
    private readonly WeightTable _weightsY;
    private readonly CancellationToken _cancellation;

    // Nearest keeps the whole-image path's point-sampling fast path; every other filter takes the separable one.
    private readonly bool _nearest;
    private readonly int _tapX;
    private readonly int _tapY;

    private readonly int _chunkRows;
    private readonly int _ringRows;
    private readonly float[]? _ring;
    private readonly byte[]? _staging;
    private readonly byte[] _sourceRow;
    private readonly WorkingBuffer _ready;

    private int _pulled;       // source rows consumed
    private int _nextDest;     // first destination row not yet produced
    private int _readyRow;     // next ready row to hand out
    private int _readyCount;   // ready rows in the batch
    private bool _disposed;

    public ResizeRowSource(IRowSource source, int width, int height, Resampler resampler,
        ExecuteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        options ??= new ExecuteOptions();

        _source = source;
        _sourceWidth = source.Width;
        _sourceHeight = source.Height;
        _destWidth = width;
        _destHeight = height;
        _format = source.Format;
        _srcRowBytes = checked(_sourceWidth * _format.BytesPerPixel());
        _destStride = checked(width * 4);
        _cancellation = options.CancellationToken;

        _weightsX = WeightTable.Build(_sourceWidth, width, resampler);
        _weightsY = WeightTable.Build(_sourceHeight, height, resampler);
        _tapX = 0;
        _tapY = 0;
        _nearest = resampler == Resampler.Nearest && _format == WorkingFormat.Rgba8
                   && Resample.TryNearestTap(_weightsX, out _tapX)
                   && Resample.TryNearestTap(_weightsY, out _tapY);

        var workspace = ResizeWorkspace.Estimate(_sourceWidth, _sourceHeight, width, height, _weightsY.Taps,
            _nearest, _format, options);
        _chunkRows = workspace.ChunkRows;
        _ringRows = workspace.RingRows;

        // Reserve before the allocations: a reservation refused after them would leave the buffers nobody can
        // release, because a throwing constructor has no Dispose.
        ResidentBytes = workspace.TotalBytes;
        _workspaceLease = options.Workspace?.Reserve(ResidentBytes, "resize ring");
        float[]? ring = null;
        byte[]? staging = null;
        WorkingBuffer? ready = null;
        try
        {
            // Keep partial construction local: a failed later rent must return every earlier allocation because a
            // throwing constructor cannot be disposed by its caller.
            var sourceRow = new byte[_srcRowBytes];
            if (!workspace.IsNearest)
            {
                ring = FloatScratch.Rent(checked(_ringRows * _destStride));
                staging = ArrayPool<byte>.Shared.Rent(checked(_chunkRows * _srcRowBytes));
            }

            ready = WorkingBuffer.Rent(width, workspace.ReadyRows, _format);
            _sourceRow = sourceRow;
            _ring = ring;
            _staging = staging;
            _ready = ready ?? throw new InvalidOperationException("The resize output batch was not allocated.");
        }
        catch
        {
            ready?.Dispose();
            if (ring is not null)
                FloatScratch.Return(ring);
            if (staging is not null)
                ArrayPool<byte>.Shared.Return(staging);
            _workspaceLease?.Dispose();
            _workspaceLease = null;
            throw;
        }
    }

    public int Width => _destWidth;

    public int Height => _destHeight;

    /// <summary>Bytes this instance holds at once: ring, staging, output batch, source row and worker rows. The
    /// constructor sizes the chunk so this figure fits the budget; tests pin that invariant.</summary>
    internal long ResidentBytes { get; private set; }

    private IDisposable? _workspaceLease;

    public WorkingFormat Format => _format;

    public int ReadRows(Span<byte> destination, int stride, int maxRows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rowBytes = _destWidth * _format.BytesPerPixel();
        if (stride == 0)
            stride = rowBytes;

        var copied = 0;
        while (copied < maxRows)
        {
            if (_readyRow == _readyCount)
            {
                if (_nextDest >= _destHeight)
                    break;
                Produce();
            }

            var take = Math.Min(maxRows - copied, _readyCount - _readyRow);
            for (var y = 0; y < take; y++)
                _ready.View.Row(_readyRow + y).CopyTo(destination.Slice((copied + y) * stride, rowBytes));
            _readyRow += take;
            copied += take;
        }

        return copied;
    }

    /// <summary>Fills the ready batch with the next destination rows that can be completed.</summary>
    private void Produce()
    {
        _cancellation.ThrowIfCancellationRequested();
        _readyRow = 0;
        _readyCount = 0;

        if (_nearest)
        {
            ProduceNearest();
            return;
        }

        var batchRows = Math.Min(OutputBatchRows, _destHeight);
        while (_readyCount < batchRows && _nextDest < _destHeight)
        {
            var target = LastSourceRow(_nextDest);
            if (_pulled <= target)
            {
                // Pull a whole chunk, not just up to this row: the horizontal pass is parallel only when a batch has
                // enough rows, and a 4× downscale would otherwise hand it four rows at a time and run it on one core.
                var lastWanted = LastSourceRow(_destHeight - 1);
                var want = Math.Min(_chunkRows, lastWanted - _pulled + 1);
                if (want > 0)
                    PullAndConvert(want);
            }

            var start = _nextDest;
            while (_nextDest < _destHeight && LastSourceRow(_nextDest) <= _pulled - 1)
                _nextDest++;
            var end = _nextDest;
            var take = Math.Min(end - start, batchRows - _readyCount);
            if (take > 0)
                Emit(start, take);

            // Anything beyond the batch waits for the next call, still in order.
            _nextDest = start + take;
        }
    }

    private void ProduceNearest()
    {
        var batchRows = Math.Min(OutputBatchRows, _destHeight);
        var startsX = _weightsX.Starts;
        var startsY = _weightsY.Starts;
        var bandView = _ready.View;

        while (_readyCount < batchRows && _nextDest < _destHeight)
        {
            var sy = Math.Clamp(startsY[_nextDest] + _tapY, 0, _sourceHeight - 1);
            while (_pulled <= sy)
            {
                _cancellation.ThrowIfCancellationRequested();
                var read = _source.ReadRows(_sourceRow, 0, 1);
                if (read != 1)
                    throw new InvalidDataException(
                        $"The row source ended after {_pulled} of {_sourceHeight} rows.");
                _pulled++;
            }

            var destinationRow = bandView.Row(_readyCount);
            for (var x = 0; x < _destWidth; x++)
            {
                var from = (startsX[x] + _tapX) * 4;
                var pixel = Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(in _sourceRow[from]));
                Unsafe.WriteUnaligned(ref destinationRow[x * 4], pixel);
            }

            _readyCount++;
            _nextDest++;

            if (_readyCount == batchRows)
                return;
        }
    }

    /// <summary>Reads <paramref name="count"/> source rows and resizes them horizontally into the ring, one row per
    /// worker.</summary>
    private void PullAndConvert(int count)
    {
        var staging = _staging!;
        var baseRow = _pulled;
        for (var k = 0; k < count; k++)
        {
            _cancellation.ThrowIfCancellationRequested();
            var read = _source.ReadRows(_sourceRow, 0, 1);
            if (read != 1)
                throw new InvalidDataException(
                    $"The row source ended after {_pulled} of {_sourceHeight} rows.");
            _sourceRow.AsSpan(0, _srcRowBytes).CopyTo(staging.AsSpan(k * _srcRowBytes, _srcRowBytes));
            _pulled++;
        }

        var ring = _ring!;
        var ringRows = _ringRows;
        var destStride = _destStride;
        var sourceWidth = _sourceWidth;
        var format = _format;
        var weightsX = _weightsX;

        RowParallel.For(_destWidth, count, (from, to) =>
        {
            var floatRow = FloatScratch.Rent(sourceWidth * 4);
            try
            {
                for (var k = from; k < to; k++)
                {
                    var sy = baseRow + k;
                    Pixels.ReadRowPremultiplied(staging.AsSpan(k * _srcRowBytes, _srcRowBytes), sourceWidth, format,
                        floatRow);
                    Resample.HorizontalRow(floatRow, sourceWidth,
                        ring.AsSpan((sy % ringRows) * destStride, destStride), _destWidth, weightsX);
                }
            }
            finally
            {
                FloatScratch.Return(floatRow);
            }
        });
    }

    /// <summary>Produces destination rows <c>[start, start + count)</c> into the ready batch.</summary>
    private void Emit(int start, int count)
    {
        var bandView = _ready.View;
        var outBase = _readyCount;
        var ring = _ring!;

        // ForBands, not For: a 4× downscale completes only ~16 destination rows per chunk, which is below For's
        // 64 K-pixel gate and would run the vertical pass on one core.
        RowParallel.ForBands(count, (from, to) =>
        {
            var outputRow = FloatScratch.Rent(_destStride);
            try
            {
                for (var k = from; k < to; k++)
                {
                    var dy = start + k;
                    Resample.VerticalRow(ring, _ringRows, _sourceHeight, _destWidth, dy, _weightsY, outputRow);
                    Pixels.WriteRowUnpremultiplied(outputRow, _destWidth, _format, bandView.Row(outBase + k));
                }
            }
            finally
            {
                FloatScratch.Return(outputRow);
            }
        });

        _readyCount += count;
    }

    private int LastSourceRow(int dy) =>
        Math.Clamp(_weightsY.Starts[dy] + _weightsY.Taps - 1, 0, _sourceHeight - 1);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _workspaceLease?.Dispose();
        _workspaceLease = null;
        _ready.Dispose();
        if (_ring is not null)
            FloatScratch.Return(_ring);
        if (_staging is not null)
            ArrayPool<byte>.Shared.Return(_staging);
    }
}

/// <summary>Drives a <see cref="ResizeRowSource"/> into a sink: the single-resize entry.</summary>
internal static class StreamingResize
{
    /// <summary>What this stage may hold at once — ring, staging chunk, output batch, source row and one float row
    /// per worker — unless <see cref="ExecuteOptions.MaxWorkingBytes"/> asks for less.</summary>
    internal const long DefaultBudgetBytes = 24L << 20;

    /// <summary>Runs the resize, returns the destination rows written.</summary>
    public static int Execute(IRowSource source, IRowSink sink, int width, int height, Resampler resampler,
        ExecuteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        options ??= new ExecuteOptions();

        using var rows = new ResizeRowSource(source, width, height, resampler, options);
        return Pipeline.Copy(rows, sink, options.CancellationToken);
    }
}
