using System.Diagnostics;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

// Table building is 1024 transform invocations: cheap next to a frame, not cheap next to a thumbnail. Reusing a
// pre-built table is the difference between paying that per call and paying it once.
var chain = new[]
{
    PointOps.ContrastRow(1.2f),
    PointOps.InvertRow(),
    PointOps.BrightnessRow(0.02f),
    PointOps.ContrastRow(1.1f),
    PointOps.InvertRow(),
};

var table = PointChainTable.Create(chain);

foreach (var size in new[] { 16, 256, 1920 })
{
    using var image = Image.Create(size, size, WorkingFormat.Rgba8);

    var perCall = Best(image, () => image.PointChain(chain));
    var reused = Best(image, () => image.PointChain(table));

    Console.WriteLine($"{size,5}x{size,-5} per-call {perCall:F3} ms   reused {reused:F3} ms   x{perCall / reused:F2}");
}

static double Best(Image image, Func<Image> run)
{
    for (var i = 0; i < 3; i++)
    {
        using var warm = run();
    }

    var best = double.MaxValue;
    for (var i = 0; i < 7; i++)
    {
        var watch = Stopwatch.StartNew();
        using var result = run();
        watch.Stop();
        best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
    }

    return best;
}
