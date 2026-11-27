using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>Why a plan (or a segment of it) ran step-by-step instead of fused.</summary>
public enum FusionFallbackReason : byte
{
    /// <summary>The chain ran fused.</summary>
    None = 0,

    /// <summary><c>ExecuteOptions.AllowFusion == false</c>.</summary>
    AllowFusionDisabled = 1,

    /// <summary>A <c>PlanObserver</c> was present: it must see every step's full output, which a fused
    /// band window does not materialise.</summary>
    ObserverPresent = 2,

    /// <summary>A category boundary (Global / Geometric / Composite step) split the chain; the steps
    /// on each side still fuse among themselves.</summary>
    CategoryBoundary = 3,

    /// <summary>A neighbouring pair cannot share a fused window (e.g. a format change).</summary>
    UnsupportedPair = 4,

    /// <summary>The plan had zero steps and returned a plain copy.</summary>
    EmptyPlan = 5,
}

/// <summary>One contiguous run of steps in the executed chain.</summary>
/// <param name="FirstStep">Zero-based index of the first step in the segment.</param>
/// <param name="StepCount">How many steps the segment spans.</param>
/// <param name="Fused">Whether the segment ran in fused band windows.</param>
/// <param name="Name">Concatenated step names, for logs.</param>
public readonly record struct PlanSegment(int FirstStep, int StepCount, bool Fused, string Name);

/// <summary>
/// Execution diagnostics for one <c>Execute</c> call. Built only when
/// <c>ExecuteOptions.Diagnostics</c> is on — with diagnostics off, no report object, no timing and
/// no collection exist, and the hot path is byte-for-byte unchanged. Fields with no real consumer
/// were deliberately cut during review rather than speculatively kept.
/// </summary>
public sealed class PlanReport
{
    /// <summary>How many contiguous segments the chain was cut into (1 = fully fused or fully step-by-step).</summary>
    public int SegmentCount { get; internal init; }

    /// <summary>The segments in execution order.</summary>
    public IReadOnlyList<PlanSegment> Segments { get; internal init; } = [];

    /// <summary>Why the chain (or its longest segment) fell back to step-by-step execution.</summary>
    public FusionFallbackReason FallbackReason { get; internal init; }

    /// <summary>Worker count the band loops actually used (from <c>RowParallel.LastWorkerCount</c> diagnostics).</summary>
    public int WorkerCount { get; internal init; }

    /// <summary>Total row bands executed across all steps and segments.</summary>
    public int BandCount { get; internal init; }

    /// <summary>How many buffers the pool rented during execution (intermediates + output).</summary>
    public int PoolRentCount { get; internal init; }

    /// <summary>How many rented buffers were returned during execution.</summary>
    public int PoolReturnCount { get; internal init; }

    /// <summary>Wall-clock ticks for the whole call. Only meaningful with Diagnostics on.</summary>
    public long ElapsedTicks { get; internal init; }

    /// <summary>The execution planner's decision, when the run used one. Fusion fallback stays in
    /// <see cref="FallbackReason"/>; backend fallback is in <see cref="ExecutionReport.FallbackReason"/>.</summary>
    public ExecutionReport? Execution { get; internal init; }
}
