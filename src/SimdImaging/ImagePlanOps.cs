using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Planned execution: a chain of kernels run band by band.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Runs <paramref name="plan"/> over this image and returns the last step's result. Same-size steps are
    /// split into row bands; global steps run whole.
    /// </summary>
    /// <param name="plan">The steps, in order.</param>
    /// <param name="pool">Where the intermediate and output buffers are rented.</param>
    /// <param name="observe">Optional per-step hook, called while the step's output is still alive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    public Image Apply(ProcessingPlan plan, IPixelPool? pool = null, PlanObserver? observe = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ReplaceBuffer(plan.Execute(this[0].Pixels.View, pool, observe));
    }
}
