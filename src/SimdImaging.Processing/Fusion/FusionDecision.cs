using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Fusion;

/// <summary>What a section boundary costs the executor that will run it — the two prices this table can quote.</summary>
internal enum FusionBoundary : byte
{
    /// <summary>The whole-image executor: a boundary is an intermediate frame, written once and then read once.</summary>
    Frame = 0,

    /// <summary>The row pump: a boundary is one more stage, holding two band windows for the life of the chain and
    /// copying every row through the transfer buffer once more. Nothing is materialised.</summary>
    RowPump = 1,
}

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
{    /// <summary>
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
    /// <para>Three corrections to the policy band, all measured, all about the same window:</para>
    /// <para>① A halo-carrying segment gets at least four times its accumulated halo, so the window
    /// (<c>band + 2 × Σhalo</c>) stays below 1.5 bands instead of recomputing most of itself per band.</para>
    /// <para>② A plan whose fused segments are all halo-free gets a cache-sized band (about 192 KiB per band,
    /// 8-16 rows) instead of the thread-sized one: with no halo the band loop is pure streaming, so what matters is
    /// that the two buffers stay in cache — measured 0.60-0.66 against the oracle on a four-step point chain with the
    /// thread-sized band, 0.92-0.99 with this one.</para>
    /// <para>③ ① must not run the machine out of bands. The band count is the parallelism here: the loop starts
    /// <c>min(degree, bands)</c> workers, so a band taller than a quarter of the image leaves cores idle. Measured
    /// with fusion forced (the band scan in <c>chaindiag</c>), 20 steps, Σhalo = 52: 1920×1080 is 5 bands at 208 rows
    /// and 180 ms against 170 ms at 180 rows, so the floor does not bite; 1920×400 is 2 bands at the same 208 rows
    /// and ~135 ms against 100 ms at 96 rows (5 bands), which is what ③ exists to prevent. On a short image the
    /// traffic rule usually declines to fuse at all, so ③ only matters when the caller forces fusion or the chain is
    /// long enough to clear 64 MiB of avoided frames.</para>
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

        // Four bands is the floor: enough workers for the arithmetic to beat the overlap it pays, few enough that
        // the 4x-halo window still wins on the shapes measured above.
        var floorBands = Math.Max(1, height / 4);
        var capped = Math.Min(4 * halo, Math.Max(bandRows, floorBands));
        return Math.Min(height, Math.Max(bandRows, capped));
    }

    internal static bool ShouldFuse(IReadOnlyList<PlanStep> steps, int width, int height, int bytesPerPixel,
        FusionBoundary boundary = FusionBoundary.Frame) =>
        ShouldFuse(steps, 0, steps.Count, width, height, bytesPerPixel, boundary);

    /// <summary>
    /// The one decision table every executor uses: contiguous bandable runs, each cut into segments that either
    /// fuse or run as a single step. The runtime and the compiler both consume this list, so a compiled section
    /// and a directly executed plan cannot disagree about which runs fuse.
    /// <para>
    /// A run that is not worth fusing as a whole is <b>not</b> thrown away wholesale: it is split greedily at the
    /// halo budget, so the cheap prefix of a long chain still fuses. Only a sub-segment that itself fails the
    /// table falls back to single steps.
    /// </para>
    /// <para>
    /// <paramref name="boundary"/> is what a section boundary costs. It is a property of the executor, not of the
    /// chain, and the same step list can be priced two ways: the whole-image path materialises a frame, the row pump
    /// links a stage. The compiler passes the one belonging to the plan it is compiling, so a streamed chain is not
    /// cut by a rule written for the executor it is not running on.
    /// </para>
    /// </summary>
    internal static List<(int First, int Count)> PlanSegments(IReadOnlyList<PlanStep> steps, int first, int count,
        int width, int height, int bytesPerPixel, bool allowFusion, bool forceFusion,
        FusionBoundary boundary = FusionBoundary.Frame)
    {
        var segments = new List<(int First, int Count)>();
        foreach (var (runFirst, runCount) in FusionPlanner.Segment(steps, first, count))
        {
            var fused = allowFusion && runCount > 1 &&
                (forceFusion || ShouldFuse(steps, runFirst, runCount, width, height, bytesPerPixel, boundary));
            if (fused)
            {
                segments.Add((runFirst, runCount));
                continue;
            }

            // A run usually fails on the halo budget rather than on traffic: 20 blurs of σ=1 stack to 52 halo rows,
            // so every band would re-run the whole chain over a 52-row overlap on each side. Splitting at the budget
            // keeps the part that fits fusable — [h=5 ×5, h=20 ×3] becomes one fused prefix and three single steps
            // instead of eight. Each sub-segment is then judged by the same table, and a segment boundary is a whole
            // frame, so the split can only choose a cheaper window, never a different byte.
            if (allowFusion && runCount > 1)
            {
                var subStart = runFirst;
                var subHalo = 0;
                for (var i = 0; i < runCount; i++)
                {
                    var stepHalo = steps[runFirst + i].HaloY;
                    if (subStart < runFirst + i && subHalo + stepHalo > MaxFusedHaloRows)
                    {
                        Flush(subStart, runFirst + i - subStart);
                        subStart = runFirst + i;
                        subHalo = 0;
                    }

                    subHalo += stepHalo;
                }

                Flush(subStart, runFirst + runCount - subStart);
            }
            else
            {
                for (var i = 0; i < runCount; i++)
                    segments.Add((runFirst + i, 1));
            }
        }

        return segments;

        // One step is encoded the same way fused or not — the executor runs count == 1 stepwise — so it does not
        // need a decision.
        void Flush(int start, int length)
        {
            if (length == 1 || forceFusion ||
                ShouldFuse(steps, start, length, width, height, bytesPerPixel, boundary))
                segments.Add((start, length));
            else
                for (var j = 0; j < length; j++)
                    segments.Add((start + j, 1));
        }
    }

    /// <summary>The same table for a whole plan.</summary>
    internal static List<(int First, int Count)> PlanSegments(IReadOnlyList<PlanStep> steps, int width, int height,
        int bytesPerPixel, bool allowFusion, bool forceFusion, FusionBoundary boundary = FusionBoundary.Frame) =>
        PlanSegments(steps, 0, steps.Count, width, height, bytesPerPixel, allowFusion, forceFusion, boundary);

    /// <summary>The same decision for one run inside a larger list: the compiler classifies a section at a time and
    /// must not let a neighbouring section's halo or length decide for it.</summary>
    internal static bool ShouldFuse(IReadOnlyList<PlanStep> steps, int first, int count, int width, int height,
        int bytesPerPixel, FusionBoundary boundary = FusionBoundary.Frame)
    {
        // Only HaloY matters for the band-height decision: bands are horizontal slices, so vertical support
        // drives the window size; HaloX never inflates the row count. Using Max(HaloX, HaloY) previously caused
        // steps with HaloX > 0 but HaloY = 0 (e.g. a purely horizontal kernel) to miss the zero-halo fast path
        // and be charged a halo budget they never actually needed.
        var maxHaloY = 0;
        for (var i = first; i < first + count; i++)
            maxHaloY = Math.Max(maxHaloY, steps[i].HaloY);

        var pixels = (long)width * height;

        // Below the parallel gate every step would run on the calling thread: fusion is the only way this image
        // uses more than one core, and it measured 0.25-0.46 at 160x120 and 320x180.
        if (pixels < RowParallel.MinWorkPixels)
            return true;

        // A chain with no halo costs nothing to fuse: there is no window to recompute, only a frame to skip.
        // Measured 0.80-0.84 from 640x360 to 8K for the chains in the table above.
        if (maxHaloY == 0)
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

        // A row-pump boundary is a stage, not a frame, so the traffic test below is answering the wrong question here.
        // Fusing costs re-running the segment over 2 × Σhalo rows per band; splitting costs one extra stage — two band
        // windows held for the life of the chain plus one more copy of every row. Measured on blur+invert (the
        // two-step chain a transcode runs), as decided versus forced, bytes identical both times:
        //   1080p  303.3 ms / 36.7 MiB  ->  298.6 ms / 30.7 MiB
        //   4K     691.0 ms / 55.1 MiB  ->  692.9 ms / 50.4 MiB      (benchmarks … -- fusionab)
        // Equal-or-better on time and 16% / 8% smaller, so within the halo budget the pump puts the whole run in one
        // segment and the frame-sized traffic test keeps only the meaning it has for the whole-image executor.
        if (boundary == FusionBoundary.RowPump)
            return true;

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
