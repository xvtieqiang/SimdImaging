using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>Why a plan was not run against a row source and sink.</summary>
public enum StreamRefusal : byte
{
    /// <summary>Nothing refused it; the plan ran.</summary>
    None = 0,

    /// <summary>A <see cref="OpCategory.Global"/> step (histogram, quantise, CLAHE) needs a whole-image statistic
    /// before it writes a pixel, and a forward-only source cannot be read twice.</summary>
    GlobalStep,

    /// <summary>A <see cref="OpCategory.Composite"/> step combines two inputs, and a row source supplies one.</summary>
    CompositeStep,

    /// <summary>A step reads the whole source image and not just its window (the general convolution's Mirror and
    /// Wrap borders). Handing it a band would reflect about the band edge and seam.</summary>
    StepNeedsWholeSource,

    /// <summary>The decode asked for a codec-negotiated scale (<c>LoadOptions.PreferNativeScale</c>) or another
    /// variant that must be chosen from the whole header. Not a refusal of the plan: the input has to be read
    /// whole before its size is known, so the facade materialises instead of streaming.</summary>
    DecodeNeedsWholeInput,

    /// <summary>A geometry step has no row-source form, so a stream would have to materialise at that point. A
    /// resize, crop, pad and horizontal flip do have one; rotation, warp and canvas changes do not. Appended after
    /// <see cref="DecodeNeedsWholeInput"/> so the existing values keep their numbers.</summary>
    GeometricStep,

    /// <summary>A step changes the working format; a row chain cannot splice two formats, so the plan materialises.</summary>
    FormatChange,

    /// <summary>A step declares order-dependent or per-call state (<see cref="ExecutionOrder.SourceOrder"/> or
    /// <see cref="StateScope.PerCall"/>). A row pump feeds bands in order but may interleave workers, and a step
    /// whose result depends on the call sequence cannot promise the same pixels. No operator needs this today —
    /// every order-dependent step is Global — but the declaration is what decides, not the category alone.</summary>
    StatefulStep,
}

/// <summary>What <see cref="ProcessingPlan.TryExecuteTo"/> did.</summary>
/// <param name="Refusal">Why the plan did not run; <see cref="StreamRefusal.None"/> when it did.</param>
/// <param name="RowsWritten">Rows handed to the sink; 0 when refused.</param>
/// <param name="BandCount">Bands the source was consumed in; 0 when refused.</param>
/// <param name="WorkerCount">Workers the last band's row loop used; 0 when refused.</param>
public readonly record struct StreamResult(StreamRefusal Refusal, int RowsWritten, int BandCount, int WorkerCount)
{
    /// <summary>The planner's decision, when the strict streaming preflight used one.</summary>
    public ExecutionReport? Execution { get; init; }

    /// <summary>True when the plan ran.</summary>
    public bool Succeeded => Refusal == StreamRefusal.None;

    /// <summary>The step that caused the refusal, when one did.</summary>
    public string? RefusingStep { get; init; }
}

/// <summary>
/// Runs a plan over a forward-only row source: the file-level streaming counterpart of
/// <c>ProcessingPlan.Execute</c>. The band loop is <c>FusedExecutor.RunWindow</c> — the same halo recursion
/// the fused in-memory path uses — so a streamed result is byte-identical to a whole-image run, not merely similar.
/// <para>
/// The source's height is known from its header, so the kernels clamp at the real image edge. Memory is four
/// windows (band + halo, twice) plus the operators' declared scratch: it does not grow with the image height.
/// </para>
/// <para>
/// <b>The sections are the stages.</b> A forward-only source cannot materialise an intermediate, so the chain is one
/// <c>PlanRowSource</c> per operator section of the compiled plan — the pull chain the report names, with each stage's
/// window sized by its own <c>Σhalo</c>. That is why these entry points take a <c>CompiledExecutionPlan</c> rather than
/// a step list: the shape the caller was shown is the shape that runs, and one figure
/// (<c>WorkspacePlan.BoundedBytes</c>) covers both. The trade the streaming path makes is that a section boundary
/// cannot reset anything to a frame, so a chain the whole-image path would cut into eleven fused plus nine stepwise
/// steps still streams as ten linked windows. <see cref="Classify"/> says whether a plan <i>can</i> stream, never
/// whether streaming is the cheaper shape.
/// </para>
/// <para>
/// A plan that cannot stream is refused rather than approximated: see <see cref="StreamRefusal"/>. Empty plans are
/// a straight copy of the rows.
/// </para>
/// </summary>
internal static class StreamingExecutor
{
    /// <summary>
    /// Whether the plan can run over a forward-only source: no <see cref="OpCategory.Global"/> or
    /// <see cref="OpCategory.Composite"/> step (both need more than one pass or more than one input) and no step
    /// whose kernel reads the whole image. <c>Geometric</c> steps cannot be in a plan at all.
    /// </summary>
    internal static StreamRefusal Classify(IReadOnlyList<PlanStep> steps, out string? refusingStep)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var refusal = step.Category switch
            {
                OpCategory.Global => StreamRefusal.GlobalStep,
                OpCategory.Composite => StreamRefusal.CompositeStep,
                _ => step.NeedsWholeSource ? StreamRefusal.StepNeedsWholeSource : StreamRefusal.None,
            };

            if (refusal == StreamRefusal.None)
            {
                var requirements = step.Definition.Contract.Requirements;
                if (requirements.Partition != Partitioning.RowBands ||
                    requirements.State is not StateScope.None || requirements.Order is not ExecutionOrder.Any ||
                    requirements.Threading == ThreadSafety.SingleThread ||
                    requirements.Determinism != Determinism.CoordinateDeterministic)
                    refusal = StreamRefusal.StatefulStep;
            }

            if (refusal != StreamRefusal.None)
            {
                refusingStep = step.Name;
                return refusal;
            }
        }

        refusingStep = null;
        return StreamRefusal.None;
    }

    /// <summary>
    /// The compiled plan's sections, then a resize, as one chain: the stages produce same-size rows, the resize pulls
    /// them and the sink receives the resized ones. No intermediate image, whichever stage the caller stops at.
    /// </summary>
    internal static StreamResult ExecuteWithResize(CompiledExecutionPlan plan, IRowSource source, IRowSink sink,
        int width, int height, Resampler resampler, ExecuteOptions? options, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        if (plan.Backend != ExecutionBackend.Stream)
            throw new InvalidOperationException(
                $"The plan did not choose the stream backend ({plan.Backend}); there is no chain to build.");
        options ??= new ExecuteOptions();

        options = EnsureWorkspace(options);
        var resizeWorkspace = EstimateResizeWorkspace(source, width, height, resampler, options);
        var copyBytes = checked((long)width * Math.Min(Pipeline.TransferRows, height)
            * source.Format.BytesPerPixel());
        WorkingSetBudget.Ensure(checked(WithTransferBuffer(plan.Workspace, copyBytes)
                + resizeWorkspace.TotalBytes), plan.Workspace.UndeclaredSteps, plan.Steps.Count, options,
            "The streaming plan, resize and transfer pipeline");

        var chain = CompiledPlanExecutor.CreateStreamChain(plan, source, options, pool);
        try
        {
            // LastWorkerCount is thread-static and survives the call, so a run that reaches no row loop (an empty
            // plan is one straight copy; a Nearest resize runs its point loop on the caller) would report whatever
            // the previous run left there. Reset before the work from which the number is read.
            RowParallel.LastWorkerCount = 1;
            using var resized = new ResizeRowSource(chain, width, height, resampler, options);
            var written = Pipeline.Copy(resized, sink, options.CancellationToken, options);
            // The last row loop is the resize's vertical pass, not a plan band: report the workers it actually used
            // rather than a constant, which is what this field means on the chain-less entry point.
            return new StreamResult(StreamRefusal.None, written, Bands(chain),
                Math.Max(1, RowParallel.LastWorkerCount));
        }
        finally
        {
            (chain as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Runs a compiled plan over a forward-only source. The stages it builds are the plan's sections, so the shape
    /// the caller was shown is the shape that runs: segment boundaries, per-stage windows and the working-set figure
    /// all come from the same <see cref="CompiledExecutionPlan"/>.
    /// </summary>
    internal static StreamResult Execute(CompiledExecutionPlan plan, IRowSource source, IRowSink sink,
        ExecuteOptions? options, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        if (plan.Backend != ExecutionBackend.Stream)
            throw new InvalidOperationException(
                $"The plan did not choose the stream backend ({plan.Backend}); there is no chain to build.");
        options ??= new ExecuteOptions();

        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException(
                $"A row source must have a positive size; got {source.Width}x{source.Height}.", nameof(source));

        options = EnsureWorkspace(options);
        var copyBytes = checked((long)source.Width * Math.Min(Pipeline.TransferRows, source.Height)
            * source.Format.BytesPerPixel());
        WorkingSetBudget.Ensure(WithTransferBuffer(plan.Workspace, copyBytes),
            plan.Workspace.UndeclaredSteps, plan.Steps.Count, options, "The streaming plan and transfer pipeline");

        // The chain is a pull source now; this driver is the only thing that writes to the sink. Keeping one
        // direction for every stage is what lets a resize sit between two plans (see PlanRowSource).
        var chain = CompiledPlanExecutor.CreateStreamChain(plan, source, options, pool);
        try
        {
            RowParallel.LastWorkerCount = 1;
            var written = Pipeline.Copy(chain, sink, options.CancellationToken, options);
            return new StreamResult(StreamRefusal.None, written, Bands(chain),
                Math.Max(1, RowParallel.LastWorkerCount));
        }
        finally
        {
            (chain as IDisposable)?.Dispose();
        }
    }

    private static ExecuteOptions EnsureWorkspace(ExecuteOptions options) =>
        options.Workspace is not null
            ? options
            : options with { Workspace = new WorkspaceManager(options.MaxWorkingBytes) };

    /// <summary>
    /// The plan's estimate carries one transfer buffer at the plan's own output width; the driver rents one at the
    /// sink's. Replace that term instead of adding a second copy of it — a post-resize width may be larger, so the
    /// larger of the two is the one that keeps the figure an upper bound.
    /// </summary>
    private static long WithTransferBuffer(WorkspacePlan workspace, long copyBytes)
    {
        var planned = 0L;
        foreach (var term in workspace.Terms)
        {
            if (term.Kind == WorkspaceTermKind.CopyBuffer)
                planned = checked(planned + term.Bytes);
        }

        return checked(workspace.BoundedBytes - planned + Math.Max(planned, copyBytes));
    }

    /// <summary>Bands produced by the busiest stage of the chain; 1 for an empty plan, which is one straight copy.</summary>
    private static int Bands(IRowSource chain) =>
        chain is CompiledPlanExecutor.OwnedRowChain owned ? Math.Max(1, owned.Bands) : 1;

    private static ResizeWorkspace EstimateResizeWorkspace(IRowSource source, int width, int height,
        Resampler resampler, ExecuteOptions options)
    {
        var weightsX = WeightTable.Build(source.Width, width, resampler);
        var weightsY = WeightTable.Build(source.Height, height, resampler);
        var tapX = 0;
        var tapY = 0;
        var nearest = resampler == Resampler.Nearest && source.Format == WorkingFormat.Rgba8
                      && Resample.TryNearestTap(weightsX, out tapX)
                      && Resample.TryNearestTap(weightsY, out tapY);
        return ResizeWorkspace.Estimate(source.Width, source.Height, width, height, weightsY.Taps,
            nearest, source.Format, options);
    }
}
