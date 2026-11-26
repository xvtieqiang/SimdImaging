using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Fusion;

/// <summary>
/// Cuts a plan's step chain into contiguous execution segments: consecutive band-able steps
/// (Point / Neighborhood / Separable) form fused segments; everything else (Global / Composite /
/// Geometric) runs as its own whole-image segment. Geometric steps cannot appear — <c>ProcessingPlan.Add</c>
/// rejects them.
/// </summary>
internal static class FusionPlanner
{
    /// <summary>Whether a step may be executed in row bands and joined with its neighbours. A bandable category
    /// is not enough: a step whose contract requires one whole-image invocation cannot share a fused window.</summary>
    public static bool IsBandable(PlanStep step) =>
        step.Category is OpCategory.Point or OpCategory.Neighborhood or OpCategory.Separable &&
        !step.Definition.Contract.Requirements.RequiresWholeImageInvocation;

    /// <summary>Segments the chain into (firstStep, stepCount) runs, in execution order.</summary>
    public static List<(int First, int Count)> Segment(IReadOnlyList<PlanStep> steps)
    {
        var segments = new List<(int First, int Count)>();
        var index = 0;
        while (index < steps.Count)
        {
            var count = 1;
            if (IsBandable(steps[index]))
            {
                while (index + count < steps.Count && IsBandable(steps[index + count]))
                    count++;
            }

            segments.Add((index, count));
            index += count;
        }

        return segments;
    }
}
