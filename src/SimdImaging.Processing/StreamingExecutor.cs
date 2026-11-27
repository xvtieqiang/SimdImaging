using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
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
    /// The plan's steps, then a resize, as one chain: the pump produces same-size rows, the resize pulls them and the
    /// sink receives the resized ones. No intermediate image, whichever stage the caller stops at.
    /// </summary>
    internal static StreamResult ExecuteWithResize(ProcessingPlan plan, IRowSource source, IRowSink sink, int width,
        int height, Resampler resampler, ExecuteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        options ??= new ExecuteOptions();

        var refusal = Classify(plan.Steps, out var refusingStep);
        if (refusal != StreamRefusal.None)
            return new StreamResult(refusal, 0, 0, 0) { RefusingStep = refusingStep };

        options = EnsureWorkspace(options);
        var planBytes = EstimatePlanRowSource(plan, source, options, out var undeclared);
        var resizeWorkspace = EstimateResizeWorkspace(source, width, height, resampler, options);
        var copyBytes = checked((long)width * Math.Min(Pipeline.TransferRows, height)
            * source.Format.BytesPerPixel());
        WorkingSetBudget.Ensure(checked(planBytes + resizeWorkspace.TotalBytes + copyBytes), undeclared,
            plan.Steps.Count, options, "The streaming plan, resize and transfer pipeline");

        using var rows = new PlanRowSource(plan, source, options);
        using var resized = new ResizeRowSource(rows, width, height, resampler, options);
        var written = Pipeline.Copy(resized, sink, options.CancellationToken, options);

        return new StreamResult(StreamRefusal.None, written, Math.Max(1, rows.Bands), 1);
    }

    public static StreamResult Execute(ProcessingPlan plan, IRowSource source, IRowSink sink,
        ExecuteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        options ??= new ExecuteOptions();

        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException(
                $"A row source must have a positive size; got {source.Width}x{source.Height}.", nameof(source));

        var refusal = Classify(plan.Steps, out var refusingStep);
        if (refusal != StreamRefusal.None)
            return new StreamResult(refusal, 0, 0, 0) { RefusingStep = refusingStep };

        options = EnsureWorkspace(options);
        var planBytes = EstimatePlanRowSource(plan, source, options, out var undeclared);
        var copyBytes = checked((long)source.Width * Math.Min(Pipeline.TransferRows, source.Height)
            * source.Format.BytesPerPixel());
        WorkingSetBudget.Ensure(checked(planBytes + copyBytes), undeclared, plan.Steps.Count, options,
            "The streaming plan and transfer pipeline");

        // The pump is a pull source now; this driver is the only thing that writes to the sink. Keeping one
        // direction for every stage is what lets a resize sit between two plans (see PlanRowSource).
        using var rows = new PlanRowSource(plan, source, options);
        var written = Pipeline.Copy(rows, sink, options.CancellationToken, options);

        return new StreamResult(StreamRefusal.None, written, Math.Max(1, rows.Bands),
            Math.Max(1, RowParallel.LastWorkerCount));
    }

    private static ExecuteOptions EnsureWorkspace(ExecuteOptions options) =>
        options.Workspace is not null
            ? options
            : options with { Workspace = new WorkspaceManager(options.MaxWorkingBytes) };

    private static long EstimatePlanRowSource(ProcessingPlan plan, IRowSource source, ExecuteOptions options,
        out int undeclared)
    {
        if (plan.Steps.Count == 0)
        {
            undeclared = 0;
            return 0;
        }

        var halo = 0;
        foreach (var step in plan.Steps)
            halo = checked(halo + step.HaloY);

        var bandRows = SequentialBands.For(plan, source.Width, source.Height, source.Format,
            options.MaxDegreeOfParallelism);
        var windowRows = (int)Math.Min(source.Height, (long)bandRows + 2L * halo);
        var windows = plan.Steps.Count > 1 ? 4 : 2;
        return WorkingSetBudget.Measure(plan.Steps, options, source.Width, source.Height, source.Width,
            windowRows, source.Format, windows, out undeclared);
    }

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
