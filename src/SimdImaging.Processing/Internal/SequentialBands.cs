using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Internal;

/// <summary>
/// Band height for an executor that runs its bands one after another (the row pump, the region pump): the band is
/// the unit whose <em>rows</em> are parallelised, so it has to be tall enough to keep the machine busy, and small
/// enough that one window stays cache-sized.
/// <para>This is deliberately not <c>ProcessingPlan.BandRowsFor</c>: there the band count is the parallelism, so
/// many small bands are right. Here a band below <see cref="RowParallel.MinWorkPixels"/> runs its operators on the
/// calling thread — the band is sequential and nothing else is left — which measured 3–5× slower on a 512-wide
/// region (64-row bands: 12–37 ms; 192- and 256-row bands: 3 ms for the same pixels).</para>
/// </summary>
internal static class SequentialBands
{
    /// <summary>Bytes one window may take. Four windows are alive at once in the worst case, so this is a quarter
    /// of the executor's window budget; large enough to keep the row loop parallel at 1080p, small enough that an
    /// 8K stream stays in single-digit megabytes.</summary>
    private const long WindowBudgetBytes = 4L << 20;

    /// <param name="width">Width of one band — the window's width, halo included.</param>
    /// <param name="degree">The configured degree; 0 means the machine default.</param>
    public static int For(ProcessingPlan plan, int width, int height, WorkingFormat format, int degree)
    {
        if (plan.BandRows != ProcessingPlan.DefaultBandRows)
            return Math.Max(1, Math.Min(plan.BandRows, height));

        var workers = Math.Max(1, degree > 0 ? degree : Environment.ProcessorCount);

        // Two floors: enough rows for every worker (the row loop splits by RowsPerWorker), and enough pixels for the
        // loop to parallelise at all.
        var byWorkers = workers * RowParallel.RowsPerWorker;
        var byPixels = (int)Math.Min(int.MaxValue,
            (RowParallel.MinWorkPixels + Math.Max(1, width) - 1) / Math.Max(1, width));

        var rowBytes = Math.Max(1L, (long)width * format.BytesPerPixel());
        var byBudget = (int)Math.Clamp(WindowBudgetBytes / rowBytes, 8, 1 << 20);

        var rows = Math.Min(Math.Max(byWorkers, byPixels), byBudget);
        return Math.Max(1, Math.Min(rows, height));
    }
}
