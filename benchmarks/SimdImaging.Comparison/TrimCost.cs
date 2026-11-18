using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// What the per-thread scratch cache actually buys: the same chain run repeatedly with the caches kept and with them
/// dropped between runs. <c>FloatScratch</c> keeps up to 64 MiB per thread, which at 8K on sixteen cores is a
/// resident floor close to a gigabyte — worth paying only if re-allocating each time is measurably slower.
/// </summary>
internal static class TrimCost
{
    public static void Run()
    {
        foreach (var (w, h) in new[] { (1920, 1080), (7680, 4320) })
        {
            var bandRows = int.TryParse(Environment.GetEnvironmentVariable("SIMDIMAGING_PLAN_BAND_ROWS"), out var br) && br > 0
                ? br
                : 0;
            var plan = new ProcessingPlan();
            if (bandRows > 0)
                plan = new ProcessingPlan { BandRows = bandRows };
            plan.Add(PlanOps.Sharpen(0.6f)).Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel());

            var data = Fixture.Make(w, h);
            using var image = Fixture.Ours(data, w, h);

            double Run(bool trim)
            {
                for (var i = 0; i < 3; i++) using (image.ApplyWithOptions(plan, new ExecuteOptions { ForceFusion = true })) { }
                var times = new List<double>();
                for (var i = 0; i < 7; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using (image.ApplyWithOptions(plan, new ExecuteOptions { ForceFusion = true })) { }
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    if (trim) MemoryBudget.Trim();
                }

                times.Sort();
                return times[times.Count / 2];
            }

            var kept = Run(false);
            var dropped = Run(true);
            Console.WriteLine($"{w}x{h}: 保留 {kept:F1} ms · 清缓存 {dropped:F1} ms · 代价 {(dropped / kept - 1) * 100:F1}%");
        }
    }
}
