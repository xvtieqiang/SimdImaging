using System.Diagnostics;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Is a one-step plan worth a fast path? Compares the operator alone, the facade's single-step call, and a
/// one-step plan, for a cheap operator (where overhead dominates) and an expensive one (where it does not).
/// </summary>
internal static class OneStepCost
{
    public static void Run(int width, int height, int rounds)
    {
        var data = Fixture.Make(width, height);
        using var image = Fixture.Ours(data, width, height);
        var view = image[0].Pixels.View;

        var cases = new (string Name, Func<SimdImaging.Image> Facade, Func<WorkingBuffer> Operator, ImageOp Op)[]
        {
            ("brightness", () => image.Pipe().Brightness(0.05f).ToImage(),
                () => PointOps.Brightness(view, 0.05f), PlanOps.Brightness(0.05f)),
            ("gaussian σ=2", () => image.Pipe().GaussianBlur(2f).ToImage(),
                () => FilterOps.GaussianBlur(view, 2f, null), PlanOps.GaussianBlur(2f)),
            ("median3x3", () => image.Pipe().Median3x3().ToImage(),
                () => FilterOps.Median3x3(view, null), PlanOps.Median3x3()),
            ("gaussian σ=1", () => image.Pipe().GaussianBlur(1f).ToImage(),
                () => FilterOps.GaussianBlur(view, 1f, null), PlanOps.GaussianBlur(1f)),
            ("sharpen 0.6/1", () => image.Pipe().Sharpen(0.6f, 1f).ToImage(),
                () => FilterOps.Sharpen(view, 0.6f, 1f, null), PlanOps.Sharpen(0.6f, 1f)),
        };

        foreach (var (name, facade, op, planOp) in cases)
        {
            var plan = new ProcessingPlan().Add(planOp);
            var rows = new (string Path, Func<IDisposable> Run)[]
            {
                ("算子直调", () => op()),
                ("Pipe()", () => facade()),
                ("单步计划", () => plan.Execute(view)),
            };

            Console.WriteLine(name);
            var baseline = 0.0;
            foreach (var (path, run) in rows)
            {
                var times = new List<double>();
                for (var i = 0; i < rounds + 3; i++)
                {
                    var clock = Stopwatch.StartNew();
                    using (run()) { }
                    clock.Stop();
                    if (i >= 3) times.Add(clock.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                var median = times[times.Count / 2];
                if (baseline == 0) baseline = median;
                Console.WriteLine($"  {path,-8} 中位 {median,7:F3} ms   相对直调 {median / baseline:F2}×");
            }
        }
    }
}
