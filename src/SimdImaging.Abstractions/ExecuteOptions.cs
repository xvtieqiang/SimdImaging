namespace SimdImaging.Abstractions;

/// <summary>
/// Execution knobs for <c>Image.ApplyWithOptions(plan, options, pool, observe)</c> and
/// <c>ProcessingPlan.ExecuteWithOptions</c>. Every field is optional; the defaults reproduce the
/// historical behaviour except <see cref="AllowFusion"/>, which is on by default because a fused run is
/// byte-identical to a step-by-step run (the plan's equivalence tests are what hold that line). See
/// docs/architecture.md §6.2: outside the explicit degree nothing may read
/// <c>Environment.ProcessorCount</c> in a different way than the frozen entry points already do.
/// </summary>
public sealed record ExecuteOptions
{
    /// <summary>
    /// What the caller is willing to accept from the execution planner. <see cref="ExecutionPolicy.Auto"/>
    /// allows a whole-image fallback; the strict values refuse instead of materialising.
    /// </summary>
    public ExecutionPolicy Policy { get; init; } = ExecutionPolicy.Auto;

    /// <summary>
    /// Maximum worker threads one image's plan may use. 0 keeps the frozen behaviour
    /// (<c>RowParallel.DegreeOverride</c> or the machine default for legacy entry points).
    /// Passed explicitly into the band loops — never read from the environment a second time.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>
    /// Soft ceiling on the working set of one <c>Execute</c> call (intermediate + output buffers,
    /// estimated before execution). 0 = unlimited. Exceeding it throws rather than degrading
    /// silently; the estimate is documented on the executor and is an estimate, not a measurement.
    /// </summary>
    public long MaxWorkingBytes { get; init; }

    /// <summary>
    /// Accept a <see cref="MaxWorkingBytes"/> ceiling even when some steps declared no scratch, which makes the
    /// estimate a lower bound rather than a ceiling.
    /// </summary>
    /// <remarks>
    /// The default is <c>false</c>, and the executor throws instead of guessing. A budget that silently treats an
    /// undeclared operator as free is worse than no budget: it reports success while the working set runs past the
    /// number the caller asked for. Operators that know their footprint declare it through <c>PlanStep.Scratch</c>
    /// (see <c>ImageOp</c>); set this only when a lower bound is genuinely what you want.
    /// </remarks>
    public bool AllowUnestimatedScratch { get; init; }

    /// <summary>Optional reservation account for engine-managed working memory; null disables it.</summary>
    public WorkspaceManager? Workspace { get; init; }

    /// <summary>
    /// Allow same-size steps to run fused in band windows (fewer full-size intermediates).
    /// <c>false</c> forces the historical step-by-step ping-pong; the reason is recorded in the
    /// <c>PlanReport</c> when diagnostics are on.
    /// </summary>
    public bool AllowFusion { get; init; } = true;

    /// <summary>
    /// Fuse regardless of what the plan would decide for itself.
    /// </summary>
    /// <remarks>
    /// The executor picks per plan: a segment containing a small kernel fuses, and a chain of expensive steps fuses
    /// only when the intermediates it avoids are large (see <c>ProcessingPlan.ShouldFuse</c>, which lists the
    /// measurements). This switch exists because that decision is a heuristic and the fused path still needs to be
    /// reachable on demand — the equivalence tests use it to hold the two paths byte-identical on inputs the
    /// heuristic would otherwise route stepwise.
    /// </remarks>
    public bool ForceFusion { get; init; }

    /// <summary>
    /// Collect a <c>PlanReport</c>. <c>false</c> (default) allocates nothing, times nothing and
    /// changes no hot-path byte — the report is only built when this is on.
    /// </summary>
    public bool Diagnostics { get; init; }

    /// <summary>Cooperative cancellation, checked between bands. 0 = never cancelled.</summary>
    public CancellationToken CancellationToken { get; init; }
}
