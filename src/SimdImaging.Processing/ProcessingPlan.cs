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
/// A step kernel that reads a second input as well: the overlay view the plan carries on the step, not one captured
/// by the closure. The executor supplies it, so the value that reaches the kernel is the one the plan recorded.
/// </summary>
/// <param name="source">The image being transformed.</param>
/// <param name="second">The step's second input, as recorded by <see cref="ProcessingPlan.Add(ImageOp, PixelView)"/>.</param>
/// <param name="destination">The buffer this call fills.</param>
/// <param name="destinationRect">The rectangle this call must fill.</param>
/// <param name="sourceRect">The part of <paramref name="source"/> that rectangle may read.</param>
public delegate void OpKernel2(PixelView source, PixelView second, PixelView destination, PixelRect destinationRect,
    PixelRect sourceRect);

/// <summary>
/// Writes <paramref name="rows"/> rows into <paramref name="destination"/> starting at
/// <paramref name="destinationY"/>, reading from a window whose first produced row is
/// <paramref name="sourceRowOffset"/> rows into it.
/// </summary>
public delegate void IntoKernel(PixelView sourceWindow, PixelView destination, int destinationY, int rows,
    int sourceRowOffset);

/// <summary>
/// One compiled step in a <see cref="ProcessingPlan"/>: a reference to the operator definition plus the kernel
/// closure the executor dispatches. The metadata is read from the definition, never copied.
/// </summary>
/// <remarks>
/// Before 2026-11-26 this type carried its own copy of the name, category, halos, scratch and whole-source flag,
/// filled at <c>Add</c> time. Two copies of the same facts drift: the estimator and the classifier read one while
/// the operator changed the other. A step is now a view over <see cref="ImageOp"/>, and the kernel is the only
/// value copied here — the fused executor dispatches it once per band and should not walk three references first.
/// </remarks>
public readonly record struct PlanStep
{
    internal PlanStep(ImageOp definition, byte[]? lut = null, PixelView? secondary = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Kernel = definition.Kernels.PlanKernel;
        Lut = lut;
        Secondary = secondary;
    }

    /// <summary>The operator this step was compiled from.</summary>
    internal ImageOp Definition { get; }

    /// <summary>
    /// The step's second input, when it has one. The view is owned by the caller and must outlive the execution;
    /// recording it on the step is what lets the executor supply it instead of a closure capturing it.
    /// </summary>
    internal PixelView? Secondary { get; init; }

    /// <summary>The second-input kernel, when the operator was built for injection; null for an operator that
    /// declares no second input.</summary>
    internal OpKernel2? SecondKernel => Definition.Kernels.SecondKernel;

    /// <summary>The exact rectangle the injected second input occupies in its declared coordinates, or null when the
    /// operator declares no injected rectangle.</summary>
    internal PixelRect? SecondaryRegion => Secondary is { } view && Definition.InjectedRect is { } rect
        ? rect(view)
        : null;

    /// <summary>Dispatches to the second-input kernel when the step has one, and checks that the view is present.</summary>
    internal void Invoke(PixelView source, PixelView destination, PixelRect destinationRect, PixelRect sourceRect)
    {
        if (SecondKernel is not { } kernel)
        {
            Kernel(source, destination, destinationRect, sourceRect);
            return;
        }

        if (Secondary is not { } second)
            throw new InvalidOperationException(
                $"The step '{Name}' needs its second input, which was not recorded on the step. Add it with " +
                "ProcessingPlan.Add(operator, second) instead of Add(operator).");

        kernel(source, second, destination, destinationRect, sourceRect);
    }

    /// <summary>Stable name, used in diagnostics.</summary>
    public string Name => Definition.Name;

    /// <summary>How the step reads its source; <see cref="OpCategory.Global"/> and <see cref="OpCategory.Composite"/>
    /// run whole.</summary>
    public OpCategory Category => Definition.Category;

    /// <summary>Columns read on each side of a destination pixel.</summary>
    public int HaloX => Definition.HaloX;

    /// <summary>Rows read on each side.</summary>
    public int HaloY => Definition.HaloY;

    /// <summary>The code that fills a rectangle. Copied once so a band dispatch is a field read.</summary>
    public OpKernel Kernel { get; }

    /// <summary>The 4×256 byte table when this step came from <see cref="ProcessingPlan.AddPointLut"/>;
    /// consecutive table steps compose into a single pass. Internal: the table is an execution detail,
    /// not part of the step's identity.</summary>
    internal byte[]? Lut { get; init; }

    /// <summary>
    /// What this step allocates privately, per worker, for a band of <c>height</c> rows — or null when the
    /// operator has not declared its footprint. Read from the definition; <c>MemoryEstimator</c> turns it into the
    /// per-worker term and refuses a ceiling it cannot enforce.
    /// </summary>
    public Func<int, int, int, WorkingFormat, long>? Scratch => Definition.Contract.Workspace.Estimate;

    /// <summary>
    /// True when the kernel reads the whole source image and not just its window — the general convolution's
    /// Mirror and Wrap borders reflect or wrap about the <b>image</b> edge, and a band-only source cannot supply
    /// that. A streaming executor must refuse such a plan instead of handing the kernel a band and letting it
    /// reflect about the band edge, which would seam.
    /// </summary>
    internal bool NeedsWholeSource => Definition.Contract.Footprint.CoversWholeImage;
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

    /// <summary>The trailing resize, for the compiled path; null when there is none.</summary>
    internal (int Width, int Height, Resampler Resampler)? ResizeSpec =>
        HasResize ? (_resizeWidth, _resizeHeight, _resizeResampler) : null;

    /// <summary>
    /// Ends the plan with a resize. Steps before it run at the source size, the resize changes it, and nothing may be
    /// added after — a plan with a resize <em>between</em> steps is not expressible yet (§6.7).
    /// <para>For a streamed run the rows are resized as they are produced, so the size change never becomes an
    /// intermediate image; for <see cref="Execute"/> the same-size steps run first and the result is resampled.</para>
    /// <para>In memory the idiomatic form is <c>image.Pipe().Resize(width, height)</c>; this exists so a file pipeline
    /// (<c>ImageTranscode</c>), which has no <c>Image</c> to pipe, can describe the same chain in one pass.</para>
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
    /// <para>Nothing is written when the plan is refused. Memory is the windows plus the operators' declared
    /// scratch, and does not grow with the source's width or height. The destination is the caller's: this call does
    /// not rent it, so it is not part of the estimate or the reservation — the facade and the tile walk reserve the
    /// targets they rent.</para>
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
        if (op.Kernels.SecondKernel is not null || op.Kernels.WholeImageSecond is not null)
            throw new InvalidOperationException(
                $"The step '{op.Name}' needs its second input injected; use Add(operator, second) so the plan " +
                "records the view.");
        return AddCore(op, secondary: null);
    }

    /// <summary>
    /// Appends an operator whose second input is supplied here and recorded on the step: the executor reads the
    /// view from the plan, the compiler derives the port's regions from the same contract, and the caller keeps
    /// ownership — the view must stay valid until the plan finishes.
    /// </summary>
    /// <param name="op">The operator; it must declare a second input and an injectable kernel.</param>
    /// <param name="second">The second input, in the format its port accepts.</param>
    /// <exception cref="ArgumentException">The operator has no injectable second input, or the format is not accepted.</exception>
    public ProcessingPlan Add(ImageOp op, PixelView second)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (op.Contract.Inputs.Count < 2 ||
            (op.Kernels.SecondKernel is null && op.Kernels.WholeImageSecond is null))
            throw new ArgumentException(
                $"The operator '{op.Name}' does not declare an injectable second input.", nameof(op));
        if (second.Width <= 0 || second.Height <= 0)
            throw new ArgumentException("The second input is empty.", nameof(second));
        if (!Supports(op.Contract.Inputs[1].Formats, second.Format))
            throw new ArgumentException(
                $"The second input is {second.Format}, which '{op.Name}' does not accept on that port.", nameof(second));

        return AddCore(op, second);
    }

    private ProcessingPlan AddCore(ImageOp op, PixelView? secondary)
    {
        if (op.Category == OpCategory.Geometric)
            throw new NotSupportedException(
                "A geometric step changes the image size, which a same-size plan cannot represent; run it outside the plan.");
        if (op.Contract.Output.Format is { } outputFormat)
            throw new NotSupportedException(
                $"The step '{op.Name}' changes the working format to {outputFormat}; a same-size plan keeps one " +
                "format, so run it as a compiled recipe section instead.");
        if (HasResize)
            throw new InvalidOperationException(
                "A resize ends the plan; add the steps before processing the size change, or run the resize outside.");

        _steps.Add(new PlanStep(op, secondary: secondary));
        _version++;
        return this;
    }

    private static bool Supports(WorkingFormatMask mask, WorkingFormat format) => mask.HasFlag(format switch
    {
        WorkingFormat.Rgba8 => WorkingFormatMask.Rgba8,
        WorkingFormat.Rgba16 => WorkingFormatMask.Rgba16,
        WorkingFormat.Rgba32F => WorkingFormatMask.Rgba32F,
        _ => WorkingFormatMask.None,
    });

    /// <summary>Appends a step that also declares its own scratch. Tests and the benchmark harness use this to
    /// build plans whose operators are not in <see cref="PlanOps"/>.</summary>
    /// <param name="scratch">What the step allocates per worker for a band of the given height; null when nothing
    /// is known, which the budget reports as a lower bound rather than as zero.</param>
    /// <param name="needsWholeSource">The kernel reads the whole image, not just its window.</param>
    internal ProcessingPlan Add(string name, OpCategory category, OpKernel kernel, int haloX, int haloY,
        Func<int, int, int, WorkingFormat, long>? scratch, bool needsWholeSource = false)
    {
        if (category == OpCategory.Geometric)
            throw new NotSupportedException(
                "A geometric step changes the image size, which a same-size plan cannot represent; run it outside the plan.");

        return Add(ImageOp.AdHoc(name, category, haloX, haloY, kernel, scratch, needsWholeSource));
    }

    /// <summary>
    /// Builds a plan from steps a <c>PipelineCompiler</c> already validated and segmented. No re-validation: the
    /// compiler produced these from <see cref="ImageOp"/> definitions, so the halos and scratch are the same ones
    /// the definition carries.
    /// </summary>
    internal static ProcessingPlan FromCompiled(IReadOnlyList<PlanStep> steps, int bandRows)
    {
        var plan = new ProcessingPlan { BandRows = bandRows };
        foreach (var step in steps)
            plan._steps.Add(step);
        plan._version++;
        return plan;
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
        _steps.Add(new PlanStep(ImageOp.PointLut(name, table), table));
        _version++;
        return this;
    }

    /// <summary>Byte-domain table application; the kernel checks the format because the table is
    /// defined on 8-bit samples only.</summary>
    internal static OpKernel BuildLutKernel(byte[] table) => (source, destination, rect, _) =>
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
        {
            var bytes = (long)source.Width * source.Height * source.Format.BytesPerPixel();
            if (options.MaxWorkingBytes > 0 && bytes > options.MaxWorkingBytes)
                throw new InvalidOperationException(
                    $"Copying an empty plan needs {bytes} bytes, above ExecuteOptions.MaxWorkingBytes = {options.MaxWorkingBytes}.");
            using var copyLease = options.Workspace?.Reserve(bytes, "empty plan destination");
            return Geometry.Transforms.Copy(source, pool);
        }

        var width = source.Width;
        var height = source.Height;
        var bandRows = BandRowsFor(width, height);
        var fusionAllowed = options.AllowFusion && observe is null;

        // Decided before the shape is built, not per segment: the segments are cut for one execution mode, and a
        // segment of several steps has no step-by-step form — the else branch runs its first step only.
        var fused = fusionAllowed && (options.ForceFusion ||
            FusionDecision.ShouldFuse(_steps, width, height, source.Format.BytesPerPixel()));
        var shape = ExecutionShapeFor(fused);
        var execution = shape.Steps;
        var segments = shape.Segments;
        var reason = observe is not null ? FusionFallbackReason.ObserverPresent
            : !options.AllowFusion ? FusionFallbackReason.AllowFusionDisabled
            : shape.HasCategoryBoundary ? FusionFallbackReason.CategoryBoundary
            : FusionFallbackReason.None;

        IDisposable? workspaceLease = null;
        if (options.MaxWorkingBytes > 0 || options.Workspace is not null)
        {
            // One formula, shared with the public estimator: the previous private version counted only the buffers
            // the executor rents and never looked at PlanStep.Scratch, so a ceiling could pass a plan whose
            // operators were about to allocate several more full-size planes.
            var estimate = MemoryEstimator.Estimate(source.Width, source.Height, source.Format, execution,
                bandRows, fused, options.MaxDegreeOfParallelism);

            if (options.MaxWorkingBytes > 0)
            {
                if (estimate.UndeclaredSteps > 0 && !options.AllowUnestimatedScratch)
                    throw new InvalidOperationException(
                        $"The plan sets a {options.MaxWorkingBytes}-byte ceiling but {estimate.UndeclaredSteps} of " +
                        $"its {execution.Count} steps declare no scratch, so the estimate ({estimate.PeakBytes} " +
                        "bytes) is a lower bound and cannot enforce that ceiling. Declare the operator's scratch, " +
                        "raise the ceiling, drop it, or set ExecuteOptions.AllowUnestimatedScratch to accept the " +
                        "lower bound.");

                if (estimate.PeakBytes > options.MaxWorkingBytes)
                    throw new InvalidOperationException(
                        $"The plan would need an estimated {estimate.PeakBytes} bytes of working set, above the " +
                        $"{options.MaxWorkingBytes}-byte ceiling in ExecuteOptions.MaxWorkingBytes.");
            }

            workspaceLease = options.Workspace?.Reserve(estimate.PeakBytes, "plan");
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
                        step.Invoke(current, destination.View, whole, whole);
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
                                step.Invoke(current, destination.View, band,
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
                                    step.Invoke(current, destination.View, band,
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
            workspaceLease?.Dispose();
            throw;
        }

        workspaceLease?.Dispose();
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

                    steps.Add(new PlanStep(ImageOp.PointLut(name, table), table));
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
