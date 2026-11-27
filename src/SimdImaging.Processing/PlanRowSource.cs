using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// A same-size plan presented as rows: the band pump turned inside out so another stage can pull from it. Each band is
/// produced on demand, through the same <c>FusedExecutor.RunWindow</c> the whole-image path uses, and handed out a row
/// at a time; the next band is produced only once the current one has been consumed.
/// <para>This is what lets a resize sit between two plans: <c>plan → rows</c>, <c>resize</c>, <c>plan → rows</c> is a
/// chain of sources, and the encoder pulls from the last one. Nothing is materialised at either end.</para>
/// <para>
/// <b>One stage is one section.</b> The compiled plan's operator sections become one of these each, so
/// <see cref="_haloTotal"/> is that section's Σhalo, the band comes from the sequential rule (this pump runs its bands
/// one at a time and parallelises inside them), and the whole section re-runs over the overlap on every band. A
/// section boundary links two pumps instead of materialising a frame; nothing resets to a whole image.
/// </para>
/// </summary>
internal sealed class PlanRowSource : IRowSource, IDisposable
{
    private readonly ProcessingPlan _plan;
    private readonly IRowSource _source;
    private readonly ExecuteOptions _options;
    private readonly IPixelPool? _pool;
    private IDisposable? _workspaceLease;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bandRows;
    private readonly int _haloTotal;

    private readonly WorkingBuffer? _cache;
    private readonly WorkingBuffer? _output;
    private readonly WorkingBuffer? _scratchA;
    private readonly WorkingBuffer? _scratchB;

    private int _bandTop;       // top of the band being produced
    private int _cacheTop;
    private int _cacheRows;
    private int _bandRow;       // next row of the current band to hand out
    private int _bandRowCount;  // rows in the current band
    private PixelView _band;    // the current band, a slice of the output window
    private bool _disposed;

    public PlanRowSource(ProcessingPlan plan, IRowSource source, ExecuteOptions? options = null,
        IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new ExecuteOptions();

        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException(
                $"A row source must have a positive size; got {source.Width}x{source.Height}.", nameof(source));

        var refusal = StreamingExecutor.Classify(plan.Steps, out var refusingStep);
        if (refusal != StreamRefusal.None)
            throw new NotSupportedException(
                $"The plan cannot stream ({refusal} at '{refusingStep}'); check ProcessingPlan.CanStream first.");

        _plan = plan;
        _source = source;
        _options = options;
        _pool = pool;
        _width = source.Width;
        _height = source.Height;

        var steps = plan.Steps;
        _haloTotal = 0;
        for (var i = 0; i < steps.Count; i++)
            _haloTotal += steps[i].HaloY;

        // Bands run one at a time (the caller pulls), so the band has to keep its own row loop busy; the whole-image
        // rule would hand it 32 rows at 8K and leave every operator on one thread. `withinOneFrame` is this pump's
        // own constraint: holding more than a materialised frame is worse than not streaming at all.
        _bandRows = SequentialBands.For(plan, _width, _height, source.Format, options.MaxDegreeOfParallelism,
            withinOneFrame: true);
        if (steps.Count == 0)
            return;

        var maxWindowRows = Math.Min(_height, _bandRows + (2 * _haloTotal));
        var windows = SequentialBands.Windows(steps);
        var estimate = WorkingSetBudget.Measure(steps, options, _width, _height, _width, maxWindowRows,
            source.Format, windows, out var undeclared);
        WorkingSetBudget.Ensure(estimate, undeclared, steps.Count, options, "Streaming");

        // Reserve before the allocations: a reservation refused after them would leave the buffers nobody can
        // release, because a throwing constructor has no Dispose. The estimate includes the declared scratch
        // for every worker, not only the window buffers.
        _workspaceLease = options.Workspace?.Reserve(estimate, "stream windows");
        WorkingBuffer? cache = null;
        WorkingBuffer? output = null;
        WorkingBuffer? scratchA = null;
        WorkingBuffer? scratchB = null;
        try
        {
            // Keep partial construction local: a failed later rent must return every earlier allocation because a
            // throwing constructor cannot be disposed by its caller.
            cache = WorkingBuffer.Rent(_width, maxWindowRows, source.Format, _pool);
            output = WorkingBuffer.Rent(_width, maxWindowRows, source.Format, _pool);
            if (windows > 2)
                scratchA = WorkingBuffer.Rent(_width, maxWindowRows, source.Format, _pool);
            if (windows > 3)
                scratchB = WorkingBuffer.Rent(_width, maxWindowRows, source.Format, _pool);

            _cache = cache;
            _output = output;
            _scratchA = scratchA;
            _scratchB = scratchB;
        }
        catch
        {
            scratchB?.Dispose();
            scratchA?.Dispose();
            output?.Dispose();
            cache?.Dispose();
            _workspaceLease?.Dispose();
            _workspaceLease = null;
            throw;
        }
    }

    public int Width => _width;

    public int Height => _height;

    public WorkingFormat Format => _source.Format;

    /// <summary>Bands produced so far; the executor reports it.</summary>
    public int Bands { get; private set; }

    public int ReadRows(Span<byte> destination, int stride, int maxRows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rowBytes = _width * Format.BytesPerPixel();
        if (stride == 0)
            stride = rowBytes;

        if (_plan.Steps.Count == 0)
            return _source.ReadRows(destination, stride, maxRows);

        var copied = 0;
        while (copied < maxRows)
        {
            if (_bandRow == _bandRowCount)
            {
                if (_bandTop >= _height)
                    break;
                ProduceBand();
            }

            var take = Math.Min(maxRows - copied, _bandRowCount - _bandRow);
            for (var y = 0; y < take; y++)
                _band.Row(_bandRow + y).CopyTo(destination.Slice((copied + y) * stride, rowBytes));
            _bandRow += take;
            copied += take;
        }

        return copied;
    }

    private void ProduceBand()
    {
        _options.CancellationToken.ThrowIfCancellationRequested();

        // The band carries the parallelism: inside this scope the plan adapters' EnterNested is a no-op, so the
        // operator's own row loop sees the configured degree.
        using var pump = RowParallel.EnterTopLevel(_options.MaxDegreeOfParallelism);

        var bandTop = _bandTop;
        var bottom = Math.Min(bandTop + _bandRows, _height);
        var winTop = Math.Max(0, bandTop - _haloTotal);
        var winBottom = Math.Min(_height, bottom + _haloTotal);
        var windowRows = winBottom - winTop;

        var skip = winTop - _cacheTop;
        var keep = skip >= 0 && skip < _cacheRows ? Math.Min(_cacheRows - skip, windowRows) : 0;
        if (keep > 0 && skip > 0)
            CopyRows(_cache!.View, skip, _cache.View, 0, keep);

        _cacheRows = keep;
        _cacheTop = winTop;

        var missing = windowRows - keep;
        while (missing > 0)
        {
            var destination = _cache!.View.Slice(new PixelRect(0, keep, _width, missing));
            var read = _source.ReadRows(destination.Span, destination.Stride, missing);
            if (read <= 0)
            {
                // A source that stopped because the caller cancelled must surface as a cancellation, not as a
                // truncated stream: the two mean different things to whoever catches them.
                _options.CancellationToken.ThrowIfCancellationRequested();
                throw new InvalidDataException(
                    $"The row source ended after {winTop + keep} of {_height} rows; a band still needed {missing}.");
            }

            keep += read;
            missing -= read;
        }

        _cacheRows = keep;

        var sourceWindow = _cache!.View.Slice(new PixelRect(0, 0, _width, windowRows));
        var finished = _output!.View.Slice(new PixelRect(0, 0, _width, windowRows));
        var a = _scratchA is null ? default : _scratchA.View.Slice(new PixelRect(0, 0, _width, windowRows));
        // A two-step chain has one intermediate, so the second slot is the first view rather than a rented buffer:
        // the alternation writes `window` on step 0 and `output` on step 1, and never reads `next`.
        var b = _scratchB is null ? a : _scratchB.View.Slice(new PixelRect(0, 0, _width, windowRows));

        // Absolute rows, not a band index: the band height is this class's own choice, so handing over
        // `bandTop / _bandRows` would make the executor re-derive a row it was just given, and any future change to
        // how bands advance would silently move the band instead of failing.
        FusedExecutor.RunWindow(_width, _height, sourceWindow, finished, winTop, bandTop, bottom, _plan.Steps, 0,
            _plan.Steps.Count, _haloTotal, a, b);

        _band = finished.Slice(new PixelRect(0, bandTop - winTop, _width, bottom - bandTop));
        _bandRow = 0;
        _bandRowCount = bottom - bandTop;
        _bandTop = bottom;
        Bands++;
    }

    private static void CopyRows(PixelView from, int fromRow, PixelView to, int toRow, int rows)
    {
        for (var y = 0; y < rows; y++)
            from.Row(fromRow + y).CopyTo(to.Row(toRow + y));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _cache?.Dispose();
            _output?.Dispose();
            _scratchA?.Dispose();
            _scratchB?.Dispose();
        }
        finally
        {
            _workspaceLease?.Dispose();
            _workspaceLease = null;
        }
    }
}
