using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SixLabors.ImageSharp.Processing;
using TheirImage = SixLabors.ImageSharp.Image;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// Pipeline scenarios, which the twelve single-operator cases deliberately avoided.
/// <para>The structural difference is real but easy to overstate: <c>ProcessingPlan</c> runs every same-size
/// step inside one row-band pass, so a three-step chain never materialises an intermediate image. The oracle
/// also mutates in place through its own processing context, so the honest question is not "do we fuse and
/// they don't" but "does our fusion actually beat their in-place chain". Only a measurement answers that.</para>
/// </summary>
internal static class PerfPipeline
{
    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, "E 组 · 流水线端到端 A/B（`ProcessingPlan` 对对手的处理链）");
        report.Line($"> 输入 {width}×{height} `Rgba8`。两边都从共享源产出新图；我方用 `ProcessingPlan` 单次行带执行，");
        report.Line("> 对手用 `Clone(ctx => ctx.A()...)` 的原地处理链。加速比 = 对手 ÷ 我方，>1 表示我方更快。");
        report.Line();

        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);

        var cases = new (string Name, string Chain, ProcessingPlan Plan,
            Func<TheirImage, TheirImage> Oracle)[]
        {
            ("point-3", "Contrast 1.2 → Sepia → Grayscale (BT.601)",
                new ProcessingPlan()
                    .Add("contrast", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.2f)))
                    .Add("sepia", OpCategory.Point, PlanSteps.Point(v => PointOps.Sepia(v, 1f)))
                    .Add("grayscale", OpCategory.Point, PlanSteps.Point(v => PointOps.Grayscale(v))),
                i => i.Clone(x => x.Contrast(1.2f).Sepia().Grayscale(GrayscaleMode.Bt601))),

            ("point-5", "Invert → Contrast 0.9 → Sepia → Contrast 1.1 → Grayscale",
                new ProcessingPlan()
                    .Add("invert", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)))
                    .Add("contrast1", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 0.9f)))
                    .Add("sepia", OpCategory.Point, PlanSteps.Point(v => PointOps.Sepia(v, 1f)))
                    .Add("contrast2", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.1f)))
                    .Add("grayscale", OpCategory.Point, PlanSteps.Point(v => PointOps.Grayscale(v))),
                i => i.Clone(x => x.Invert().Contrast(0.9f).Sepia().Contrast(1.1f)
                    .Grayscale(GrayscaleMode.Bt601))),

            ("blur-point", "Gaussian σ3 → Sepia → Grayscale",
                new ProcessingPlan()
                    .Add("blur", OpCategory.Neighborhood,
                        PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 3f), 9, 9), 9, 9)
                    .Add("sepia", OpCategory.Point, PlanSteps.Point(v => PointOps.Sepia(v, 1f)))
                    .Add("grayscale", OpCategory.Point, PlanSteps.Point(v => PointOps.Grayscale(v))),
                i => i.Clone(x => x.GaussianBlur(3f).Sepia().Grayscale(GrayscaleMode.Bt601))),
        };

        var rows = new List<string[]>();
        double logSum = 0;
        var wins = 0;

        foreach (var testCase in cases)
        {
            var runners = new[]
            {
                new Runner("ours", () => { using var r = ours.Apply(testCase.Plan); GC.KeepAlive(r); }),
                new Runner("theirs", () => { using var r = testCase.Oracle(theirs); GC.KeepAlive(r); }),
            };

            var allocated = new Dictionary<string, long>();
            var times = Timing.Time(runners, rounds: 7, allocated: allocated);
            var ratio = times["theirs"] / times["ours"];
            logSum += Math.Log(ratio);
            if (ratio > 1) wins++;

            rows.Add([testCase.Name, testCase.Chain, testCase.Plan.Steps.Count.ToString(CultureInfo.InvariantCulture),
                Timing.F(times["ours"]), Timing.F(times["theirs"]),
                ratio.ToString("F2", CultureInfo.InvariantCulture) + "×",
                (allocated["ours"] / 1024).ToString(CultureInfo.InvariantCulture) + " KiB",
                (allocated["theirs"] / 1024).ToString(CultureInfo.InvariantCulture) + " KiB",
                ratio > 1 ? "我方快" : "对手快"]);
        }

        report.Table(["场景", "链路", "步数", "我方 ms", "对手 ms", "加速比", "我方分配/op", "对手分配/op", "胜负"], rows);
        report.Line($"**流水线超越判定：{(Math.Exp(logSum / cases.Length)).ToString("F4", CultureInfo.InvariantCulture)}×**（{wins} / {cases.Length} 项领先）。");
        report.Line("> 分配为 `GC.GetTotalAllocatedBytes`（进程级），口径与单算子表一致。");
        report.Line();
    }
}
