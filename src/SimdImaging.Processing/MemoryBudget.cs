using SimdImaging.Abstractions;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// The resident memory this library keeps between calls, and the knobs for it. Two caches hold it: the
/// per-thread float scratch (idle buffers, budgeted in bytes) and the reconstruction-weight tables (one per
/// distinct source/destination length pair).
/// <para>
/// The scratch figures describe <b>the calling thread</b>, because the storage is thread-local — that is
/// the property that makes the pool lock-free and the output byte-identical regardless of worker count, and
/// it is not negotiable. A process-wide total would need a registry updated on the hot path. Trim and
/// SetLimits are likewise per-thread for the scratch; the weight cache is shared and process-wide.
/// </para>
/// </summary>
public static class MemoryBudget
{
    /// <summary>What is held right now, plus the counters since the process started.</summary>
    public static MemoryBudgetSnapshot Snapshot() =>
        new(FloatScratch.Statistics, WeightTable.Statistics);

    /// <summary>
    /// Releases every idle buffer on the calling thread, every cached weight table, and the shared pixel
    /// pool's retained large blocks. Pixels in use are untouched: a buffer that is rented is not retained.
    /// </summary>
    public static void Trim()
    {
        FloatScratch.Trim();
        WeightTable.TrimCache();
        ArrayPixelPool.Shared.Trim();
    }

    /// <summary>
    /// Changes a budget. Null leaves that budget alone; a value applies from the next rent or build.
    /// Lowering the scratch budget trims immediately so the new figure is real rather than aspirational.
    /// </summary>
    /// <param name="scratchRetainedBytes">Idle scratch one thread may keep, in bytes.</param>
    /// <param name="scratchBufferBytes">The largest single scratch buffer a thread keeps; bigger ones are
    /// handed out and then dropped.</param>
    /// <param name="weightCacheBytes">Bytes of reconstruction-weight tables to cache.</param>
    /// <param name="weightCacheEntries">How many distinct size pairs to cache.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is negative.</exception>
    /// <exception cref="ArgumentException">The single-buffer cap exceeds the retained budget.</exception>
    public static void SetLimits(long? scratchRetainedBytes = null, long? scratchBufferBytes = null,
        long? weightCacheBytes = null, int? weightCacheEntries = null)
    {
        if (scratchRetainedBytes is { } retained)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(retained);
            FloatScratch.RetainedBytesLimit = retained;
        }

        if (scratchBufferBytes is { } buffer)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(buffer);
            if (buffer > FloatScratch.RetainedBytesLimit)
                throw new ArgumentException(
                    "A single scratch buffer cannot exceed the retained budget; it would never fit.", nameof(scratchBufferBytes));

            FloatScratch.BufferBytesLimit = buffer;
        }

        if (weightCacheBytes is { } weights)
            ArgumentOutOfRangeException.ThrowIfNegative(weights);

        if (weightCacheEntries is { } entries)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entries);

        if (weightCacheBytes is { } bytes)
            WeightTable.BudgetBytes = bytes;

        if (weightCacheEntries is { } count)
            WeightTable.MaxEntries = count;

        // A lowered budget has to bite now, or the snapshot would report more than the limit allows.
        FloatScratch.Trim();
        WeightTable.TrimCache();
    }

    /// <summary>Restores the defaults and clears the counters (tests only).</summary>
    internal static void ResetForTests()
    {
        FloatScratch.ResetForTests();
        WeightTable.ResetForTests();
    }
}

/// <summary>One reading of <see cref="MemoryBudget.Snapshot"/>.</summary>
/// <param name="Scratch">The calling thread's float-scratch pool.</param>
/// <param name="WeightCache">The shared reconstruction-weight cache.</param>
public sealed record MemoryBudgetSnapshot(ScratchStatistics Scratch, WeightCacheStatistics WeightCache);

/// <summary>
/// Predicts what a plan will hold, before it runs. The structural terms are arithmetic the executor
/// controls; the per-worker term is whatever the steps declared through <c>PlanStep.Scratch</c>, which is
/// why <see cref="MemoryEstimate.HasEstimatedTerms"/> exists.
/// <para>
/// The estimate is deliberately conservative in one direction: it reads the step list as given and does not
/// fold consecutive point-LUT steps, which can only remove buffers. It never under-reports a structural
/// buffer, and every multiplication uses <c>checked</c> arithmetic so an absurd size throws instead of
/// wrapping into a small number.
/// </para>
/// </summary>
public static class MemoryEstimator
{
    /// <summary>Estimates a plan against a concrete source.</summary>
    /// <param name="source">The pixels the plan would run on.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="degree">Worker count the executor would use; 0 means "the machine default".</param>
    public static MemoryEstimate Estimate(PixelView source, ProcessingPlan plan, int degree = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(degree);
        var fusionAllowed = plan.Steps.All(step =>
            !step.Definition.Contract.Requirements.RequiresWholeImageInvocation);
        var fuse = fusionAllowed && FusionDecision.ShouldFuse(
            plan.Steps, source.Width, source.Height, source.Format.BytesPerPixel());
        var bandRows = plan.BandRowsFor(source.Width, source.Height);
        var stepwiseRows = plan.StepwiseRowsFor(source.Height, bandRows, degree);
        return EstimateCore(source.Width, source.Height, source.Format, plan.Steps, bandRows,
            stepwiseRows, allowFusion: fuse, degree);
    }

    /// <summary>Estimates a plan from geometry alone, without an image in hand.</summary>
    /// <param name="width">Source width.</param>
    /// <param name="height">Source height.</param>
    /// <param name="format">Working format.</param>
    /// <param name="steps">The steps that would run.</param>
    /// <param name="bandRows">Rows per band.</param>
    /// <param name="allowFusion">Whether the executor would fuse consecutive bandable steps.</param>
    /// <param name="degree">Worker count the executor would use; 0 means "the machine default".</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive, or the degree is negative.</exception>
    /// <exception cref="OverflowException">The image is large enough that the estimate overflows 64 bits.</exception>
    public static MemoryEstimate Estimate(int width, int height, WorkingFormat format,
        IReadOnlyList<PlanStep> steps, int bandRows = ProcessingPlan.DefaultBandRows,
        bool allowFusion = true, int degree = 0)
    {
        ValidateEstimateArguments(width, height, steps, bandRows, degree);
        var stepwiseRows = ProcessingPlan.ResolveStepwiseRows(height, bandRows, degree,
            useDefaultPolicy: bandRows == ProcessingPlan.DefaultBandRows);
        return EstimateCore(width, height, format, steps, bandRows, stepwiseRows, allowFusion, degree);
    }

    internal static MemoryEstimate EstimateForExecution(int width, int height, WorkingFormat format,
        IReadOnlyList<PlanStep> steps, int bandRows, int stepwiseRows, bool allowFusion, int degree)
    {
        ValidateEstimateArguments(width, height, steps, bandRows, degree);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepwiseRows);
        return EstimateCore(width, height, format, steps, bandRows, stepwiseRows, allowFusion, degree);
    }

    private static void ValidateEstimateArguments(int width, int height, IReadOnlyList<PlanStep> steps,
        int bandRows, int degree)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bandRows);
        ArgumentOutOfRangeException.ThrowIfNegative(degree);
    }

    private static MemoryEstimate EstimateCore(int width, int height, WorkingFormat format,
        IReadOnlyList<PlanStep> steps, int bandRows, int stepwiseRows, bool allowFusion, int degree)
    {
        var bytesPerPixel = format.BytesPerPixel();
        var full = checked((long)width * height * bytesPerPixel);
        var bandWorkers = Workers(height, bandRows, degree);
        var stepwiseWorkers = Workers(height, stepwiseRows, degree);
        var resolvedDegree = RowParallel.ResolveDegree(degree);
        var workers = 1;

        // The previous segment's full-frame result remains alive while the next segment writes its output.
        // Keep the segmentation for both structural buffers and per-step adapter scratch: in a fused run, step k
        // receives the band widened by its own halo and every later step's halo (Point adapters consume the
        // destination rows, so they only include later halos).
        var fusedSegments = allowFusion && steps.Count > 1 ? FusionPlanner.Segment(steps) : null;
        var rented = full;
        if (steps.Count > 1)
        {
            if (fusedSegments is null)
            {
                // Stepwise execution keeps the prior full-frame result alive while the next destination is filled.
                rented = checked(2L * full);
            }
            else
            {
                for (var index = 0; index < fusedSegments.Count; index++)
                {
                    var (first, count) = fusedSegments[index];
                    var segment = count > 1
                        ? FusedExecutor.EstimateSegmentBytes(width, height, format, steps, first, count,
                            bandRows, degree)
                        : full;

                    if (index > 0)
                        segment = checked(segment + full);

                    rented = Math.Max(rented, segment);
                }
            }
        }

        // Scratch is counted by the invocation shape, not by a blanket multiplier:
        //   fused runs     one adapter window per worker, including downstream halo
        //   stepwise runs  one adapter window per worker using the actual stepwise row split
        //   whole-image    one kernel invocation; internal PerWorker scratch still follows its own workers.
        long perWorker = 0;
        long perWorkerPeak = 0;
        long wholeImage = 0;
        var undeclared = 0;
        for (var stepIndex = 0; stepIndex < steps.Count; stepIndex++)
        {
            var step = steps[stepIndex];
            var requirements = step.Definition.Contract.Requirements;
            var wholeImageInvocation =
                step.Category is OpCategory.Global or OpCategory.Composite ||
                requirements.RequiresWholeImageInvocation;
            var fusedInvocation = IsFusedStep(fusedSegments, stepIndex);
            var stepWorkers = wholeImageInvocation
                ? requirements.Threading == ThreadSafety.SingleThread
                    ? 1
                    : Math.Min(resolvedDegree, height)
                : fusedInvocation ? bandWorkers : stepwiseWorkers;

            var workspace = step.Definition.Contract.Workspace;
            if (!workspace.IsDeclared)
            {
                undeclared++;
                continue;
            }
            var rows = wholeImageInvocation
                ? height
                : FusedInvocationRows(steps, stepIndex, fusedSegments, height, bandRows, stepwiseRows, step);

            // The partition contract says how often the kernel is called; WorkspaceModel says which of its
            // allocations are per worker, whole-image, or per call. A Global kernel can still allocate per worker
            // internally, so Category must not silently turn PerWorker into a single copy.
            if (workspace.HasWholeImageEstimate ||
                (wholeImageInvocation && workspace.PerCallEstimate is not null))
            {
                wholeImage = Math.Max(wholeImage,
                    workspace.EstimateWholeImage(width, height, format));
            }

            if (workspace.HasPerWorkerEstimate ||
                (!wholeImageInvocation && workspace.PerCallEstimate is not null))
            {
                workers = Math.Max(workers, stepWorkers);

                // Whole-image operators may run their own RowParallel loops. Their concurrency is independent
                // of ProcessingPlan.BandRows, which only configures the outer row-band executor.
                var stepScratch = workspace.EstimatePerWorker(width, height, rows, format);
                perWorker = Math.Max(perWorker, stepScratch);
                perWorkerPeak = Math.Max(perWorkerPeak, checked(stepScratch * stepWorkers));
            }
        }

        // The caller holds the source; the executor holds `rented`. Everything beyond the destination is
        // scratch, and the operator terms sit on top of both.
        var planScratch = Math.Max(0, rented - full);
        var peak = checked(full + rented + perWorkerPeak + wholeImage);
        return new MemoryEstimate(full, full, planScratch, perWorker, workers, peak)
        {
            PerWorkerPeakBytes = perWorkerPeak,
            WholeImageScratchBytes = wholeImage,
            UndeclaredSteps = undeclared,
        };
    }

    /// <summary>
    /// Rows in the view actually passed to one operator adapter. A fused neighbourhood step receives its current
    /// halo plus all downstream halos; a point adapter runs only over the output rectangle, which excludes its own
    /// halo. Outside a multi-step fused segment, the existing band/window geometry applies.
    /// </summary>
    private static int FusedInvocationRows(IReadOnlyList<PlanStep> steps, int stepIndex,
        IReadOnlyList<(int First, int Count)>? segments, int height, int bandRows, int stepwiseRows, PlanStep step)
    {
        if (segments is not null)
        {
            foreach (var (first, count) in segments)
            {
                if (count <= 1 || stepIndex < first || stepIndex >= first + count)
                    continue;

                var downstreamHalo = 0;
                for (var i = stepIndex + 1; i < first + count; i++)
                    downstreamHalo = checked(downstreamHalo + steps[i].HaloY);

                var context = step.Category == OpCategory.Point
                    ? downstreamHalo
                    : checked(step.HaloY + downstreamHalo);
                return (int)Math.Min(height, (long)bandRows + (2L * context));
            }
        }

        return (int)Math.Min(height, (long)stepwiseRows + (2L * step.HaloY));
    }

    private static bool IsFusedStep(IReadOnlyList<(int First, int Count)>? segments, int stepIndex)
    {
        if (segments is null)
            return false;

        foreach (var (first, count) in segments)
        {
            if (count > 1 && stepIndex >= first && stepIndex < first + count)
                return true;
        }

        return false;
    }

    /// <summary>Mirrors <see cref="RowParallel"/>'s rule: one worker per band, capped by the degree.</summary>
    private static int Workers(int height, int bandRows, int degree)
    {
        var bands = Math.Max(1, (height + bandRows - 1) / bandRows);
        var cap = RowParallel.ResolveDegree(degree);
        return Math.Max(1, Math.Min(cap, bands));
    }
}
