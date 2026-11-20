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
    /// <summary>Four sizes, because the per-band cost is a fixed one and only a sweep shows it being amortised.</summary>
    private static readonly (int Width, int Height)[] Sizes =
        [(160, 120), (320, 180), (640, 360), (1920, 1080), (3840, 2160), (7680, 4320)];

    public static void Write(Report report, int width, int height)
    {
        // `fusion 320x180` sweeps one size; the default runs the whole ladder. The small end exists because the
        // per-band cost is fixed and only a sweep shows where it stops being amortised.
        var sizes = width > 0 && height > 0 ? [(width, height)] : Sizes;
        report.Heading(2, "G 组 · 融合是否发生，以及它的代价随尺寸怎么变");
        report.Line("> **判定不看时间，看托管分配与 `IPixelPool.Rent`**：融合的行带窗口只租输出、边读源边算；");
        report.Line("> 逐步执行会在每一级之间落一张中间图。计时用 `Timing.Time`（≥300 ms 热身 + 7 轮交错取中位）。");
        report.Line("> 质量维度取**链长**（1 / 3 / 5 个点算子）与**模糊半径**（σ=1 / 3 / 8）：链越长、半径越大，");
        report.Line("> 融合省下的中间图越多，而每带的固定成本被摊得越薄——这两件事必须分尺寸看。");
        report.Line();

        foreach (var (w, h) in sizes)
        {
            Console.WriteLine($"[fusion] {w}x{h}...");
            var data = Fixture.Make(w, h);
            using var source = Fixture.Ours(data, w, h);

            var rows = new List<string[]>();
            rows.Add(Measure(source, "点算子 ×1", PointPlan(1), pool => source.Apply(PointPlan(1), pool), pool => source.Pipe().Brightness(0.05f).ToImage()));
            rows.Add(Measure(source, "点算子 ×3", PointPlan(3), pool => source.Apply(PointPlan(3), pool),
                pool => { using var a = source.Pipe().Brightness(0.05f).ToImage(); using var b = a.Pipe().Contrast(1.1f).ToImage(); return b.Pipe(pool).Invert().ToImage(); }));
            rows.Add(Measure(source, "点算子 ×5", PointPlan(5), pool => source.Apply(PointPlan(5), pool),
                pool =>
                {
                    using var a = source.Pipe().Brightness(0.05f).ToImage();
                    using var b = a.Pipe().Contrast(1.1f).ToImage();
                    using var c = b.Pipe(pool).Invert().ToImage();
                    using var d = c.Pipe().Brightness(0.05f).ToImage();
                    return d.Pipe(pool).Contrast(1.1f).ToImage();
                }));

            foreach (var sigma in new[] { 1f, 3f, 8f })
            {
                var plan = BlurPlan(sigma);
                rows.Add(Measure(source, $"模糊 σ={sigma} + 2 点", plan, pool => source.Apply(plan, pool), pool =>
                {
                    using var a = source.Pipe().GaussianBlur(sigma).ToImage();
                    using var b = a.Pipe().Brightness(0.05f).ToImage();
                    return b.Pipe().Contrast(1.1f).ToImage();
                }));
            }

            // The point chains above are the easy case: one pass, no halo. These add a convolution, a 3x3 family, a
            // reversed order, and a deep chain, because a rule that only covers point steps would be a rule for the
            // benchmark rather than for the library.
            rows.Add(Measure(source, "点 ×2", new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.Contrast(1.1f)),
                pool => source.Apply(new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.Contrast(1.1f)), pool),
                pool => { using var a = source.Pipe().Brightness(0.05f).ToImage(); return a.Pipe(pool).Contrast(1.1f).ToImage(); }));

            rows.Add(Measure(source, "3×3 ×2", new ProcessingPlan().Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel()),
                pool => source.Apply(new ProcessingPlan().Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel()), pool),
                pool => { using var a = source.Pipe().Emboss(0.6f).ToImage(); return a.Pipe(pool).Sobel().ToImage(); }));

            rows.Add(Measure(source, "3×3 ×3",
                new ProcessingPlan().Add(PlanOps.Sharpen(0.6f)).Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel()),
                pool => source.Apply(new ProcessingPlan().Add(PlanOps.Sharpen(0.6f)).Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel()), pool),
                pool =>
                {
                    using var a = source.Pipe().Sharpen(0.6f).ToImage();
                    using var b = a.Pipe().Emboss(0.6f).ToImage();
                    return b.Pipe(pool).Sobel().ToImage();
                }));

            rows.Add(Measure(source, "点 + 模糊 σ=3", new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.GaussianBlur(3f)),
                pool => source.Apply(new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.GaussianBlur(3f)), pool),
                pool => { using var a = source.Pipe().Brightness(0.05f).ToImage(); return a.Pipe(pool).GaussianBlur(3f).ToImage(); }));

            rows.Add(Measure(source, "模糊 σ=3 + 3 点",
                new ProcessingPlan().Add(PlanOps.GaussianBlur(3f)).Add(PlanOps.Brightness(0.05f))
                    .Add(PlanOps.Contrast(1.1f)).Add(PlanOps.Invert()),
                pool => source.Apply(new ProcessingPlan().Add(PlanOps.GaussianBlur(3f)).Add(PlanOps.Brightness(0.05f))
                    .Add(PlanOps.Contrast(1.1f)).Add(PlanOps.Invert()), pool),
                pool =>
                {
                    using var a = source.Pipe().GaussianBlur(3f).ToImage();
                    using var b = a.Pipe().Brightness(0.05f).ToImage();
                    using var c = b.Pipe().Contrast(1.1f).ToImage();
                    return c.Pipe(pool).Invert().ToImage();
                }));

            report.Heading(3, $"{w}×{h}");
            report.Table(
                ["链条", "计划 ms", "逐次 ms", "计划 ÷ 逐次", "计划分配", "逐次分配", "Rent（计划 / 逐次）"],
                rows);
        }

        report.Line("> 判读：**计划分配到 KiB 级、逐次到 MB 级**就是融合成立。比值列若随尺寸下滑，说明每带的固定成本");
        report.Line("> 正被摊薄；稳态比值才是融合的时间代价，小图那一列不是。");
    }

    private static ProcessingPlan PointPlan(int steps)
    {
        var plan = new ProcessingPlan();
        for (var i = 0; i < steps; i++)
        {
            // Three different point operators, cycled, so a five-step chain is not one kernel repeated.
            plan = (i % 3) switch
            {
                0 => plan.Add($"step{i}", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.05f))),
                1 => plan.Add($"step{i}", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.1f))),
                _ => plan.Add($"step{i}", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v))),
            };
        }

        return plan;
    }

    private static ProcessingPlan BlurPlan(float sigma) => new ProcessingPlan()
        .Add("blur", OpCategory.Neighborhood,
            PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, sigma), 3, 3), 3, 3)
        .Add("brightness", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.05f)))
        .Add("contrast", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.1f)));

    private static string[] Measure(OurImage source, string name, ProcessingPlan plan,
        Func<IPixelPool?, OurImage> fused, Func<IPixelPool?, OurImage> separate)
    {
        // Both sides go through one Timing.Time call so their rounds alternate. Measured separately they were two
        // independent seven-round medians taken minutes apart, and on this machine the ratio moved by 3x between
        // runs of the mode — that was the instrument, not the code.
        using (var warm = fused(null))
        {
        }

        using (var warm = separate(null))
        {
        }

        var times = Timing.Time(
        [
            new Runner("fused", () => { using var r = fused(null); }),
            new Runner("separate", () => { using var r = separate(null); }),
        ]);

        var fusedSpy = new CountingPool();
        var separateSpy = new CountingPool();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        using (var result = fused(fusedSpy))
        {
        }

        var fusedAllocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        before = GC.GetTotalAllocatedBytes(precise: true);
        using (var result = separate(separateSpy))
        {
        }

        var separateAllocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        return
        [
            name,
            Timing.F(times["fused"]),
            Timing.F(times["separate"]),
            (times["fused"] / times["separate"]).ToString("F2", CultureInfo.InvariantCulture) + "×",
            KiB(fusedAllocated),
            KiB(separateAllocated),
            $"{fusedSpy.Rents} / {separateSpy.Rents}",
        ];
    }

    private static string KiB(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MiB"
            : (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KiB";

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
