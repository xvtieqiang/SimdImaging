using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Two band heights, interleaved in one process. Comparing separate builds cannot answer this: run-to-run drift on
/// this machine is larger than the effect, so the only honest instrument is alternating the two inside one loop and
/// taking medians.
/// </summary>
internal static class BandCost
{
    public static void Run(int width, int height, int rounds)
    {
        var defaultRows = Math.Clamp(ProcessingPlan.DefaultBandPixels / width, 8, 1024);
        var plans = new (string Name, ProcessingPlan Plan)[]
        {
            ($"128 行", Make(128)),
            ($"{defaultRows} 行（按像素）", Make(defaultRows)),
        };

        var data = Fixture.Make(width, height);
        using var image = Fixture.Ours(data, width, height);

        var samples = new Dictionary<string, List<double>>();
        foreach (var (name, _) in plans)
            samples[name] = [];

        for (var i = 0; i < rounds + 2; i++)
        {
            foreach (var (name, plan) in plans)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (image.ApplyWithOptions(plan, new ExecuteOptions { ForceFusion = true })) { }
                sw.Stop();
                if (i >= 2)
                    samples[name].Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        Console.WriteLine($"{width}x{height} · {rounds} 轮交错");
        foreach (var (name, list) in samples)
        {
            list.Sort();
            Console.WriteLine($"  {name,-22} 中位 {list[list.Count / 2]:F1} ms   " +
                $"最小 {list[0]:F1}  最大 {list[^1]:F1}");
        }
    }

    private static ProcessingPlan Make(int bandRows) => new ProcessingPlan { BandRows = bandRows }
        .Add(PlanOps.Sharpen(0.6f))
        .Add(PlanOps.Emboss(0.6f))
        .Add(PlanOps.Sobel());
}
