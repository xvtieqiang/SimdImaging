using System.Collections.ObjectModel;
using System.Diagnostics;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Geometry;
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

/// <summary>
/// Writes <paramref name="rows"/> rows into <paramref name="destination"/> starting at
/// <paramref name="destinationY"/>, reading from a window whose first produced row is
/// <paramref name="sourceRowOffset"/> rows into it.
/// </summary>
public delegate void IntoKernel(PixelView sourceWindow, PixelView destination, int destinationY, int rows,
    int sourceRowOffset);

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
    public Func<int, int, int, WorkingFormat, long>? Scratch { get; init; }

    /// <summary>
    /// True when the kernel reads the whole source image and not just its window — the general convolution's
    /// Mirror and Wrap borders reflect or wrap about the <b>image</b> edge, and a band-only source cannot supply
    /// that. A streaming executor must refuse such a plan instead of handing the kernel a band and letting it
    /// reflect about the band edge, which would seam.
    /// </summary>
    internal bool NeedsWholeSource { get; init; }
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

    /// <summary>
    /// Pixels one band should hold, which is what the default band height is really expressing.
    /// </summary>
    /// <remarks>
    /// A band is a cache-sized unit, so its size belongs in pixels: 128 rows is 245 K pixels at 1920 wide and 983 K
    /// at 7680, and a separable filter keeps eight float planes of the band per worker. The number is anchored so that
    /// 1920-wide images keep the 128 rows they were tuned at, and only wider images get a shorter band.
    /// </remarks>
    public const int DefaultBandPixels = 128 * 1920;

    private readonly List<PlanStep> _steps = [];
    private int _resizeWidth;
    private int _resizeHeight;
    private Resampler _resizeResampler;
    private ReadOnlyCollection<PlanStep>? _readOnlySteps;

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
    /// <remarks>
    /// Wrapped, not cast: returning the backing <see cref="List{T}"/> as <see cref="IReadOnlyList{T}"/> still hands
    /// out an object a caller can cast back and mutate, which would change a plan between its estimate and its run.
    /// </remarks>
    public IReadOnlyList<PlanStep> Steps => _readOnlySteps ??= _steps.AsReadOnly();

    /// <summary>Whether the plan ends with a size change.</summary>
    public bool HasResize => _resizeWidth > 0;

    /// <summary>
    /// Ends the plan with a resize. Steps before it run at the source size, the resize changes it, and nothing may be
    /// added after — a plan with a resize <em>between</em> steps is not expressible yet (§6.7).
    /// <para>For a streamed run the rows are resized as they are produced, so the size change never becomes an
    /// intermediate image; for <see cref="Execute"/> the same-size steps run first and the result is resampled.</para>
    /// <para>In memory the idiomatic form is <c>image.Pipe().Resize(width, height)</c>; this exists so a file pipeline
    /// (<c>Image.ProcessTo</c>), which has no <c>Image</c> to pipe, can describe the same chain in one pass.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="InvalidOperationException">The plan already ends with a resize.</exception>
    public ProcessingPlan Resize(int width, int height, Resampler resampler = Resampler.Bicubic)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (HasResize)
            throw new InvalidOperationException(
                "The plan already ends with a resize; a second one is a second call, not a second step.");

        _resizeWidth = width;
        _resizeHeight = height;
        _resizeResampler = resampler;
        _version++;
        return this;
    }

    /// <summary>Appends another plan's steps. Used by the file builder to keep one plan per side of a resize.</summary>
    internal void Append(ProcessingPlan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (HasResize || other.HasResize)
            throw new InvalidOperationException("A plan that ends with a resize cannot be merged with another.");
        if (other._steps.Count == 0)
            return;

        _steps.AddRange(other._steps);
        _version++;
    }

    /// <summary>The size <see cref="Execute"/> and a streamed run produce for a given input.</summary>
    public (int Width, int Height) OutputSize(int width, int height) =>
        HasResize ? (_resizeWidth, _resizeHeight) : (width, height);

    /// <summary>
    /// Whether this plan can run against a forward-only row source (<see cref="TryExecuteTo"/>): every step must be
    /// able to produce its output from a band of rows. A <see cref="OpCategory.Global"/> or
    /// <see cref="OpCategory.Composite"/> step cannot, and neither can a step whose kernel reads the whole image
    /// (the general convolution's Mirror and Wrap borders).
    /// <para>A refused plan is refused, not approximated: run it over a whole image instead. The answer depends on
    /// the plan alone, so a caller can decide before it opens a stream it could not re-read.</para>
    /// </summary>
    public StreamRefusal CanStream() => StreamingExecutor.Classify(_steps, out _);

    /// <summary>
    /// Runs the plan over a forward-only row source, writing finished rows to <paramref name="sink"/>: the
    /// file-level streaming counterpart of <see cref="Execute"/>. Nothing but a band of rows and the operators'
    /// own scratch is ever held, so memory does not grow with the image height.
    /// <para>The result is byte-identical to <c>Execute</c> over the same pixels: both use the same halo
    /// recursion. An empty plan is a straight copy of the rows.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The source reports a non-positive size.</exception>
    /// <exception cref="InvalidDataException">The source ends before its declared height.</exception>
    /// <exception cref="InvalidOperationException"><see cref="ExecuteOptions.MaxWorkingBytes"/> cannot be met, or
    /// cannot be enforced because a step declares no scratch and
    /// <see cref="ExecuteOptions.AllowUnestimatedScratch"/> is false.</exception>
    /// <exception cref="OperationCanceledException">The token in <paramref name="options"/> was cancelled.</exception>
    public StreamResult TryExecuteTo(IRowSource source, IRowSink sink, ExecuteOptions? options = null) =>
        HasResize
            ? StreamingExecutor.ExecuteWithResize(this, source, sink, _resizeWidth, _resizeHeight, _resizeResampler,
                options)
            : StreamingExecutor.Execute(this, source, sink, options);

    /// <summary>
    /// Whether this plan can run over a rectangle of a <see cref="IRegionSource"/>. The conditions are the same as
    /// <see cref="CanStream"/> — a <see cref="OpCategory.Global"/> or <see cref="OpCategory.Composite"/> step, or one
    /// whose kernel reads the whole image, cannot produce its output from a window.
    /// </summary>
    public RegionRefusal CanExecuteRegion() => RegionExecutor.Classify(_steps, HasResize, out _);

    /// <summary>
    /// Runs the plan over <paramref name="region"/> only, reading through <paramref name="source"/>: the
    /// two-dimensional counterpart of <see cref="Execute"/>. The result is byte-identical to
    /// <c>Execute(whole).Slice(region)</c> for every pixel of the region.
    /// <para>Nothing is written when the plan is refused. Memory is four windows plus the destination, and does not
    /// grow with the source's width or height.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The region is empty or lies outside the source, or the destination is not
    /// the region's size or format.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="BandRows"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException"><see cref="ExecuteOptions.MaxWorkingBytes"/> cannot be met, or
    /// cannot be enforced because a step declares no scratch and
    /// <see cref="ExecuteOptions.AllowUnestimatedScratch"/> is false.</exception>
    /// <exception cref="OperationCanceledException">The token in <paramref name="options"/> was cancelled.</exception>
    public RegionResult TryExecuteRegion(IRegionSource source, PixelRect region, PixelView destination,
        ExecuteOptions? options = null, IPixelPool? pool = null) =>
        RegionExecutor.Execute(this, source, region, destination, options, pool);

    /// <summary>Rows per band for this plan.</summary>
    public int BandRows { get; init; } = DefaultBandRows;

    // Internal on purpose: a caller who can pass a halo can pass the wrong one, and the wrong one does not
    // throw - it returns a wrong pixel at a band boundary. Plans are built from ImageOps, which owns the number.
    internal ProcessingPlan Add(string name, OpCategory category, OpKernel kernel, int haloX = 0, int haloY = 0) =>
        Add(name, category, kernel, haloX, haloY, scratch: null);

    /// <summary>
    /// Appends an operator that carries its own support radius. This is the only public way to add a step: a
    /// caller cannot state a halo, so a caller cannot state one wrongly.
    /// </summary>
    public ProcessingPlan Add(ImageOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return Add(op.Name, op.Category, op.Kernel, op.HaloX, op.HaloY, op.Scratch, op.NeedsWholeSource);
    }

    /// <summary>Appends a step that also declares its own scratch.</summary>
    /// <param name="scratch">What the step allocates per worker for a band of the given height; null when it
    /// allocates nothing worth reporting. <see cref="MemoryEstimator"/> turns these into the per-worker term.</param>
    /// <param name="needsWholeSource">The kernel reads the whole image, not just its window (see
    /// <see cref="PlanStep.NeedsWholeSource"/>).</param>
    internal ProcessingPlan Add(string name, OpCategory category, OpKernel kernel, int haloX, int haloY,
        Func<int, int, int, WorkingFormat, long>? scratch, bool needsWholeSource = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentOutOfRangeException.ThrowIfNegative(haloX);
        ArgumentOutOfRangeException.ThrowIfNegative(haloY);
        if (category == OpCategory.Geometric)
            throw new NotSupportedException(
                "A geometric step changes the image size, which a same-size plan cannot represent; run it outside the plan.");
        if (HasResize)
            throw new InvalidOperationException(
                "A resize ends the plan; add the steps before processing the size change, or run the resize outside.");

        _steps.Add(new PlanStep(name, category, haloX, haloY, kernel)
        {
            Scratch = scratch,
            NeedsWholeSource = needsWholeSource,
        });
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

        if (HasResize)
            throw new InvalidOperationException(
                "A resize ends the plan; add the steps before processing the size change, or run the resize outside.");

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
        var buffer = ExecuteSameSize(source, options, out report, pool, observe);
        if (!HasResize)
            return buffer;

        try
        {
            // The steps ran at the source size; the resize is the last stage, so it sees their output whole. That is
            // the same thing a streamed run does band by band, byte for byte.
            return Resample.Resize(buffer.View, _resizeWidth, _resizeHeight, _resizeResampler, pool);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private WorkingBuffer ExecuteSameSize(PixelView source, ExecuteOptions options, out PlanReport? report,
        IPixelPool? pool, PlanObserver? observe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BandRows);

        report = null;
        if (_steps.Count == 0)
            return Geometry.Transforms.Copy(source, pool);

        var width = source.Width;
        var height = source.Height;
        var bandRows = BandRowsFor(width, height);
        var fusionAllowed = options.AllowFusion && observe is null;

        // Decided before the shape is built, not per segment: the segments are cut for one execution mode, and a
        // segment of several steps has no step-by-step form — the else branch runs its first step only.
        var fused = fusionAllowed && (options.ForceFusion ||
            ShouldFuse(_steps, width, height, source.Format.BytesPerPixel()));
        var shape = ExecutionShapeFor(fused);
        var execution = shape.Steps;
        var segments = shape.Segments;
        var reason = observe is not null ? FusionFallbackReason.ObserverPresent
            : !options.AllowFusion ? FusionFallbackReason.AllowFusionDisabled
            : shape.HasCategoryBoundary ? FusionFallbackReason.CategoryBoundary
            : FusionFallbackReason.None;

        if (options.MaxWorkingBytes > 0)
        {
            // One formula, shared with the public estimator: the previous private version counted only the buffers
            // the executor rents and never looked at PlanStep.Scratch, so a ceiling could pass a plan whose
            // operators were about to allocate several more full-size planes.
            var estimate = MemoryEstimator.Estimate(source.Width, source.Height, source.Format, execution,
                bandRows, fused, options.MaxDegreeOfParallelism);

            if (estimate.UndeclaredSteps > 0 && !options.AllowUnestimatedScratch)
                throw new InvalidOperationException(
                    $"The plan sets a {options.MaxWorkingBytes}-byte ceiling but {estimate.UndeclaredSteps} of its "
                    + $"{execution.Count} steps declare no scratch, so the estimate ({estimate.PeakBytes} bytes) is "
                    + "a lower bound and cannot enforce that ceiling. Declare the operator's scratch, raise the "
                    + "ceiling, drop it, or set ExecuteOptions.AllowUnestimatedScratch to accept the lower bound.");

            if (estimate.PeakBytes > options.MaxWorkingBytes)
                throw new InvalidOperationException(
                    $"The plan would need an estimated {estimate.PeakBytes} bytes of working set, above the " +
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
                    destination = FusedExecutor.ExecuteSegment(current, execution, first, count, bandRows,
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
                            var bands = BandCountFor(height, bandRows);
                            for (var b = 0; b < bands; b++)
                            {
                                var top = b * bandRows;
                                var band = new PixelRect(0, top, width, Math.Min(bandRows, height - top));
                                step.Kernel(current, destination.View, band,
                                    MapSource(band, step.HaloX, step.HaloY, width, height));
                            }
                        }
                        else
                        {
                            // Without fusion a band is just a unit of work for one worker, so it is sized for the
                            // thread count rather than for the fusion window: at 360 rows and 128-row bands the old
                            // split gave three workers on a sixteen-core machine and lost 40-80% to the stepwise
                            // chain it was supposed to match (`fusion` mode, "点算子 ×3" at 640×360).
                            var stepwiseBands = StepwiseBandCount(height, bandRows);
                            var stepwiseRows = (height + stepwiseBands - 1) / stepwiseBands;

                            RowParallel.ForBands(stepwiseBands, (start, end) =>
                            {
                                for (var b = start; b < end; b++)
                                {
                                    var top = b * stepwiseRows;
                                    var band = new PixelRect(0, top, width, Math.Min(stepwiseRows, height - top));
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
                BandCount = CountBands(execution, segments, fused, width, height),
                PoolRentCount = counting!.Rents,
                PoolReturnCount = counting.Returns,
                ElapsedTicks = Stopwatch.GetTimestamp() - startTicks,
            };
        }

        return owned!;
    }

    /// <summary>The band height for this image: the caller's, or the pixel budget expressed in rows.</summary>
    internal int BandRowsFor(int width, int height)
    {
        if (BandRows != DefaultBandRows)
            return BandRows;

        // Two bounds, and the tighter one wins: the pixel budget keeps a band cache-sized on wide images, and
        // height/degree keeps it small enough to keep every worker busy on short ones. Without the second bound a
        // 1920-wide budget gives 640×360 a single band of 384 rows — one worker for the whole image.
        var byPixels = DefaultBandPixels / Math.Max(1, width);
        var byThreads = Math.Max(8, height / Math.Max(1, RowParallel.Degree));
        return Math.Clamp(Math.Min(byPixels, byThreads), 8, 1024);
    }

    private int BandCountFor(int height, int rows) => (height + rows - 1) / rows;

    /// <summary>
    /// Bands for an unfused segment: enough to keep every worker busy, no finer than a few rows each.
    /// </summary>
    private int StepwiseBandCount(int height, int bandRows)
    {
        // An explicit BandRows is a request about the band geometry and is honoured as given; only the default is
        // reinterpreted, because it was chosen as a fusion window and is too coarse to fill the threads.
        if (BandRows != DefaultBandRows)
            return BandCountFor(height, bandRows);

        var byRows = Math.Max(1, height / 32);
        return Math.Max(1, Math.Min(RowParallel.Degree, byRows));
    }

    /// <summary>
    /// Whether this segment is worth fusing, decided from what the segment contains and how big the image is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fusing trades two things: it saves one full-frame write and read per intermediate, and it pays for the halo
    /// rows recomputed at every band boundary plus a fixed cost per band. Which side wins is not a property of the
    /// kernel size — it is a property of whether the steps can use the machine some other way.
    /// </para>
    /// <para>
    /// An earlier version of this rule read "fuse when the segment contains a small kernel", and at the time it was
    /// right: <c>Morph3x3</c>, <c>Convolve3x3</c> and the combine loop in <c>Sharpen</c> ran on one thread, so the
    /// fused band was the only path that used more than one core, and a 3×3 chain read 0.45 (fusion 2.2x faster).
    /// Those operators now parallelise over rows themselves. Re-measured with the two sides interleaved in one
    /// timing call (six sizes, three runs, plan ÷ step-by-step):
    /// </para>
    /// <para>
    /// <code>
    ///           160x120  320x180  640x360  1080p   4K      8K
    /// point x2    0.59     0.42     0.84    0.83   0.83    0.80
    /// 3x3 x2      0.25     0.27     1.37    1.23   1.05    1.11
    /// 3x3 x3      0.46     0.32     1.08    1.00   1.03    1.16
    /// blur+2pt    1.07     0.87     1.12    1.05   1.05    1.24
    /// </code>
    /// </para>
    /// <para>
    /// The curve is not monotonic, and the reason is the parallel gate: below it a step runs on one thread, so
    /// fusion is the only source of parallelism and wins 2-4x; above it every step is already using the cores, and
    /// fusion keeps only the traffic it saves, which at 640x360 is less than the halo it recomputes. A chain of
    /// points is the one shape that wins at every size above the gate, because it has no halo to recompute. What
    /// is left for the size test is the purpose fusion was built for: not materialising N full frames.
    /// </para>
    /// <para>
    /// The traffic is counted in the source's own bytes per pixel, over the intermediates fusion actually removes.
    /// Both halves of that sentence were once wrong: the rule multiplied by 4 whatever the format was (wrong by 2x
    /// for <c>Rgba16</c> and 4x for <c>Rgba32F</c>), and it used <c>steps.Count - 1</c> even though a Global step
    /// splits the chain and the executor then fuses each side separately, so a plan whose only bandable run is two
    /// steps long was credited with avoiding a whole chain's worth of frames.
    /// </para>
    /// </remarks>
    private static bool ShouldFuse(List<PlanStep> steps, int width, int height, int bytesPerPixel)
    {
        var maxHalo = 0;
        foreach (var step in steps)
            maxHalo = Math.Max(maxHalo, Math.Max(step.HaloX, step.HaloY));

        var pixels = (long)width * height;

        // Below the parallel gate every step would run on the calling thread: fusion is the only way this image
        // uses more than one core, and it measured 0.25-0.46 at 160x120 and 320x180.
        if (pixels < RowParallel.MinWorkPixels)
            return true;

        // A chain with no halo costs nothing to fuse: there is no window to recompute, only a frame to skip.
        // Measured 0.80-0.84 from 640x360 to 8K for the chains in the table above.
        if (maxHalo == 0)
            return true;

        // Everything else keeps fusion for what it was built for — not materialising N full frames. Count them
        // per contiguous bandable run: a category boundary already forces a full frame, so only the steps on this
        // side of it are traffic fusion can avoid. Counted without allocating a segment list — this runs per call.
        long avoided = 0;
        var run = 0;
        foreach (var step in steps)
        {
            if (FusionPlanner.IsBandable(step))
            {
                run++;
                continue;
            }

            if (run > 1)
                avoided += (long)(run - 1) * pixels * bytesPerPixel;
            run = 0;
        }

        if (run > 1)
            avoided += (long)(run - 1) * pixels * bytesPerPixel;

        return avoided > FuseIntermediateBytes;
    }

    /// <summary>
    /// The worth of the full-size intermediates fusion avoids, in bytes, above which fusing is justified even when
    /// the steps are expensive: below this the pass is cheap enough that its arithmetic, not its memory traffic,
    /// sets the time.
    /// </summary>
    private const long FuseIntermediateBytes = 64L * 1024 * 1024;

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
        int width, int height)
    {
        var rows = BandRowsFor(width, height);
        var bands = 0;
        foreach (var (first, count) in segments)
            bands += (fused && count > 1) || FusionPlanner.IsBandable(steps[first]) ? BandCountFor(height, rows) : 1;
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
