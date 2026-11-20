using System.Diagnostics;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Where the stepwise plan's overhead comes from: the same chain through <c>Pipe()</c>, through a plan with the
/// default band height, and through a plan with one band. If the one-band plan matches <c>Pipe()</c>, the cost is
/// per-band and not per-step.
/// </summary>
internal static class PlanOverhead
{
    public static void Run(int width, int height, int rounds)
    {
        var data = Fixture.Make(width, height);
        using var image = Fixture.Ours(data, width, height);
        var view = image[0].Pixels.View;

        var fused = new ProcessingPlan().Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel());
        var bands = new (string Name, int Rows)[] { ("默认带高", 0), ("单带", height), ("16 带", Math.Max(1, (height + 15) / 16)) };

        foreach (var (name, rows) in bands)
        {
            var plan = rows > 0
                ? new ProcessingPlan { BandRows = rows }.Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel())
                : new ProcessingPlan().Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel());

            var times = new List<double>();
            for (var i = 0; i < rounds + 2; i++)
            {
                var clock = Stopwatch.StartNew();
                using (plan.Execute(view)) { }
                clock.Stop();
                if (i >= 2) times.Add(clock.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"  计划 {name,-8} 中位 {times[times.Count / 2],7:F2} ms   最小 {times[0],7:F2}");
        }

        var direct = new List<double>();
        for (var i = 0; i < rounds + 2; i++)
        {
            var clock = Stopwatch.StartNew();
            using (var a = image.Pipe().Emboss(0.6f).ToImage())
            using (var b = a.Pipe().Sobel().ToImage()) { }
            clock.Stop();
            if (i >= 2) direct.Add(clock.Elapsed.TotalMilliseconds);
        }
        direct.Sort();
        Console.WriteLine($"  Pipe 直写链      中位 {direct[direct.Count / 2],7:F2} ms   最小 {direct[0],7:F2}");
    }
}
