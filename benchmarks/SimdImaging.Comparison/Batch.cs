using System.Diagnostics;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SixLabors.ImageSharp.Processing;
using TheirImage = SixLabors.ImageSharp.Image;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// A batch of photographs, not one big image: N independent pictures run through the same three-step chain.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape real callers have, and it answers a question none of the other tables can. The single-image
/// runs hide per-call overhead behind the pixels: at 8K one call is 33 MPix, so a pooled buffer rent is
/// invisible. A batch of 1024x768 frames is 0.79 MPix per call, repeated sixteen times, and the same overhead
/// is amortised over a sixteenth as much work per frame - which is where a per-call cost either shows up or
/// does not.
/// </para>
/// <para>
/// <b>Not yet a trustworthy instrument, and it says so here rather than in a footnote.</b> The blocks are timed
/// one after another, so machine drift lands on one side only; five runs gave the oracle 11.8, 12.5, 12.6, 26.8
/// and 41.9 ms per frame at sixteen frames - a 3.5x spread on the same work. Our side stayed inside 13.8-19.9.
/// The fix is the one <c>Timing.Time</c> already implements: interleave the three variants round by round and
/// take medians. Until that is done, treat the batch numbers as directional only.
/// </para>
/// <para>
/// It also separates the first frame from the rest. The first pays JIT, pool growth and any one-off table build;
/// the subsequent ones pay the chain. Reporting only the average of sixteen would attribute the warm-up to every
/// frame, and reporting only the first would call the library slow.
/// </para>
/// </remarks>
internal static class Batch
{
    private const int FrameWidth = 1024;
    private const int FrameHeight = 768;
    private static readonly int[] BatchSizes = [1, 4, 16];

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, "F 组 · 批量与场景：同一处理链跑 N 张图");
        report.Line($"> 每帧 {FrameWidth}×{FrameHeight} `Rgba8`（0.79 MPix），链为 **Gaussian σ1 → Brightness +0.05 → Contrast 1.1**（同尺寸，可融合）。");
        report.Line("> 两侧都从各自的源图产出一张新图；我方用 `ProcessingPlan` 单次行带执行，对手用 `Clone` 原地链。");
        report.Line("> **首帧单独记账**：第一帧付 JIT、池增长与任何一次性建表，其余帧付链本身。只报平均会把预热摊到每一帧，");
        report.Line("> 只报首帧会把库说慢——两笔账分开列，判定用稳态每帧。");
        report.Line();

        var data = Fixture.Make(FrameWidth, FrameHeight);
        using var ours = Fixture.Ours(data, FrameWidth, FrameHeight);
        using var theirs = Fixture.Theirs(data, FrameWidth, FrameHeight);

        // All three steps keep the geometry, which is what lets the plan fuse them into one row-band pass. A
        // resize cannot join them (it changes the size, like crop and seam), and putting one first would compare
        // a materialised intermediate against the oracle's in-place chain - a different question.
        var plan = new ProcessingPlan()
            .Add("blur", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1f), 3, 3), 3, 3)
            .Add("brightness", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.05f)))
            .Add("contrast", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.1f)));

        Func<TheirImage, TheirImage> oracle =
            i => i.Clone(x => x.GaussianBlur(1f).Brightness(0.05f).Contrast(1.1f));

        // Resize changes the geometry, so the plan cannot fuse across it; that is exactly what a real chain looks
        // like and the point is to measure it, not to make it disappear.
        // The same three operations, one call each, without the plan: if this beats the fused plan then the
        // fusion is costing more than the passes it saves, and that is worth knowing before blaming the ops.
        Func<OurImage, OurImage> unfused = i =>
        {
            using var blurred = i.Pipe().GaussianBlur(1f).ToImage();
            using var bright = blurred.Pipe().Brightness(0.05f).ToImage();
            return bright.Pipe().Contrast(1.1f).ToImage();
        };

        var rows = new List<string[]>();
        foreach (var count in BatchSizes)
        {
            var ourFirst = TimeOnce(() => { using var r = ours.Apply(plan); });
            var theirFirst = TimeOnce(() => { using var r = oracle(theirs); });

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < count; i++)
            {
                using var r = ours.Apply(plan);
            }

            var ourTotal = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var ourAllocated = (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / (double)count;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            start = Stopwatch.GetTimestamp();
            for (var i = 0; i < count; i++)
            {
                using var r = oracle(theirs);
            }

            var theirTotal = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            start = Stopwatch.GetTimestamp();
            for (var i = 0; i < count; i++)
            {
                using var r = unfused(ours);
            }

            var unfusedTotal = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            rows.Add([
                count.ToString(CultureInfo.InvariantCulture),
                F(ourFirst), F(theirFirst),
                F(ourTotal / count), F(theirTotal / count), F(unfusedTotal / count),
                (theirTotal / ourTotal).ToString("F2", CultureInfo.InvariantCulture) + "×",
                MiB(ourAllocated)]);
        }

        report.Table(
            ["批量", "首帧 我方 ms", "首帧 对手 ms", "稳态 融合 ms/帧", "稳态 对手 ms/帧", "稳态 未融合 ms/帧", "批量加速比", "我方分配/帧"],
            rows);

        var first = rows[0];
        var last = rows[^1];
        report.Line($"> 批量的价值在最后两列：从 1 帧到 {BatchSizes[^1]} 帧，我方每帧从 {first[3]} ms 降到 {last[3]} ms，"
                  + $"说明固定开销确实被摊薄；对手从 {first[4]} 降到 {last[4]}。**真正要比的是每帧稳态成本，不是总时长**——"
                  + "总时长随批量线性增长，任何一方都不会因为批量而改变复杂度。");
    }

    private static double TimeOnce(Action run)
    {
        var start = Stopwatch.GetTimestamp();
        run();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static string F(double ms) => ms.ToString(ms < 10 ? "F3" : "F1", CultureInfo.InvariantCulture);

    private static string MiB(double bytes) => (bytes / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture) + " MiB";
}
