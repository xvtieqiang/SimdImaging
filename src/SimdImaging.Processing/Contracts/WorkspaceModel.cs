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
    private WorkspaceModel(Func<int, int, int, WorkingFormat, long>? estimate, WorkspaceScope scope)
    {
        Estimate = estimate;
        Scope = scope;
    }

    /// <summary>The operator has not declared its footprint; the estimator reports a lower bound.</summary>
    internal static WorkspaceModel Undeclared { get; } = new(null, WorkspaceScope.PerWorker);

    /// <summary>The step allocates nothing worth reporting.</summary>
    internal static WorkspaceModel None { get; } = new((_, _, _, _) => 0L, WorkspaceScope.PerWorker);

    /// <summary>A declaration: bytes for a view of <c>width × rows</c>, and how the executor multiplies it.</summary>
    internal static WorkspaceModel Of(Func<int, int, int, WorkingFormat, long> estimate,
        WorkspaceScope scope = WorkspaceScope.PerWorker) =>
        new(estimate ?? throw new ArgumentNullException(nameof(estimate)), scope);

    /// <summary>The declaration, or null when there is none. The signature matches the executor's scratch hook.</summary>
    internal Func<int, int, int, WorkingFormat, long>? Estimate { get; }

    internal WorkspaceScope Scope { get; }

    internal bool IsDeclared => Estimate is not null;
}

/// <summary>The kernels an operator offers. The contract describes; these run.</summary>
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

    /// <summary>The whole-image form, used by the single-step fast path. This was <c>ImageOp.Direct</c>.</summary>
    internal Func<PixelView, WorkingBuffer>? WholeImage { get; }

    /// <summary>"Write these rows into that destination", when the operator has that form.</summary>
    internal IntoKernel? Into { get; }

    /// <summary>The second-input form, when the operator takes its overlay from the plan rather than a closure.</summary>
    internal OpKernel2? SecondKernel { get; }

    /// <summary>The whole-image second-input form: a format-changing composite whose output leaves the plan's
    /// working format, so it can only run as a compiled format section.</summary>
    internal Func<PixelView, PixelView, WorkingBuffer>? WholeImageSecond { get; }
}
