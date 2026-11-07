using System.Diagnostics;
using System.Runtime.InteropServices;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

// End-to-end timings for the processing path. Deliberately a stopwatch harness: the question it answers
// is "did the whole operation move", not "is this kernel fast in isolation".
// Usage: dotnet run -c Release --project benchmarks/SimdImaging.Benchmarks [iterations]

var iterations = args.Length > 0 ? int.Parse(args[0]) : 3;
const int Width = 3840;
const int Height = 2160;

using var source = Image.Create(Width, Height, WorkingFormat.Rgba8);
var pixels = source.WritablePixels;
for (var i = 0; i < pixels.Length; i++)
    pixels[i] = (byte)((i * 37 + 11) & 0xFF);

using var overlay = Image.Create(1920, 1080, WorkingFormat.Rgba8);
overlay.WritablePixels.Fill(128);

Console.WriteLine($"machine: {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"tier: avx512={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated} " +
                  $"avx2={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} " +
                  $"v128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}");
Console.WriteLine($"source: {Width}x{Height} Rgba8 = {Width * Height / 1_000_000.0:F1} MPix");
Console.WriteLine();

Measure("resize 4K->1080p bicubic", () =>
{
    using var result = source.Resize(1920, 1080, Resampler.Bicubic);
    return result.Width * result.Height;
});

Measure("resize 4K->1080p lanczos3", () =>
{
    using var result = source.Resize(1920, 1080, Resampler.Lanczos3);
    return result.Width * result.Height;
});

Measure("crop 1000x1000", () =>
{
    using var result = source.Crop(new Sdcb.SimdCodecs.Abstractions.PixelRect(100, 100, 1000, 1000));
    return result.Width * result.Height;
});

Measure("quantize 4K -> 64 colours", () =>
{
    using var result = source.Quantize(64);
    return result.Width * result.Height;
});

Measure("quantize 4K -> 64 (wu)", () =>
{
    using var result = source.Quantize(64, SimdImaging.Processing.Quantizer.Wu);
    return result.Width * result.Height;
});

Measure("quantize 4K -> 64 (octree)", () =>
{
    using var result = source.Quantize(64, SimdImaging.Processing.Quantizer.Octree);
    return result.Width * result.Height;
});

Measure("reduce colors 4K -> 64 dithered", () =>
{
    using var result = source.ReduceColors(64, SimdImaging.Processing.Quantizer.Octree);
    return result.Width * result.Height;
});

using var canvas = source.Clone();
var ellipse = new PixelPathBuilder().AddEllipse(Width / 2f, Height / 2f, Width * 0.45f, Height * 0.45f).Build();
Measure("fill 4K ellipse path (aa)", () =>
{
    canvas.FillPath(ellipse, new RgbaColor(200, 40, 40, 255));
    return canvas.Width * canvas.Height;
});

Measure("stroke 4K ellipse path (aa, w=6)", () =>
{
    canvas.DrawPath(ellipse, new RgbaColor(40, 200, 120, 255), new StrokeOptions(width: 6f));
    return canvas.Width * canvas.Height;
});

Measure("clip 4K ellipse path (aa)", () =>
{
    canvas.ClipPath(ellipse);
    return canvas.Width * canvas.Height;
});

Measure("oil paint 4K r=2", () =>
{
    using var result = source.OilPaint(2, 20);
    return result.Width * result.Height;
});

Measure("guided filter 4K r=4", () =>
{
    using var result = source.GuidedFilter(4, 1e-4f);
    return result.Width * result.Height;
});

Measure("gaussian blur 4K sigma=3", () =>
{
    using var result = source.GaussianBlur(3f);
    return result.Width * result.Height;
});

Measure("chroma 4:2:0 4K", () =>
{
    using var result = source.ChromaResample(SimdImaging.Processing.ChromaSubsampling.Yuv420);
    return result.Width * result.Height;
});

Measure("bilateral 4K r=2", () =>
{
    using var result = source.Bilateral(2, 0f, 0.1f);
    return result.Width * result.Height;
});

Measure("composite 1080p overlay", () =>
{
    using var result = source.Composite(overlay, 200, 200);
    return result.Width * result.Height;
});

void Measure(string name, Func<long> body)
{
    body();                                        // warm-up: JIT the path before timing it
    var best = double.MaxValue;
    long outputPixels = 0;
    long allocated = 0;
    for (var i = 0; i < iterations; i++)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        outputPixels = body();
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
    }

    var megapixelsPerSecond = outputPixels / 1_000_000.0 / (best / 1000.0);
    Console.WriteLine($"{name,-30} {best,8:F1} ms   {megapixelsPerSecond,7:F1} MPix/s   alloc {allocated / 1024.0,8:F0} KiB");
}
