using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Internal;

/// <summary>
/// The <see cref="ExecuteOptions.MaxWorkingBytes"/> rule, in one place: the two sequential-band executors and the
/// whole-image one must refuse the same plans for the same reasons, or a caller learns three different truths about
/// the same ceiling.
/// </summary>
internal static class WorkingSetBudget
{
    /// <param name="windows">Window buffers the executor holds at once (2 for a single step, 4 for a chain).</param>
    /// <param name="extraBytes">Bytes held besides the windows — the destination region, for instance.</param>
    /// <param name="pathName">What to call this executor in the message ("Streaming", "The region").</param>
    public static void Ensure(IReadOnlyList<PlanStep> steps, ExecuteOptions options, int width, int height,
        int windowCols, int windowRows, WorkingFormat format, int windows, long extraBytes, string pathName)
    {
        if (options.MaxWorkingBytes <= 0)
            return;

        var estimate = ((long)windows * windowCols * windowRows * format.BytesPerPixel()) + extraBytes;
        var undeclared = 0;
        var perWorker = 0L;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Scratch is not { } scratch)
            {
                undeclared++;
                continue;
            }

            perWorker = Math.Max(perWorker, scratch(windowCols, height, windowRows, format));
        }

        if (undeclared > 0 && !options.AllowUnestimatedScratch)
            throw new InvalidOperationException(
                $"{pathName} sets a {options.MaxWorkingBytes}-byte ceiling but {undeclared} of the {steps.Count} "
                + "steps declare no scratch, so the estimate is a lower bound and cannot enforce that ceiling. "
                + "Declare the operator's scratch, raise the ceiling, drop it, or set "
                + "ExecuteOptions.AllowUnestimatedScratch to accept the lower bound.");

        var workers = Math.Max(1, options.MaxDegreeOfParallelism > 0
            ? options.MaxDegreeOfParallelism
            : Environment.ProcessorCount);
        estimate += perWorker * workers;

        if (estimate > options.MaxWorkingBytes)
            throw new InvalidOperationException(
                $"{pathName} would need an estimated {estimate} bytes of working set, above the "
                + $"{options.MaxWorkingBytes}-byte ceiling in ExecuteOptions.MaxWorkingBytes.");
    }
}
