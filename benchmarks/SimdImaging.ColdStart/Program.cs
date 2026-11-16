using System.Diagnostics;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

// Where the time goes in a small nearest resize: the operator, versus the same allocation plus a bare 32-bit copy.
// If the copy alone costs about what Resize costs, the operator is not the problem.
using var source = Image.Create(640, 360, WorkingFormat.Rgba8);
var span = source.WritablePixels;
for (var i = 0; i < span.Length; i++)
    span[i] = (byte)(i * 7);

var resize = Best(() => source.Resize(320, 180, ResizeMode.Stretch, resampler: Resampler.Nearest));
var copy = Best(() =>
{
    var destination = Image.Create(320, 180, WorkingFormat.Rgba8);
    var pixels = destination.WritablePixels;
    // Row-wise: 180 copies of 1280 bytes. A per-pixel slice loop measured 1.4 ms and told us nothing.
    for (var y = 0; y < 180; y++)
    {
        source.WritablePixels.Slice(y * 2 * 640 * 4, 320 * 4)
            .CopyTo(pixels.Slice(y * 320 * 4, 320 * 4));
    }

    return destination;
});

Console.WriteLine($"resize-nearest 640->320: {resize:F3} ms");
var stats = MemoryBudget.Snapshot().WeightCache;
Console.WriteLine($"weight cache: hits {stats.Hits} misses {stats.Misses} entries {stats.Entries}");
Console.WriteLine($"create + manual copy    : {copy:F3} ms");

static double Best(Func<Image> run)
{
    for (var i = 0; i < 5; i++)
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
