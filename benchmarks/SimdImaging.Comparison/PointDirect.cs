using System.Diagnostics;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// What the generic point adapter costs: it asks the operator for a band buffer and then copies that buffer into the
/// destination, so every point step pays one full-frame copy. The byte-domain table kernel writes the destination
/// directly, and the two do the same arithmetic (a 4 × 256 table), so the difference is the copy and nothing else.
/// </summary>
/// <remarks>
/// This is the evidence for or against a direct-write point adapter. Until it exists, the number is what decides.
/// </remarks>
internal static class PointDirect
{
    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, $"点步骤：直写 vs 拷贝（{width}×{height}）");
        report.Line("> 两条路算术相同（4 × 256 表），差别只有 `PlanSteps.Point` 里的 `CopyRows` 一次整帧拷贝。");
        report.Line("> best of 9, 3 次预热。");
        report.Line();

        var data = Fixture.Make(width, height);
        using var source = Fixture.Ours(data, width, height);

        var table = new byte[4 * 256];
        for (var i = 0; i < 256; i++)
        {
            for (var c = 0; c < 4; c++)
                table[(c * 256) + i] = (byte)(255 - i);
        }

        var generic = new ProcessingPlan().Add(PlanOps.Invert());
        var direct = new ProcessingPlan().AddPointLut("invert-lut", table);

        var genericMs = Best(() => source.Apply(generic));
        var directMs = Best(() => source.Apply(direct));
        var saved = genericMs - directMs;

        report.Table(
            ["路径", "耗时 ms", "相对直写"],
            [
                ["通用适配器（含拷贝）", F(genericMs), F(genericMs / directMs) + "×"],
                ["表核直写", F(directMs), "1.00×"],
                ["差值", F(saved), (100 * saved / genericMs).ToString("F0", CultureInfo.InvariantCulture) + "%"],
            ]);

        report.Line();
        report.Line($"一步点算子在 {width}×{height} 上，{F(saved)} ms 花在那次拷贝上——"
                  + "占单步耗时的 " + (100 * saved / genericMs).ToString("F0", CultureInfo.InvariantCulture) + "%。");
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static double Best(Func<OurImage> run)
    {
        for (var i = 0; i < 3; i++)
        {
            using var warm = run();
        }

        var best = double.MaxValue;
        for (var i = 0; i < 9; i++)
        {
            var watch = Stopwatch.StartNew();
            using var result = run();
            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        return best;
    }
}
