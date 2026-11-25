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

    internal static bool ShouldFuse(IReadOnlyList<PlanStep> steps, int width, int height, int bytesPerPixel) =>
        ShouldFuse(steps, 0, steps.Count, width, height, bytesPerPixel);

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
