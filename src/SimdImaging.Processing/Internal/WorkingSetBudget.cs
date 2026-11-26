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
    /// <summary>The bytes the executor would hold: windows, the declared scratch for every worker, and
    /// <paramref name="extraBytes"/> (the destination region, for instance).</summary>
    internal static long Measure(IReadOnlyList<PlanStep> steps, ExecuteOptions options, int width, int height,
        int windowCols, int windowRows, WorkingFormat format, int windows, long extraBytes, out int undeclared)
    {
        var estimate = ((long)windows * windowCols * windowRows * format.BytesPerPixel()) + extraBytes;
        undeclared = 0;
        var perWorker = 0L;
        var wholeImage = 0L;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Scratch is not { } scratch)
            {
                undeclared++;
                continue;
            }

            // The declared scope is part of the contract, not a hint: the same rule MemoryEstimator applies to
            // the frame path, so a ceiling means the same thing on all three executors.
            var isWholeImage = steps[i].Category is OpCategory.Global or OpCategory.Composite
                || steps[i].Definition.Contract.Workspace.Scope == WorkspaceScope.WholeImage;
            if (isWholeImage)
            {
                wholeImage = Math.Max(wholeImage, scratch(width, height, height, format));
                continue;
            }

            perWorker = Math.Max(perWorker, scratch(windowCols, height, windowRows, format));
        }

        var workers = Math.Max(1, options.MaxDegreeOfParallelism > 0
            ? options.MaxDegreeOfParallelism
            : Environment.ProcessorCount);
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

    /// <param name="windows">Window buffers the executor holds at once (2 for a single step, 4 for a chain).</param>
    /// <param name="extraBytes">Bytes held besides the windows — the destination region, for instance.</param>
    /// <param name="pathName">What to call this executor in the message ("Streaming", "The region").</param>
    public static void Ensure(IReadOnlyList<PlanStep> steps, ExecuteOptions options, int width, int height,
        int windowCols, int windowRows, WorkingFormat format, int windows, long extraBytes, string pathName)
    {
        var estimate = Measure(steps, options, width, height, windowCols, windowRows, format, windows, extraBytes,
            out var undeclared);
        Ensure(estimate, undeclared, steps.Count, options, pathName);
    }
}
