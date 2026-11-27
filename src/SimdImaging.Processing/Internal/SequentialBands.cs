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
/// <para>
/// <b>The row pump has a second bound: the whole window set may not exceed one frame.</b> Its reason to exist is that
/// the source is never materialised, so a shape that holds more than a frame is strictly worse than not streaming —
/// the compression is the only thing bought, and it was not asked for. At 1080p the worker rule above gives 512-row
/// bands, and four of those (cache, output, two ping-pong) are 2048 rows: 1.9 frames, measured as 31.0 MiB of peak
/// against 25.2 MiB for the oracle on a three-step point chain. The bound is `height / windows - 2 × Σhalo`, which is
/// a comparison with the alternative rather than a constant, so 4K and 8K are untouched (their bands are already
/// under it) and every future size and format lands by itself.
/// </para>
/// </summary>
internal static class SequentialBands
{
    /// <summary>Bytes one window may take. Four windows are alive at once in the worst case, so this is a quarter
    /// of the executor's window budget; large enough to keep the row loop parallel at 1080p, small enough that an
    /// 8K stream stays in single-digit megabytes.</summary>
    private const long WindowBudgetBytes = 4L << 20;

    /// <summary>Buffers one stage holds, read off the alternation the band loop actually runs rather than off a
    /// constant: the row cache and the result always, plus one intermediate for two steps, plus both ping-pong
    /// windows from three steps on. A two-step chain writes its single intermediate into the first scratch and
    /// hands it to the last step, which writes the output — the second slot is rented and never touched, and that
    /// was a quarter of the predicted peak spent on nothing on exactly the two-step shape a transcode runs
    /// (blur + invert). One function because four copies of `steps.Count > 1 ? 4 : 2` had already drifted apart
    /// once. Zero steps rent nothing, which is what the estimators' window term has to say for an empty chain.</summary>
    public static int Windows(IReadOnlyList<PlanStep> steps) => steps.Count switch
    {
        0 => 0,
        1 => 2,
        2 => 3,
        _ => 4,
    };

    /// <param name="width">Width of one band — the window's width, halo included.</param>
    /// <param name="degree">The configured degree; 0 means the machine default.</param>
    /// <param name="withinOneFrame">True for the row pump, whose alternative is a materialised frame; false where no
    /// such alternative exists (a region or tile run never holds one, so capping there would only cost workers).</param>
    public static int For(ProcessingPlan plan, int width, int height, WorkingFormat format, int degree,
        bool withinOneFrame = false)
    {
        // Zero is the sentinel ("let the executor size it"); every positive value is the caller's geometry, 128
        // included. Comparing against DefaultBandRows instead made an explicit 128 indistinguishable from "auto".
        if (plan.BandRows > 0)
            return Math.Max(1, Math.Min(plan.BandRows, height));

        var workers = Math.Max(1, degree > 0 ? degree : Environment.ProcessorCount);

        // Two floors: enough rows for every worker (the row loop splits by RowsPerWorker), and enough pixels for the
        // loop to parallelise at all.
        var byWorkers = workers * RowParallel.RowsPerWorker;
        var byPixels = (int)Math.Min(int.MaxValue,
            (RowParallel.MinWorkPixels + Math.Max(1, width) - 1) / Math.Max(1, width));

        var rowBytes = Math.Max(1L, (long)width * format.BytesPerPixel());
        var cap = (int)Math.Clamp(WindowBudgetBytes / rowBytes, 8, 1 << 20);

        if (withinOneFrame)
        {
            var halo = 0;
            foreach (var step in plan.Steps)
                halo = checked(halo + step.HaloY);

            var frameRows = height / Math.Max(1, Windows(plan.Steps));
            cap = Math.Min(cap, Math.Max(8, frameRows - (2 * halo)));
        }

        var rows = Math.Min(Math.Max(byWorkers, byPixels), cap);
        return Math.Max(1, Math.Min(rows, height));
    }
}
