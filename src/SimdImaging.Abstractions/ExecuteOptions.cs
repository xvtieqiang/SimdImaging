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
    /// Allow same-size steps to run fused in band windows (fewer full-size intermediates).
    /// <c>false</c> forces the historical step-by-step ping-pong; the reason is recorded in the
    /// <c>PlanReport</c> when diagnostics are on.
    /// </summary>
    public bool AllowFusion { get; init; } = true;

    /// <summary>
    /// Collect a <c>PlanReport</c>. <c>false</c> (default) allocates nothing, times nothing and
    /// changes no hot-path byte — the report is only built when this is on.
    /// </summary>
    public bool Diagnostics { get; init; }

    /// <summary>Cooperative cancellation, checked between bands. 0 = never cancelled.</summary>
    public CancellationToken CancellationToken { get; init; }
}
