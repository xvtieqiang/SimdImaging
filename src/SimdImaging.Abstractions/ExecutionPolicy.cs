namespace SimdImaging.Abstractions;

/// <summary>
/// What the caller is willing to accept from the execution planner. This is a constraint, not a hint:
/// the strict values remove every other candidate instead of ranking them.
/// </summary>
public enum ExecutionPolicy : byte
{
    /// <summary>Pick a backend that satisfies the request, falling back to whole-image when necessary.</summary>
    Auto = 0,

    /// <summary>Only a real tile walk is acceptable: no whole-image pre-scan and no materialised fallback.</summary>
    RequireTiled = 1,

    /// <summary>Only row streaming is acceptable: no full-frame materialisation. A replayable source may be
    /// scanned a fixed number of times with bounded analysis state.</summary>
    RequireStreaming = 2,
}

/// <summary>The backend that actually ran.</summary>
public enum ExecutionMode : byte
{
    /// <summary>The whole chain was held in memory.</summary>
    Materialized = 0,

    /// <summary>Rows were pulled through a stream.</summary>
    Streaming = 1,

    /// <summary>Rectangles were walked one at a time.</summary>
    Tiled = 2,

    /// <summary>Only the requested region was accessed.</summary>
    Region = 3,
}

/// <summary>Why the selected backend was not the preferred one, or why the run was refused.</summary>
public enum ExecutionFallbackReason : byte
{
    None = 0,
    PolicyMismatch = 1,
    Capability = 2,
    Budget = 3,
    BudgetUnverifiable = 4,
    InvalidPlan = 5,
    InvalidRequest = 6,
}

/// <summary>How the result reached its destination.</summary>
public enum OutputStrategy : byte
{
    /// <summary>A whole result buffer was returned to the caller.</summary>
    Buffered = 0,

    /// <summary>The result was written in row bands.</summary>
    Banded = 1,

    /// <summary>The result was written as rectangles to a region sink.</summary>
    Region = 2,

    /// <summary>The result was written straight to the sink.</summary>
    Direct = 3,
}

/// <summary>
/// One execution's decision and measured outcome. The planner fills the prediction; the executor fills the
/// peak. Existing reports expose this as an additional detail rather than duplicating its fields.
/// </summary>
public sealed record ExecutionReport
{
    public ExecutionMode SelectedMode { get; init; }
    public ExecutionPolicy RequestedPolicy { get; init; }
    public ExecutionFallbackReason FallbackReason { get; init; }
    public string? RefusingStep { get; init; }
    public string? Message { get; init; }
    public long PredictedWorkingBytes { get; init; }
    public long PeakWorkingBytes { get; init; }
    public OutputStrategy OutputStrategy { get; init; }
    public IReadOnlyList<string> Steps { get; init; } = [];
    public int Passes { get; init; } = 1;
    public int SourceReads { get; init; } = 1;
    public bool ReplayRequired { get; init; }
    public bool BudgetBestEffort { get; init; }
}
