using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Compilation;

/// <summary>Why a candidate or the whole plan was refused. The planner keeps these structured; the public
/// report only needs the primary one plus a compact candidate summary.</summary>
internal enum ExecutionRefusalReason : byte
{
    None = 0,
    InvalidRequest = 1,
    Capability = 2,
    Budget = 3,
    BudgetUnverifiable = 4,
    InvalidPlan = 5,
}

/// <summary>One backend the planner evaluated. <see cref="RequiredBytes"/> is the candidate's own bounded
/// workspace estimate; <see cref="IsLowerBound"/> means at least one step declared no scratch.</summary>
internal sealed record CandidateEvaluation(
    ExecutionBackend Backend,
    bool Feasible,
    ExecutionRefusalReason Reason,
    string? RefusingStep,
    string? Message,
    long RequiredBytes,
    bool IsLowerBound)
{
    internal static CandidateEvaluation Selected(ExecutionBackend backend, long bytes, bool lowerBound) =>
        new(backend, true, ExecutionRefusalReason.None, null, null, bytes, lowerBound);

    internal static CandidateEvaluation Failed(ExecutionBackend backend, ExecutionRefusalReason reason,
        string? step, string message, long bytes = 0, bool lowerBound = false) =>
        new(backend, false, reason, step, message, bytes, lowerBound);
}

/// <summary>
/// Everything the planner may consider besides the policy itself: the chain, what the source and sink can do,
/// the request's preference, and the real geometry the plan will run on. Internal for now; the public entries
/// will construct it once the decision table is stable.
/// </summary>
internal sealed record ExecutionRequest
{
    internal required PipelineRecipe Recipe { get; init; }

    internal required int Width { get; init; }
    internal required int Height { get; init; }
    internal required WorkingFormat Format { get; init; }

    internal SourceCapabilities Source { get; init; } = SourceCapabilities.Seekable |
                                                          SourceCapabilities.Replayable |
                                                          SourceCapabilities.StableContent;

    internal SinkCapabilities Sink { get; init; }

    /// <summary>The rectangle the caller asked for; a data-access constraint, never a ranking hint.</summary>
    internal PixelRect? Region { get; init; }

    /// <summary>True when the entry point is a tile walk; it makes Tile the preferred candidate under Auto.</summary>
    internal bool TileRequested { get; init; }
    internal int TileWidth { get; init; }
    internal int TileHeight { get; init; }

    internal ExecutionPolicy Policy { get; init; } = ExecutionPolicy.Auto;

    /// <summary>The entry point's preference under <see cref="ExecutionPolicy.Auto"/>. Region is not a value:
    /// it is expressed by <see cref="Region"/> and is always a fixed constraint.</summary>
    internal ExecutionBackend? Preferred { get; init; }

    internal ExecuteOptions Execute { get; init; } = new();
    /// <summary>Rows per band; 0 lets the executor size it, a positive value is the caller's geometry.</summary>
    internal int BandRows { get; init; }

    /// <summary>Bytes a backend needs beyond its compiled workspace (a full destination for a buffer-returning
    /// tile walk, an encoder band for a tile-to-encoder run). Zero for a sink that owns its storage.</summary>
    internal Func<ExecutionBackend, long>? AdditionalBytesFor { get; init; }
    internal bool ObserveSteps { get; init; }
    internal double MaxRegionReadAmplification { get; init; }
}

/// <summary>The planner's output. Exactly one of <see cref="Plan"/> and <see cref="Refusal"/> is set.</summary>
internal sealed record ExecutionDecision(
    ExecutionPolicy Policy,
    ExecutionBackend? Preferred,
    CompiledExecutionPlan? Plan,
    CandidateEvaluation? Refusal,
    ExecutionRefusalReason Reason,
    string? RefusingStep,
    string? Message,
    bool BudgetBestEffort,
    IReadOnlyList<CandidateEvaluation> Candidates)
{
    /// <summary>Wall-clock ticks the planner spent on this decision; 0 when it was not measured.</summary>
    internal long PlanningTicks { get; init; }

    internal bool Succeeded => Plan is not null;

    internal ExecutionBackend? SelectedBackend => Plan?.Backend;

    /// <summary>The first candidate failed but a later one was selected. The reason is the preferred
    /// candidate's blocker, not merely "the last one ran out of budget".</summary>
    internal CandidateEvaluation? FallbackFrom => Succeeded && Candidates.Count > 0 && !Candidates[0].Feasible
        ? Candidates[0]
        : null;

    /// <summary>When nothing succeeded, the last candidate's blocker is the one that stopped the final fallback.
    /// It decides the exception type and the required bytes, while <see cref="Refusal"/> keeps the preferred
    /// candidate's reason for the report.</summary>
    internal CandidateEvaluation? TerminalRefusal => !Succeeded && Candidates.Count > 0 ? Candidates[^1] : null;

    /// <summary>Projects the internal decision onto the public report. The executor fills PeakWorkingBytes
    /// afterwards with <c>with { PeakWorkingBytes = ... }</c>.</summary>
    internal ExecutionReport ToReport(OutputStrategy output, int passes = 1, int sourceReads = 1,
        bool replayRequired = false)
    {
        // A refusal has no selected backend; report the mode the request was constrained to.
        var mode = SelectedBackend switch
        {
            ExecutionBackend.Stream => ExecutionMode.Streaming,
            ExecutionBackend.Tile => ExecutionMode.Tiled,
            ExecutionBackend.Region => ExecutionMode.Region,
            _ when !Succeeded => Preferred switch
            {
                ExecutionBackend.Stream => ExecutionMode.Streaming,
                ExecutionBackend.Tile => ExecutionMode.Tiled,
                ExecutionBackend.Region => ExecutionMode.Region,
                _ => ExecutionMode.Materialized,
            },
            _ => ExecutionMode.Materialized,
        };
        var cause = Succeeded ? FallbackFrom : (TerminalRefusal ?? Refusal);
        var predicted = Succeeded
            ? Candidates.FirstOrDefault(candidate => candidate.Feasible)?.RequiredBytes ?? 0
            : (TerminalRefusal ?? Refusal)?.RequiredBytes ?? 0;

        return new ExecutionReport
        {
            SelectedMode = mode,
            RequestedPolicy = Policy,
            FallbackReason = cause is null ? ExecutionFallbackReason.None : Map(cause.Reason),
            RefusingStep = cause?.RefusingStep,
            Message = cause?.Message,
            PredictedWorkingBytes = predicted,
            OutputStrategy = output,
            Steps = Plan?.Sections.Select(section => $"{section.Kind} x{section.StepCount}").ToArray() ?? [],
            Passes = passes,
            SourceReads = sourceReads,
            ReplayRequired = replayRequired,
            BudgetBestEffort = BudgetBestEffort,
            PlanningTicks = PlanningTicks,
        };
    }

    private static ExecutionFallbackReason Map(ExecutionRefusalReason reason) => reason switch
    {
        ExecutionRefusalReason.InvalidRequest => ExecutionFallbackReason.InvalidRequest,
        ExecutionRefusalReason.Capability => ExecutionFallbackReason.Capability,
        ExecutionRefusalReason.Budget => ExecutionFallbackReason.Budget,
        ExecutionRefusalReason.BudgetUnverifiable => ExecutionFallbackReason.BudgetUnverifiable,
        ExecutionRefusalReason.InvalidPlan => ExecutionFallbackReason.InvalidPlan,
        _ => ExecutionFallbackReason.None,
    };
}
