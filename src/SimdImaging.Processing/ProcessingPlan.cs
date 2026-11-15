using System.Diagnostics;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// One step of a plan: what it is called, how much context it needs, and the code that fills a destination
/// rectangle. The kernel receives absolute coordinates on both views, so it never has to reason about which
/// band it is in.
/// </summary>
/// <param name="source">The previous step's image, in full.</param>
/// <param name="destination">The buffer being filled. Its contents are undefined; the kernel must write every
/// byte of <paramref name="destinationRect"/>, alpha included, or the result depends on what the pool
/// happened to hand out.</param>
/// <param name="destinationRect">The rectangle this call must fill, in destination coordinates.</param>
/// <param name="sourceRect">The part of <paramref name="source"/> that rectangle may read, halo included and
/// clamped to the image.</param>
public delegate void OpKernel(PixelView source, PixelView destination, PixelRect destinationRect,
    PixelRect sourceRect);

/// <summary>One named operator in a <see cref="ProcessingPlan"/>.</summary>
/// <param name="Name">Stable name, used in diagnostics.</param>
/// <param name="Category">How the step reads its source; <see cref="OpCategory.Global"/>,
/// <see cref="OpCategory.Composite"/> and <see cref="OpCategory.Geometric"/> are run whole.</param>
/// <param name="HaloX">Columns read on each side of a destination pixel.</param>
/// <param name="HaloY">Rows read on each side.</param>
/// <param name="Kernel">The code that fills a rectangle.</param>
public readonly record struct PlanStep(string Name, OpCategory Category, int HaloX, int HaloY, OpKernel Kernel)
{
    /// <summary>The 4×256 byte table when this step came from <see cref="ProcessingPlan.AddPointLut"/>;
    /// consecutive table steps compose into a single pass. Internal: the table is an execution detail,
    /// not part of the step's identity.</summary>
    internal byte[]? Lut { get; init; }

    /// <summary>
    /// What this step allocates privately, per worker, for a band of <c>height</c> rows — or null when it
    /// allocates nothing the executor should know about. Declared by the operator, because only it knows:
    /// Richardson-Lucy's four float planes are forty-eight bytes a pixel and no amount of geometry
    /// inspection from the outside recovers that.
    /// <para>The height passed in is the band the step will actually see, which is why this is a function
    /// and not a number. <c>MemoryEstimator</c> reads it and reports the largest declaration.</para>
    /// </summary>
    public Func<int, int, WorkingFormat, long>? Scratch { get; init; }
}

/// <summary>
/// Called after a step finished, while that step's output is still alive.
/// <para>
/// <b>The view is only valid for the duration of the call.</b> The buffer behind it is the plan's
/// ping-pong slot: it becomes the next step's source and is disposed on the following iteration.
/// Copy whatever you need — and expect the copy to cost more than the step did, so this is a
/// verification hook, not something to leave switched on in production.
/// </para>
/// </summary>
/// <param name="index">Zero-based step index.</param>
/// <param name="name">The step's name, as passed to <c>ProcessingPlan.Add</c>.</param>
/// <param name="result">The step's full output.</param>
public delegate void PlanObserver(int index, string name, PixelView result);

/// <summary>
/// A sequence of operators executed over one source with a bounded working set: same-size steps run in row
/// bands, so a neighbourhood kernel only ever sees its own band plus its halo.
/// <para>
/// The destination of each step is one buffer the size of the image — that part is unavoidable — but the
/// scratch a kernel allocates inside a call is band-sized, which is what keeps a large-radius filter from
/// materialising several full planes.
/// </para>
/// <para>
/// <see cref="OpCategory.Global"/> steps (histogram, quantise, CLAHE) still run whole: their answer depends on
/// every pixel, so splitting them would change the result. <see cref="OpCategory.Geometric"/> steps are
/// rejected — they change the image size, which a same-size chain cannot represent.
/// </para>
/// </summary>
public sealed class ProcessingPlan
{
    /// <summary>Rows per band when a step does not pick its own.</summary>
    public const int DefaultBandRows = 128;

    private readonly List<PlanStep> _steps = [];

    /// <summary>Bumped on every <c>Add</c>; the execution cache below is keyed on it.</summary>
    private int _version;

    /// <summary>
    /// Folded (fused) and unfolded execution shapes, cached because they depend only on the steps.
    /// A plan is normally built once and executed many times — that is the shape §1.3's "pooling + plan reuse"
    /// describes — and rebuilding the fold, the step arrays and the segmentation on every call was the bulk of
    /// the residual per-call allocation.
    /// </summary>
    private ExecutionShape? _fusedShape;
    private ExecutionShape? _plainShape;
    private int _shapeVersion = -1;

    /// <summary>The frozen defaults: fusion on, no diagnostics, no ceiling, no cancellation.</summary>
    private static readonly ExecuteOptions DefaultOptions = new();

    /// <summary>The steps, in execution order.</summary>
    public IReadOnlyList<PlanStep> Steps => _steps;

    /// <summary>Rows per band for this plan.</summary>
    public int BandRows { get; init; } = DefaultBandRows;

    /// <summary>Appends a step.</summary>
    /// <param name="name">Stable name.</param>
    /// <param name="category">How the step reads its source.</param>
    /// <param name="kernel">The code that fills a rectangle.</param>
    /// <param name="haloX">Columns of context, for neighbourhood kernels.</param>
    /// <param name="haloY">Rows of context, for neighbourhood kernels.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or <paramref name="kernel"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A halo is negative.</exception>
    /// <exception cref="NotSupportedException">The category changes the image size.</exception>
    public ProcessingPlan Add(string name, OpCategory category, OpKernel kernel, int haloX = 0, int haloY = 0) =>
        Add(name, category, kernel, haloX, haloY, scratch: null);

    /// <summary>Appends a step that also declares its own scratch.</summary>
    /// <param name="scratch">What the step allocates per worker for a band of the given height; null when it
    /// allocates nothing worth reporting. <see cref="MemoryEstimator"/> turns these into the per-worker term.</param>
    public ProcessingPlan Add(string name, OpCategory category, OpKernel kernel, int haloX, int haloY,
        Func<int, int, WorkingFormat, long>? scratch)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentOutOfRangeException.ThrowIfNegative(haloX);
        ArgumentOutOfRangeException.ThrowIfNegative(haloY);
        if (category == OpCategory.Geometric)
            throw new NotSupportedException(
                "A geometric step changes the image size, which a same-size plan cannot represent; run it outside the plan.");

        _steps.Add(new PlanStep(name, category, haloX, haloY, kernel) { Scratch = scratch });
        _version++;
        return this;
    }

    /// <summary>
    /// Appends a point step defined by an explicit 4×256 byte table (R, G, B, A — 1024 bytes). Consecutive
    /// table steps are composed into one table at execution time and run in a single pass over the band,
    /// which is exact because each step in the chain quantises to bytes anyway. Rgba8 only: another
    /// working format throws <see cref="NotSupportedException"/> when the plan runs (no silent fallback).
    /// </summary>
    /// <param name="name">Stable name, used in diagnostics.</param>
    /// <param name="rgbaTable">Four 256-entry tables, concatenated: R then G then B then A.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or the table is not 1024 bytes.</exception>
    public ProcessingPlan AddPointLut(string name, ReadOnlySpan<byte> rgbaTable)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (rgbaTable.Length != 4 * 256)
            throw new ArgumentException(
                $"The table must be 4 channels × 256 entries, not {rgbaTable.Length} bytes.", nameof(rgbaTable));

        var table = rgbaTable.ToArray();
        _steps.Add(new PlanStep(name, OpCategory.Point, 0, 0, BuildLutKernel(table)) { Lut = table });
        _version++;
        return this;
    }

    /// <summary>Byte-domain table application; the kernel checks the format because the table is
    /// defined on 8-bit samples only.</summary>
    private static OpKernel BuildLutKernel(byte[] table) => (source, destination, rect, _) =>
    {
        if (source.Format != WorkingFormat.Rgba8)
            throw new NotSupportedException(
                $"AddPointLut is defined for Rgba8 only; this plan is running on {source.Format}.");

        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            var from = source.Row(y);
            var to = destination.Row(y);
            var start = rect.X * 4;
            var end = start + rect.Width * 4;
            for (var i = start; i < end; i += 4)
            {
                to[i] = table[from[i]];
                to[i + 1] = table[256 + from[i + 1]];
                to[i + 2] = table[512 + from[i + 2]];
                to[i + 3] = table[768 + from[i + 3]];
            }
        }
    };

    /// <summary>
    /// Runs every step and returns a new buffer. Same-size steps run in row bands; global and composite
    /// steps run whole.
    /// <para><b>Kernels may be invoked concurrently.</b> Bands execute in parallel, so a kernel must be
    /// thread-safe and must only touch the rows inside <c>destinationRect</c>. This changed on 2026-11-06:
    /// the band loop used to be a plain sequential <c>for</c>, which left the whole plan single-threaded and
    /// depended on each operator's own <c>Parallel.For</c> re-entering the pool once per band. Parallel bands
    /// took a three-step point chain from 14.5 ms to 10.1 ms and a blur chain from 67.3 ms to 25.9 ms.</para>
    /// <para><b>Consecutive band-able steps are fused by default.</b> Each segment runs through band-sized
    /// windows (two per worker) instead of one full-size buffer per step, and the output is byte-identical
    /// to the step-by-step path — <c>ExecuteOptions.AllowFusion = false</c> restores it. A
    /// <see cref="PlanObserver"/> forces the step-by-step path, because the observer has to see every
    /// full-size intermediate; that fallback is reported as
    /// <see cref="FusionFallbackReason.ObserverPresent"/>.</para>
    /// </summary>
    /// <param name="source">The input pixels.</param>
    /// <param name="pool">Where the intermediate and output buffers are rented.</param>
    /// <param name="observe">Optional per-step hook, called while the step's output is still alive. Off by
    /// default: the callback is invoked on the calling thread, once per step, after that step finished.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="BandRows"/> is not positive.</exception>
    public WorkingBuffer Execute(PixelView source, IPixelPool? pool = null, PlanObserver? observe = null) =>
        ExecuteWithOptions(source, DefaultOptions, pool, observe);

    /// <summary>Runs the plan with explicit execution knobs. See
    /// <see cref="ExecuteWithOptions(PixelView,ExecuteOptions,out PlanReport?,IPixelPool?,PlanObserver?)"/>
    /// for the full contract.</summary>
    public WorkingBuffer ExecuteWithOptions(PixelView source, ExecuteOptions options,
        IPixelPool? pool = null, PlanObserver? observe = null) =>
        ExecuteWithOptions(source, options, out _, pool, observe);

    /// <summary>Runs the plan with explicit execution knobs and optionally collects a
    /// <see cref="PlanReport"/>.</summary>
    /// <param name="source">The input pixels.</param>
    /// <param name="options">Degree, working-set ceiling, fusion switch, diagnostics and cancellation.</param>
    /// <param name="report">The diagnostics report, or null when <c>options.Diagnostics</c> is off. With
    /// diagnostics off no report object, timing call or pool wrapper is created.</param>
    /// <param name="pool">Where buffers are rented. Defaults to <c>ArrayPixelPool.Shared</c>.</param>
    /// <param name="observe">Forces the step-by-step path when present.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="BandRows"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The estimated working set exceeds
    /// <see cref="ExecuteOptions.MaxWorkingBytes"/>.</exception>
    public WorkingBuffer ExecuteWithOptions(PixelView source, ExecuteOptions options, out PlanReport? report,
        IPixelPool? pool = null, PlanObserver? observe = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BandRows);

        report = null;
        if (_steps.Count == 0)
            return Geometry.Transforms.Copy(source, pool);

        var width = source.Width;
        var height = source.Height;
        var fused = options.AllowFusion && observe is null;
        var shape = ExecutionShapeFor(fused);
        var execution = shape.Steps;
        var segments = shape.Segments;
        var reason = observe is not null ? FusionFallbackReason.ObserverPresent
            : !options.AllowFusion ? FusionFallbackReason.AllowFusionDisabled
            : shape.HasCategoryBoundary ? FusionFallbackReason.CategoryBoundary
            : FusionFallbackReason.None;

        if (options.MaxWorkingBytes > 0)
        {
            var estimate = EstimateWorkingBytes(source, execution, segments, fused,
                options.MaxDegreeOfParallelism);
            if (estimate > options.MaxWorkingBytes)
                throw new InvalidOperationException(
                    $"The plan would need an estimated {estimate} bytes of working set, above the " +
                    $"{options.MaxWorkingBytes}-byte ceiling in ExecuteOptions.MaxWorkingBytes.");
        }

        var counting = options.Diagnostics ? new CountingPool(pool ?? ArrayPixelPool.Shared) : null;
        var execPool = (IPixelPool?)counting ?? pool;
        var startTicks = options.Diagnostics ? Stopwatch.GetTimestamp() : 0;
        var cancellation = options.CancellationToken;

        var current = source;
        WorkingBuffer? owned = null;
        // The buffer rented for the CURRENT segment, until ownership is handed over to `owned`.
        // Without this, a kernel or observer throwing between Rent and the hand-over leaked a
        // whole image-sized buffer: the catch below only ever disposed the previous step's frame.
        WorkingBuffer? pending = null;

        try
        {
            foreach (var (first, count) in segments)
            {
                cancellation.ThrowIfCancellationRequested();

                WorkingBuffer destination;
                if (fused && count > 1)
                {
                    destination = FusedExecutor.ExecuteSegment(current, execution, first, count, BandRows,
                        options.MaxDegreeOfParallelism, cancellation, execPool);
                }
                else
                {
                    var step = execution[first];
                    destination = WorkingBuffer.Rent(width, height, current.Format, execPool);
                    pending = destination;
                    var whole = new PixelRect(0, 0, width, height);

                    if (step.Category is OpCategory.Global or OpCategory.Composite)
                    {
                        step.Kernel(current, destination.View, whole, whole);
                    }
                    else
                    {
                        // Bands run in PARALLEL. The old loop was a plain `for`, so a plan was effectively
                        // single-threaded and leaned entirely on each operator's own Parallel.For — which meant
                        // re-entering the pool once per band and rebuilding the operator's per-call setup
                        // (a 256-entry LUT, for one) 24 times per 1080p step. Measured: three point steps cost
                        // 14.5 ms that way against 5.1 ms for the same three operators called directly.
                        // Bands write disjoint rows, so the worker count cannot change a byte.
                        if (options.MaxDegreeOfParallelism == 1)
                        {
                            // One worker is a plain loop: RowParallel would wrap it in a delegate, and that
                            // closure is a per-call allocation for a path that is meant to be allocation-free.
                            var bands = BandCountFor(height);
                            for (var b = 0; b < bands; b++)
                            {
                                var top = b * BandRows;
                                var band = new PixelRect(0, top, width, Math.Min(BandRows, height - top));
                                step.Kernel(current, destination.View, band,
                                    MapSource(band, step.HaloX, step.HaloY, width, height));
                            }
                        }
                        else
                        {
                            RowParallel.ForBands(BandCountFor(height), (start, end) =>
                            {
                                for (var b = start; b < end; b++)
                                {
                                    var top = b * BandRows;
                                    var band = new PixelRect(0, top, width, Math.Min(BandRows, height - top));
                                    step.Kernel(current, destination.View, band,
                                        MapSource(band, step.HaloX, step.HaloY, width, height));
                                }
                            }, options.MaxDegreeOfParallelism, cancellation);
                        }
                    }

                    // Before the dispose below, not after: `destination` is what `owned` is about to become, and
                    // the *previous* owned buffer is still the one being released. Calling the observer after the
                    // swap would hand out a view of a buffer the next iteration is about to dispose.
                    observe?.Invoke(first, step.Name, destination.View);
                }

                owned?.Dispose();
                owned = destination;
                pending = null;
                current = destination.View;
            }
        }
        catch
        {
            // `pending` and `owned` never point at the same buffer: pending is cleared as soon as
            // ownership is handed over. Disposing both exactly once covers kernel, band-worker,
            // observer and hand-over failures without a double release.
            pending?.Dispose();
            pending = null;
            owned?.Dispose();
            owned = null;
            throw;
        }

        if (options.Diagnostics)
        {
            report = new PlanReport
            {
                SegmentCount = segments.Count,
                Segments = DescribeSegments(execution, segments, fused, shape.FirstOriginal, shape.OriginalCount),
                FallbackReason = reason,
                WorkerCount = RowParallel.LastWorkerCount,
                BandCount = CountBands(execution, segments, fused, height),
                PoolRentCount = counting!.Rents,
                PoolReturnCount = counting.Returns,
                ElapsedTicks = Stopwatch.GetTimestamp() - startTicks,
            };
        }

        return owned!;
    }

    private int BandCountFor(int height) => (height + BandRows - 1) / BandRows;

    /// <summary>Everything about one folding mode that does not change between executions.</summary>
    private sealed class ExecutionShape
    {
        public required List<PlanStep> Steps { get; init; }
        public required List<(int First, int Count)> Segments { get; init; }
        public required int[] FirstOriginal { get; init; }
        public required int[] OriginalCount { get; init; }
        public required bool HasCategoryBoundary { get; init; }
    }

    /// <summary>
    /// The shape for <paramref name="fold"/>, built once per version of the plan.
    /// <para>
    /// Cached rather than rebuilt: a plan is normally built once and executed many times, and the fold, the
    /// step arrays and the segmentation depend only on the steps. Rebuilding them was the bulk of the residual
    /// per-call allocation that kept §1.3's "0 B/op with pooling and plan reuse" out of reach.
    /// </para>
    /// </summary>
    private ExecutionShape ExecutionShapeFor(bool fold)
    {
        if (_shapeVersion != _version)
        {
            _fusedShape = null;
            _plainShape = null;
            _shapeVersion = _version;
        }

        var cached = fold ? _fusedShape : _plainShape;
        if (cached is not null)
            return cached;

        var steps = BuildExecutionSteps(fold, out var firsts, out var counts);
        var segments = fold ? FusionPlanner.Segment(steps) : SingleSteps(steps.Count);

        var boundary = false;
        foreach (var (first, _) in segments)
        {
            if (FusionPlanner.IsBandable(steps[first]))
                continue;
            boundary = true;
            break;
        }

        var shape = new ExecutionShape
        {
            Steps = steps,
            Segments = segments,
            FirstOriginal = firsts,
            OriginalCount = counts,
            HasCategoryBoundary = boundary,
        };

        if (fold)
            _fusedShape = shape;
        else
            _plainShape = shape;

        return shape;
    }

    /// <summary>
    /// Folds consecutive <see cref="AddPointLut"/> steps into one step with a composed 4×256 table.
    /// Composition is exact by construction: the sequential path writes bytes between steps, and
    /// <c>T_AB[c][v] = T_B[c][T_A[c][v]]</c> is the same byte lookup. Folding is only done when fusion
    /// is allowed — an observer has to see every step's own output.
    /// </summary>
    internal List<PlanStep> BuildExecutionSteps(bool fold, out int[] firstOriginal, out int[] originalCount)
    {
        var steps = new List<PlanStep>(_steps.Count);
        var firsts = new List<int>(_steps.Count);
        var counts = new List<int>(_steps.Count);

        var index = 0;
        while (index < _steps.Count)
        {
            if (fold && _steps[index].Lut is { } first)
            {
                var end = index + 1;
                var table = (byte[])first.Clone();
                while (end < _steps.Count && _steps[end].Lut is { } nextLut)
                {
                    // Per channel: entry v of channel c becomes T_next[c][ T_current[c][v] ].
                    // A flat `table[i] = next[table[i]]` would index the R table for every channel.
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var offset = channel * 256;
                        for (var v = 0; v < 256; v++)
                            table[offset + v] = nextLut[offset + table[offset + v]];
                    }

                    end++;
                }

                if (end - index > 1)
                {
                    var name = _steps[index].Name;
                    for (var i = index + 1; i < end; i++)
                        name += " + " + _steps[i].Name;

                    steps.Add(new PlanStep(name, OpCategory.Point, 0, 0, BuildLutKernel(table)));
                    firsts.Add(index);
                    counts.Add(end - index);
                    index = end;
                    continue;
                }
            }

            steps.Add(_steps[index]);
            firsts.Add(index);
            counts.Add(1);
            index++;
        }

        firstOriginal = firsts.ToArray();
        originalCount = counts.ToArray();
        return steps;
    }

    /// <summary>One segment per step: the segmentation the executor uses when fusion is off, so the
    /// report always describes what actually ran.</summary>
    internal static List<(int First, int Count)> SingleSteps(int count)
    {
        var single = new List<(int First, int Count)>(count);
        for (var i = 0; i < count; i++)
            single.Add((i, 1));

        return single;
    }

    private int CountBands(IReadOnlyList<PlanStep> steps, List<(int First, int Count)> segments, bool fused,
        int height)
    {
        var bands = 0;
        foreach (var (first, count) in segments)
            bands += (fused && count > 1) || FusionPlanner.IsBandable(steps[first]) ? BandCountFor(height) : 1;
        return bands;
    }

    private static List<PlanSegment> DescribeSegments(IReadOnlyList<PlanStep> steps,
        List<(int First, int Count)> segments, bool fused, int[] firstOriginal, int[] originalCount)
    {
        var described = new List<PlanSegment>(segments.Count);
        foreach (var (first, count) in segments)
        {
            var name = steps[first].Name;
            var originalSteps = originalCount[first];
            for (var i = 1; i < count; i++)
            {
                name += " + " + steps[first + i].Name;
                originalSteps += originalCount[first + i];
            }

            described.Add(new PlanSegment(firstOriginal[first], originalSteps,
                fused && count > 1 && FusionPlanner.IsBandable(steps[first]), name));
        }

        return described;
    }

    /// <summary>Documented estimate (not a measurement): the live set is the previous result plus the
    /// destination, plus two band windows per worker for every fused segment.</summary>
    private long EstimateWorkingBytes(PixelView source, IReadOnlyList<PlanStep> steps,
        List<(int First, int Count)> segments, bool fused, int degree)
    {
        var full = (long)source.Width * source.Height * source.Format.BytesPerPixel();
        var peak = 2 * full;
        if (!fused)
            return peak;

        foreach (var (first, count) in segments)
        {
            if (count <= 1)
                continue;

            peak = Math.Max(peak, full + FusedExecutor.EstimateSegmentBytes(
                source, steps, first, count, BandRows, degree));
        }

        return peak;
    }

    /// <summary>The destination band widened by the halo and clamped to the image.</summary>
    private static PixelRect MapSource(PixelRect band, int haloX, int haloY, int width, int height)
    {
        var left = Math.Max(0, band.X - haloX);
        var top = Math.Max(0, band.Y - haloY);
        var right = Math.Min(width, band.X + band.Width + haloX);
        var bottom = Math.Min(height, band.Y + band.Height + haloY);

        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
