using System.Diagnostics;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Where the time goes in a point-operator chain: the same five steps run three ways — through a fused plan,
/// through a plan with fusion disabled, and as direct calls on the image. The plan is supposed to make chains
/// cheaper; if the direct calls win, the plan is the overhead and that is worth knowing before tuning anything.
/// </summary>
internal static class ChainAb
{
    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, $"L 组 · 点算子链的三条路径（{width}×{height}）");
        using var image = Fixture.Ours(Fixture.Make(width, height), width, height);
        var view = image[0].Pixels.View;

        var plan = new ProcessingPlan()
            .Add("invert", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)))
            .Add("contrast1", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 0.9f)))
            .Add("sepia", OpCategory.Point, PlanSteps.Point(v => PointOps.Sepia(v, 1f)))
            .Add("contrast2", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.1f)))
            .Add("grayscale", OpCategory.Point, PlanSteps.Point(v => PointOps.Grayscale(v)));

        var fused = Time(() =>
        {
            using var buffer = plan.Execute(view);
        });

        var stepped = Time(() =>
        {
            using var buffer = plan.ExecuteWithOptions(view, new ExecuteOptions { AllowFusion = false });
        });

        var collapsed = Time(() =>
        {
            using var buffer = FilterOps.PointChain(view,
            [
                PointOps.InvertRow(),
                PointOps.ContrastRow(0.9f),
                PointOps.BrightnessRow(0.02f),
                PointOps.ContrastRow(1.1f),
                PointOps.InvertRow(),
            ]);
        });

        var direct = Time(() =>
        {
            using var result = image.Pipe().Invert().Contrast(0.9f).Sepia().Contrast(1.1f).Grayscale().ToImage();
        });

        report.Table(["路径", "中位 ms", "相对直调"], [
            ["融合计划", fused.ToString("F1"), $"{fused / direct:F2}×"],
            ["不融合计划", stepped.ToString("F1"), $"{stepped / direct:F2}×"],
            ["塌缩表（5 步 → 1 遍）", collapsed.ToString("F1"), $"{collapsed / direct:F2}×"],
            ["直调（Clone + 五个门面）", direct.ToString("F1"), "1.00×"],
        ]);

        report.Line("> 若 `不融合` 明显慢于 `直调`，说明每步一张中间图的代价是主因；");
        report.Line("> 若两者接近而都慢于直调，说明瓶颈在单步内核或计划的分带调度，而不在缓冲数量。");
    }

    private static double Time(Action run)
    {
        run();

        var samples = new double[5];
        for (var i = 0; i < samples.Length; i++)
        {
            var watch = Stopwatch.StartNew();
            run();
            watch.Stop();
            samples[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return samples[2];
    }
}
