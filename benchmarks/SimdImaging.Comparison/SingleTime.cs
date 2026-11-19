using System.Diagnostics;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Comparison;

/// <summary>
/// One operator through the plan path, timed alone. A single-step plan takes the operator's whole-image form, so
/// this is the number a caller sees from <c>Pipe().Op().ToImage()</c> — and it is where an operator that never
/// learned to parallelise shows up, because no band worker can help it.
/// </summary>
internal static class SingleTime
{
    public static void Run(int width, int height, int rounds)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("SIMDIMAGING_DEGREE"), out var degree) && degree > 0)
            RowParallel.DegreeOverride = degree;

        var data = Fixture.Make(width, height);
        using var image = Fixture.Ours(data, width, height);

        foreach (var (name, run) in Cases(image))
        {
            var times = new List<double>();
            for (var i = 0; i < rounds + 2; i++)
            {
                var clock = Stopwatch.StartNew();
                using (run()) { }
                clock.Stop();
                if (i >= 2)
                    times.Add(clock.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            Console.WriteLine($"{name,-12} 中位 {times[times.Count / 2]:F2} ms   最小 {times[0]:F2}");
        }
    }

    /// <summary>The operator through <c>Pipe()</c>, which is the path a caller actually writes.</summary>
    private static IEnumerable<(string Name, Func<SimdImaging.Image> Run)> Cases(SimdImaging.Image image)
    {
        yield return ("单步 Sharpen", () => image.Pipe().Sharpen(0.6f).ToImage());
        yield return ("单步 Emboss", () => image.Pipe().Emboss(0.6f).ToImage());
        yield return ("单步 Sobel", () => image.Pipe().Sobel().ToImage());
        yield return ("单步 Median", () => image.Pipe().Median3x3().ToImage());
        yield return ("单步 Erode", () => image.Pipe().Erode3x3().ToImage());
        yield return ("单步 模糊 σ=3", () => image.Pipe().GaussianBlur(3f).ToImage());
        yield return ("单步 点", () => image.Pipe().Brightness(0.05f).ToImage());
    }
}
