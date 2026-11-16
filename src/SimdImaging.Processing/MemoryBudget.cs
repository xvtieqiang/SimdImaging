using SimdImaging.Abstractions;
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
        return Estimate(source.Width, source.Height, source.Format, plan.Steps, plan.BandRows,
            allowFusion: true, degree);
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
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bandRows);
        ArgumentOutOfRangeException.ThrowIfNegative(degree);

        var bytesPerPixel = format.BytesPerPixel();
        var full = checked((long)width * height * bytesPerPixel);
        var workers = Workers(height, bandRows, degree);

        // What the executor rents: one destination buffer per step, or — when fused — the destination plus
        // two band windows per worker. EstimateSegmentBytes already counts the destination in its answer.
        var rented = full;
        if (allowFusion && steps.Count > 1)
        {
            var segments = FusionPlanner.Segment(steps);
            foreach (var (first, count) in segments)
            {
                if (count <= 1)
                    continue;

                var segment = FusedExecutor.EstimateSegmentBytes(width, height, format, steps, first, count,
                    bandRows, degree);
                rented = Math.Max(rented, segment);
            }
        }

        // Scratch is counted by execution shape, not by a blanket multiplier:
        //   banded       one copy per worker, over band + halo rows
        //   whole-image  one copy, on the calling thread (ProcessingPlan runs Global steps exactly once)
        // A single non-Global step is banded too - the executor does not care how long the plan is, it only
        // looks at the category - so this no longer keys off steps.Count.
        long perWorker = 0;
        long wholeImage = 0;
        var undeclared = 0;
        foreach (var step in steps)
        {
            if (step.Scratch is not { } scratch)
            {
                undeclared++;
                continue;
            }

            var isWholeImage = step.Category is OpCategory.Global or OpCategory.Composite;
            if (isWholeImage)
            {
                wholeImage = Math.Max(wholeImage, scratch(width, height, height, format));
                continue;
            }

            var rows = steps.Count > 1 && !(allowFusion && FusionPlanner.IsBandable(step))
                ? height
                : Math.Min(height, bandRows + (2 * step.HaloY));
            perWorker = Math.Max(perWorker, scratch(width, height, rows, format));
        }

        // The caller holds the source; the executor holds `rented`. Everything beyond the destination is
        // scratch, and the operator terms sit on top of both.
        var planScratch = Math.Max(0, rented - full);
        var peak = checked(full + rented + (perWorker * workers) + wholeImage);
        return new MemoryEstimate(full, full, planScratch, perWorker, workers, peak)
        {
            WholeImageScratchBytes = wholeImage,
            UndeclaredSteps = undeclared,
        };
    }

    /// <summary>Mirrors <see cref="RowParallel"/>'s rule: one worker per band, capped by the degree.</summary>
    private static int Workers(int height, int bandRows, int degree)
    {
        var bands = Math.Max(1, (height + bandRows - 1) / bandRows);
        var cap = degree > 0 ? degree : Environment.ProcessorCount;
        return Math.Max(1, Math.Min(cap, bands));
    }
}
