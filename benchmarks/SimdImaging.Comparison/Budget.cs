using System.Globalization;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>
/// Attributes the resize run-to-run spread. Every budget is measured in the SAME process, against the
/// same oracle, three independent times — cross-session numbers are not a valid baseline here.
/// Output bytes are compared against the 16 MiB baseline for every budget: a different band boundary
/// is only acceptable if it produces identical pixels.
/// </summary>
internal static class Budget
{
    private static readonly long[] Budgets = [16L << 20, 8L << 20, 4L << 20, 2L << 20, 1L << 20];

    public static void Write(Report report, int width, int height)
    {
        var originalBudget = Resample.ScratchBudgetBytes;
        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);
        var resizeCases = Cases.Build(width, height).Where(c => c.Id.StartsWith("resize")).ToArray();

        // Baseline pixels for the byte-identity check.
        FloatScratch.Clear();
        Resample.ScratchBudgetBytes = Budgets[0];
        var baseline = resizeCases.Select(c =>
        {
            using var result = c.Ours(ours);
            return Fixture.Bytes(result);
        }).ToArray();

        report.Heading(2, "resize 行带预算诊断（同进程、多轮、对手同场）");
        report.Line($"> 输入 {width}×{height}；预算 = 水平 pass 的 float scratch 上限。每个预算跑 3 次独立的 `Timing.Time`（每次 ≥300 ms 热身 + 5 轮交错采样）。");
        report.Line("> **判定**：数字必须稳定且变快才落地；同时逐个预算比对输出字节，不一致的预算直接淘汰。");
        report.Line();

        var rows = new List<string[]>();
        foreach (var budget in Budgets)
        {
            FloatScratch.Clear();
            Resample.ScratchBudgetBytes = budget;

            for (var caseIndex = 0; caseIndex < resizeCases.Length; caseIndex++)
            {
                var c = resizeCases[caseIndex];
                var runners = new[]
                {
                    new Runner("ours", () => { using var r = c.Ours(ours); GC.KeepAlive(r); }),
                    new Runner("theirs", () => { using var r = c.Theirs(theirs); GC.KeepAlive(r); }),
                };

                var ourSamples = new List<double>();
                var theirSamples = new List<double>();
                long alloc = 0;
                for (var repeat = 0; repeat < 3; repeat++)
                {
                    var allocated = new Dictionary<string, long>();
                    var times = Timing.Time(runners, rounds: 5, allocated: allocated);
                    ourSamples.Add(times["ours"]);
                    theirSamples.Add(times["theirs"]);
                    allocated.TryGetValue("ours", out var a);
                    alloc = Math.Max(alloc, a);
                }

                using var check = c.Ours(ours);
                var identical = Fixture.Bytes(check).AsSpan().SequenceEqual(baseline[caseIndex]);

                var ourMedian = Median(ourSamples);
                var theirMedian = Median(theirSamples);
                var spread = ourSamples.Max() / ourSamples.Min();
                rows.Add([
                    (budget / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB",
                    c.Id,
                    string.Join(" / ", ourSamples.Select(Timing.F)),
                    Timing.F(ourMedian),
                    Timing.F(theirMedian),
                    Timing.Ratio(ourMedian, theirMedian),
                    "×" + spread.ToString("F2", CultureInfo.InvariantCulture),
                    (alloc / 1024.0).ToString("F0", CultureInfo.InvariantCulture),
                    identical ? "逐字节一致" : "❌ 不一致",
                ]);
            }
        }

        // Always leave the library in its shipped configuration.
        Resample.ScratchBudgetBytes = originalBudget;
        FloatScratch.Clear();

        report.Table(["预算", "场景", "我方 3 次 ms", "我方中位", "对手中位", "加速比", "波动(max/min)", "KiB/op", "字节一致"], rows);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }
}
