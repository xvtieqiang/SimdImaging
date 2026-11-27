using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Fusion;

/// <summary>
/// Whether a segment is worth fusing, decided from what the segment contains and how big the image is.
/// </summary>
/// <remarks>
/// <para>
/// Fusing trades two things: it saves one full-frame write and read per intermediate, and it pays for the halo
/// rows recomputed at every band boundary plus a fixed cost per band. Which side wins is not a property of the
/// kernel size — it is a property of whether the steps can use the machine some other way.
/// </para>
/// <para>
/// An earlier version of this rule read "fuse when the segment contains a small kernel", and at the time it was
/// right: <c>Morph3x3</c>, <c>Convolve3x3</c> and the combine loop in <c>Sharpen</c> ran on one thread, so the
/// fused band was the only path that used more than one core, and a 3×3 chain read 0.45 (fusion 2.2x faster).
/// Those operators now parallelise over rows themselves. Re-measured with the two sides interleaved in one
/// timing call (six sizes, three runs, plan ÷ step-by-step):
/// </para>
/// <para>
/// <code>
///           160x120  320x180  640x360  1080p   4K      8K
/// point x2    0.59     0.42     0.84    0.83   0.83    0.80
/// 3x3 x2      0.25     0.27     1.37    1.23   1.05    1.11
/// 3x3 x3      0.46     0.32     1.08    1.00   1.03    1.16
/// blur+2pt    1.07     0.87     1.12    1.05   1.05    1.24
/// </code>
/// </para>
/// <para>
/// The curve is not monotonic, and the reason is the parallel gate: below it a step runs on one thread, so
/// fusion is the only source of parallelism and wins 2-4x; above it every step is already using the cores, and
/// fusion keeps only the traffic it saves, which at 640x360 is less than the halo it recomputes. A chain of
/// points is the one shape that wins at every size above the gate, because it has no halo to recompute. What
/// is left for the size test is the purpose fusion was built for: not materialising N full frames.
/// </para>
/// <para>
/// The table above is short chains. A chain whose halos accumulate past <see cref="MaxFusedHaloRows"/> is kept
/// stepwise whatever traffic it would save: the window is <c>band + 2 × Σhalo</c> and every band re-runs the whole
/// segment over that overlap, so on a 20-step chain (Σhalo = 52) fusion measured 252 ms against 157 ms stepwise at
/// 1080p and 986 ms against 607 ms at 4K. The band height for the segments that do fuse is lifted to four times
/// their halo, which is also why the numbers in the table (measured at the old band policy) are a lower bound on
/// how much a fused chain can win.
/// </para>
/// <para>
/// The traffic is counted in the source's own bytes per pixel, over the intermediates fusion actually removes.
/// Both halves of that sentence were once wrong: the rule multiplied by 4 whatever the format was (wrong by 2x
/// for <c>Rgba16</c> and 4x for <c>Rgba32F</c>), and it used <c>steps.Count - 1</c> even though a Global step
/// splits the chain and the executor then fuses each side separately, so a plan whose only bandable run is two
/// steps long was credited with avoiding a whole chain's worth of frames.
/// </para>
/// <para>
/// Lives here rather than on <see cref="ProcessingPlan"/> because the compiler and the plan must make this
/// decision the same way: two copies of a heuristic are two heuristics.
/// </para>
/// </remarks>
internal static class FusionDecision
{
    /// <summary>
    /// The worth of the full-size intermediates fusion avoids, in bytes, above which fusing is justified even when
    /// the steps are expensive: below this the pass is cheap enough that its arithmetic, not its memory traffic,
    /// sets the time.
    /// </summary>
    internal const long FuseIntermediateBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The most halo rows a single fused segment may accumulate. The window is <c>band + 2 × Σhalo</c> and every
    /// band re-runs every step over that overlap, so past this budget the recompute grows with the chain while the
    /// frames fusion avoids stay one per step. 32 rows is half a 64-row band: at the default band heights the
    /// window never more than doubles, and <c>2 × Σhalo</c> stays inside the 128-row band the compiler sizes its
    /// window prediction with.
    /// </summary>
    internal const int MaxFusedHaloRows = 32;

    /// <summary>Bytes one band may occupy before the ping-pong buffers stop fitting a core's cache. Measured on the
    /// four-step point chain: 192 KiB is 16 rows at 1080p and 12 at 4K, the two points that measured fastest
    /// (0.91x and 1.05x against the oracle), while the thread-sized band (67 rows, 515 KiB) is 25-30% slower.</summary>
    internal const int PointBandBytes = 192 * 1024;

    /// <summary>
    /// The band height an execution actually uses, for the default band policy only — an explicit
    /// <c>BandRows</c> is a geometry request and is honoured as given.
    /// <para>Two corrections to the policy band, both measured, both about the same window:</para>
    /// <para>① A halo-carrying segment gets at least four times its accumulated halo, so the window
    /// (<c>band + 2 × Σhalo</c>) stays below 1.5 bands instead of recomputing most of itself per band.</para>
    /// <para>② A plan whose fused segments are all halo-free gets a cache-sized band (about 128 KiB per band,
    /// 8-16 rows) instead of the thread-sized one: with no halo the band loop is pure streaming, so what matters is
    /// that the two buffers stay in cache. The thread-sized band measured 0.60-0.66 against the oracle on a
    /// four-step point chain; the cache-sized one 0.92-0.99.</para>
    /// </summary>
    internal static int ExecBandRows(IReadOnlyList<PlanStep> steps, IReadOnlyList<(int First, int Count)> segments,
        int width, int height, int bytesPerPixel, int bandRows)
    {
        var halo = 0;
        var anyFused = false;
        foreach (var (first, count) in segments)
        {
            if (count < 2)
                continue;

            anyFused = true;
            var sum = 0;
            for (var i = first; i < first + count; i++)
                sum += steps[i].HaloY;
            halo = Math.Max(halo, sum);
        }

        if (!anyFused)
            return bandRows;

        if (halo == 0)
        {
            var cacheRows = Math.Clamp(PointBandBytes / Math.Max(1, width * bytesPerPixel), 8, 16);
            return Math.Min(height, Math.Min(bandRows, cacheRows));
        }

        return Math.Min(height, Math.Max(bandRows, 4 * halo));
    }

    internal static bool ShouldFuse(IReadOnlyList<PlanStep> steps, int width, int height, int bytesPerPixel) =>
        ShouldFuse(steps, 0, steps.Count, width, height, bytesPerPixel);

    /// <summary>
    /// The one decision table every executor uses: contiguous bandable runs, each either fused as a whole or
    /// split into single steps. The runtime and the compiler both consume this list, so a compiled section and
    /// a directly executed plan cannot disagree about which runs fuse.
    /// </summary>
    internal static List<(int First, int Count)> PlanSegments(IReadOnlyList<PlanStep> steps, int first, int count,
        int width, int height, int bytesPerPixel, bool allowFusion, bool forceFusion)
    {
        var segments = new List<(int First, int Count)>();
        foreach (var (runFirst, runCount) in FusionPlanner.Segment(steps, first, count))
        {
            var fused = allowFusion && runCount > 1 &&
                (forceFusion || ShouldFuse(steps, runFirst, runCount, width, height, bytesPerPixel));
            if (fused)
            {
                segments.Add((runFirst, runCount));
                continue;
            }

            for (var i = 0; i < runCount; i++)
                segments.Add((runFirst + i, 1));
        }

        return segments;
    }

    /// <summary>The same table for a whole plan.</summary>
    internal static List<(int First, int Count)> PlanSegments(IReadOnlyList<PlanStep> steps, int width, int height,
        int bytesPerPixel, bool allowFusion, bool forceFusion) =>
        PlanSegments(steps, 0, steps.Count, width, height, bytesPerPixel, allowFusion, forceFusion);

    /// <summary>The same decision for one run inside a larger list: the compiler classifies a section at a time and
    /// must not let a neighbouring section's halo or length decide for it.</summary>
    internal static bool ShouldFuse(IReadOnlyList<PlanStep> steps, int first, int count, int width, int height,
        int bytesPerPixel)
    {
        var maxHalo = 0;
        for (var i = first; i < first + count; i++)
        {
            var step = steps[i];
            maxHalo = Math.Max(maxHalo, Math.Max(step.HaloX, step.HaloY));
        }

        var pixels = (long)width * height;

        // Below the parallel gate every step would run on the calling thread: fusion is the only way this image
        // uses more than one core, and it measured 0.25-0.46 at 160x120 and 320x180.
        if (pixels < RowParallel.MinWorkPixels)
            return true;

        // A chain with no halo costs nothing to fuse: there is no window to recompute, only a frame to skip.
        // Measured 0.80-0.84 from 640x360 to 8K for the chains in the table above.
        if (maxHalo == 0)
            return true;

        // The halo is charged once per band, so a long chain of filters multiplies it: the window is
        // band + 2 * Σhalo, and every band re-runs every step on the overlap. Past this budget fusion's
        // recompute outweighs the frames it removes regardless of band height — measured on a 20-step chain
        // (Σhalo = 52) at 1080p: fused 252 ms versus 157 ms step by step, and at 4K 986 ms versus 607 ms.
        // The budget also bounds the runtime band: 2 * Σhalo stays within the 128-row band the compiler
        // predicted its windows with, so the estimate stays an upper bound.
        var haloSum = 0;
        for (var i = first; i < first + count; i++)
            haloSum += steps[i].HaloY;
        if (haloSum > MaxFusedHaloRows)
            return false;

        // Everything else keeps fusion for what it was built for — not materialising N full frames. Count them
        // per contiguous bandable run: a category boundary already forces a full frame, so only the steps on this
        // side of it are traffic fusion can avoid. Counted without allocating a segment list — this runs per call.
        long avoided = 0;
        var run = 0;
        for (var i = first; i < first + count; i++)
        {
            if (FusionPlanner.IsBandable(steps[i]))
            {
                run++;
                continue;
            }

            if (run > 1)
                avoided += (long)(run - 1) * pixels * bytesPerPixel;
            run = 0;
        }

        if (run > 1)
            avoided += (long)(run - 1) * pixels * bytesPerPixel;

        return avoided > FuseIntermediateBytes;
    }
}
