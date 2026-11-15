using System.Buffers;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// Did the plan actually fuse? Wall-clock alone cannot say — a slower machine is indistinguishable from an
/// unfused chain — so this counts what the executor rents from the pool.
/// </summary>
/// <remarks>
/// <para>
/// A fused band window keeps one output buffer and reads the source as it goes; a step-by-step chain
/// materialises an image between every step. The rent count is therefore structural evidence: one rent for a
/// plan that fused, N for N steps that did not.
/// </para>
/// <para>
/// The pool spy is the right instrument because it does not change execution. <c>PlanObserver</c> would report
/// the segmentation directly, but its documentation says a present observer forces the step-by-step path —
/// that would answer a question about a path that only runs while it is being watched.
/// </para>
/// </remarks>
internal static class Fusion
{
    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, "G 组 · 融合是否发生：按租用次数判定");
        report.Line($"> 输入 {width}×{height} `Rgba8`。**判定不看时间，看 `IPixelPool.Rent` 的次数**：");
        report.Line("> 融合的行带窗口只租一块输出、边读源边算；逐步执行会在每一级之间落一张中间图。");
        report.Line("> 计时用 `Timing.Time`（≥300 ms 热身 + 7 轮交错取中位），与其它表同一口径。");
        report.Line();

        var data = Fixture.Make(width, height);
        using var source = Fixture.Ours(data, width, height);

        var threePoint = new ProcessingPlan()
            .Add("brightness", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.05f)))
            .Add("contrast", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.1f)))
            .Add("invert", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)));

        var blurThenPoint = new ProcessingPlan()
            .Add("blur", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1f), 3, 3), 3, 3)
            .Add("brightness", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.05f)))
            .Add("contrast", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.1f)));

        var rows = new List<string[]>
        {
            Measure("3 个点算子（计划）", pool => source.Apply(threePoint, pool)),
            Measure("模糊 + 2 点算子（计划）", pool => source.Apply(blurThenPoint, pool)),
            Measure("同样三级，逐次调用", pool =>
            {
                using var a = source.GaussianBlur(1f, pool);
                using var b = a.Brightness(0.05f, pool);
                return b.Contrast(1.1f, pool);
            }),
            Measure("3 个点算子，逐次调用", pool =>
            {
                using var a = source.Brightness(0.05f, pool);
                using var b = a.Contrast(1.1f, pool);
                return b.Invert(pool);
            }),
        };

        report.Table(["链条", "Rent 次数", "租用总量", "每帧 ms", "托管分配/次"], rows);

        report.Line($"> 判读：「模糊 + 2 点算子」的计划租了 **{rows[1][1]}** 次，逐次调用是 **{rows[2][1]}** 次。"
                  + "若两者相同，说明这一串**没有融合**，融合省下的内存往返并不存在；若计划是 1 次，融合成立。");
    }

    private static string[] Measure(string name, Func<IPixelPool?, OurImage> run)
    {
        // Warm once so the pool is populated and the count reflects the steady state rather than first-touch.
        using (var warm = run(null))
        {
        }

        var spy = new CountingPool();
        using (var result = run(spy))
        {
        }

        var before = GC.GetTotalAllocatedBytes(precise: true);
        using (var result = run(spy))
        {
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        var times = Timing.Time([new Runner("run", () => { using var r = run(null); })]);

        return
        [
            name,
            spy.Rents.ToString(CultureInfo.InvariantCulture),
            (spy.Bytes / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture) + " MiB",
            Timing.F(times["run"]),
            (allocated / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KiB",
        ];
    }

    /// <summary>Delegates to the default pool and counts what passes through it.</summary>
    private sealed class CountingPool : IPixelPool
    {
        private readonly IPixelPool _inner = ArrayPixelPool.Shared;

        public int Rents;
        public long Bytes;

        public IMemoryOwner<byte> Rent(int length)
        {
            Interlocked.Increment(ref Rents);
            Interlocked.Add(ref Bytes, length);
            return _inner.Rent(length);
        }

        public string Name => "counting";
    }
}
