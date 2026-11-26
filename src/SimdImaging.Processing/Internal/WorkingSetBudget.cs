using SimdImaging.Abstractions;
using SimdImaging.Processing.Contracts;

namespace SimdImaging.Processing.Internal;

/// <summary>
/// The <see cref="ExecuteOptions.MaxWorkingBytes"/> rule, in one place: the two sequential-band executors and the
/// whole-image one must refuse the same plans for the same reasons, or a caller learns three different truths about
/// the same ceiling.
/// </summary>
internal static class WorkingSetBudget
{
    /// <summary>The bytes the executor holds: its windows plus the declared scratch for every worker. A target
    /// buffer is not part of this measurement — it belongs to whoever rents it, and <see cref="TargetBuffer"/>
    /// reserves it before the rent.</summary>
    internal static long Measure(IReadOnlyList<PlanStep> steps, ExecuteOptions options, int width, int height,
        int windowCols, int windowRows, WorkingFormat format, int windows, out int undeclared)
    {
        var estimate = (long)windows * windowCols * windowRows * format.BytesPerPixel();
        undeclared = 0;
        var perWorker = 0L;
        var wholeImage = 0L;
        for (var i = 0; i < steps.Count; i++)
        {
            var workspace = steps[i].Definition.Contract.Workspace;
            if (!workspace.IsDeclared)
            {
                undeclared++;
                continue;
            }

            var wholeImageInvocation =
                steps[i].Definition.Contract.Requirements.Partition == Partitioning.WholeImage;
            if (workspace.HasWholeImageEstimate ||
                (wholeImageInvocation && workspace.PerCallEstimate is not null))
            {
                wholeImage = Math.Max(wholeImage,
                    workspace.EstimateWholeImage(width, height, format));
            }

            if (workspace.HasPerWorkerEstimate ||
                (!wholeImageInvocation && workspace.PerCallEstimate is not null))
            {
                perWorker = Math.Max(perWorker,
                    workspace.EstimatePerWorker(windowCols, height, windowRows, format));
            }
        }

        var workers = RowParallel.ResolveWorkers(windowCols, windowRows, options.MaxDegreeOfParallelism);
        return estimate + (perWorker * workers) + wholeImage;
    }

    /// <summary>Checks a measurement against <see cref="ExecuteOptions.MaxWorkingBytes"/>.</summary>
    internal static void Ensure(long estimate, int undeclared, int stepCount, ExecuteOptions options, string pathName)
    {
        if (options.MaxWorkingBytes <= 0)
            return;

        if (undeclared > 0 && !options.AllowUnestimatedScratch)
            throw new InvalidOperationException(
                $"{pathName} sets a {options.MaxWorkingBytes}-byte ceiling but {undeclared} of the {stepCount} "
                + "steps declare no scratch, so the estimate is a lower bound and cannot enforce that ceiling. "
                + "Declare the operator's scratch, raise the ceiling, drop it, or set "
                + "ExecuteOptions.AllowUnestimatedScratch to accept the lower bound.");

        if (estimate > options.MaxWorkingBytes)
            throw new InvalidOperationException(
                $"{pathName} would need an estimated {estimate} bytes of working set, above the "
                + $"{options.MaxWorkingBytes}-byte ceiling in ExecuteOptions.MaxWorkingBytes.");
    }
}
