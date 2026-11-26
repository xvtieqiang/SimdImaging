using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Contracts;

/// <summary>How a workspace figure should be multiplied by the executor.</summary>
internal enum WorkspaceScope : byte
{
    /// <summary>One copy per worker.</summary>
    PerWorker = 0,

    /// <summary>One copy for the whole image, on the calling thread.</summary>
    WholeImage = 1,

    /// <summary>One copy per operator call. A banded executor calls once per band (so it behaves like
    /// <see cref="PerWorker"/>); a whole-image executor calls once. No current operator declares it, but the
    /// meaning is defined rather than left to the estimator to guess.</summary>
    PerCall = 2,
}

/// <summary>
/// What one call allocates, or an explicit admission that nobody knows.
/// <para>
/// <c>null</c> is not zero. A budget that treats an undeclared operator as free reports success while the process
/// runs past it, so <c>ExecuteOptions.MaxWorkingBytes</c> refuses a plan containing one unless the caller accepts a
/// lower bound. <see cref="None"/> is the positive claim "this step allocates nothing worth reporting".
/// </para>
/// </summary>
internal sealed class WorkspaceModel
{
    private WorkspaceModel(Func<int, int, int, WorkingFormat, long>? estimate, WorkspaceScope scope,
        Func<int, int, int, WorkingFormat, long>? perWorkerEstimate,
        Func<int, int, int, WorkingFormat, long>? wholeImageEstimate,
        Func<int, int, int, WorkingFormat, long>? perCallEstimate)
    {
        Estimate = estimate;
        Scope = scope;
        PerWorkerEstimate = perWorkerEstimate;
        WholeImageEstimate = wholeImageEstimate;
        PerCallEstimate = perCallEstimate;
    }

    /// <summary>The operator has not declared its footprint; the estimator reports a lower bound.</summary>
    internal static WorkspaceModel Undeclared { get; } =
        new(null, WorkspaceScope.PerWorker, null, null, null);

    /// <summary>The step allocates nothing worth reporting.</summary>
    internal static WorkspaceModel None { get; } =
        Of((_, _, _, _) => 0L, WorkspaceScope.PerWorker);

    /// <summary>A declaration with one allocation lifetime. Width × rows is the view passed to the operator.</summary>
    internal static WorkspaceModel Of(Func<int, int, int, WorkingFormat, long> estimate,
        WorkspaceScope scope = WorkspaceScope.PerWorker)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return scope switch
        {
            WorkspaceScope.PerWorker =>
                new WorkspaceModel(estimate, scope, estimate, null, null),
            WorkspaceScope.WholeImage =>
                new WorkspaceModel(estimate, scope, null, estimate, null),
            WorkspaceScope.PerCall =>
                new WorkspaceModel(estimate, scope, null, null, estimate),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
    }

    /// <summary>
    /// A mixed lifetime: the operator has some buffers shared by its one whole-image invocation and other buffers
    /// privately owned by each active worker. This avoids multiplying full-frame intermediates by the worker count.
    /// </summary>
    internal static WorkspaceModel OfMixed(
        Func<int, int, int, WorkingFormat, long>? perWorker,
        Func<int, int, int, WorkingFormat, long>? wholeImage,
        Func<int, int, int, WorkingFormat, long>? perCall = null)
    {
        if (perWorker is null && wholeImage is null && perCall is null)
            throw new ArgumentException("A mixed workspace model must declare at least one allocation.");

        long Combined(int width, int height, int rows, WorkingFormat format) =>
            checked((perWorker?.Invoke(width, height, rows, format) ?? 0L)
                + (wholeImage?.Invoke(width, height, height, format) ?? 0L)
                + (perCall?.Invoke(width, height, rows, format) ?? 0L));

        return new WorkspaceModel(Combined, WorkspaceScope.PerWorker, perWorker, wholeImage, perCall);
    }

    /// <summary>
    /// Combined unmultiplied declaration retained for diagnostics and legacy internal callers. Budget decisions
    /// must use <see cref="PerWorkerEstimate"/> and <see cref="WholeImageEstimate"/> separately.
    /// </summary>
    internal Func<int, int, int, WorkingFormat, long>? Estimate { get; }

    internal WorkspaceScope Scope { get; }

    internal Func<int, int, int, WorkingFormat, long>? PerWorkerEstimate { get; }

    internal Func<int, int, int, WorkingFormat, long>? WholeImageEstimate { get; }

    internal Func<int, int, int, WorkingFormat, long>? PerCallEstimate { get; }

    internal bool IsDeclared => Estimate is not null;

    internal bool HasPerWorkerEstimate => PerWorkerEstimate is not null;

    internal bool HasWholeImageEstimate => WholeImageEstimate is not null;

    /// <summary>Scratch owned once by a worker, including per-call scratch on a banded invocation.</summary>
    internal long EstimatePerWorker(int width, int height, int rows, WorkingFormat format) =>
        checked((PerWorkerEstimate?.Invoke(width, height, rows, format) ?? 0L)
            + (PerCallEstimate?.Invoke(width, height, rows, format) ?? 0L));

    /// <summary>Scratch held once for the whole-image invocation, including per-call scratch on that invocation.</summary>
    internal long EstimateWholeImage(int width, int height, WorkingFormat format) =>
        checked((WholeImageEstimate?.Invoke(width, height, height, format) ?? 0L)
            + (PerCallEstimate?.Invoke(width, height, height, format) ?? 0L));

}/// <summary>The kernels an operator offers. The contract describes; these run.</summary>
internal sealed class KernelImplementations
{
    internal KernelImplementations(OpKernel planKernel,
        Func<PixelView, WorkingBuffer>? wholeImage = null,
        IntoKernel? into = null,
        OpKernel2? secondKernel = null,
        Func<PixelView, PixelView, WorkingBuffer>? wholeImageSecond = null)
    {
        PlanKernel = planKernel ?? throw new ArgumentNullException(nameof(planKernel));
        WholeImage = wholeImage;
        Into = into;
        SecondKernel = secondKernel;
        WholeImageSecond = wholeImageSecond;
    }

    /// <summary>The band kernel every plan step uses.</summary>
    internal OpKernel PlanKernel { get; }

    /// <summary>The whole-image form, used by the single-step fast path.</summary>
    internal Func<PixelView, WorkingBuffer>? WholeImage { get; }

    /// <summary>"Write these rows into that destination", when the operator has that form.</summary>
    internal IntoKernel? Into { get; }

    /// <summary>The second-input form: the overlay comes from the plan's recorded view.</summary>
    internal OpKernel2? SecondKernel { get; }

    /// <summary>The whole-image second-input form: a format-changing composite whose output leaves the plan's
    /// working format, so it can only run as a compiled format section.</summary>
    internal Func<PixelView, PixelView, WorkingBuffer>? WholeImageSecond { get; }
}
