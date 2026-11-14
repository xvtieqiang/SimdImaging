using System.Diagnostics;
using System.Runtime.InteropServices;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Benchmarks;
using SimdImaging.Processing;

// End-to-end timings for the processing path. Deliberately a stopwatch harness: the question it answers
// is "did the whole operation move", not "is this kernel fast in isolation".
// Usage: dotnet run -c Release --project benchmarks/SimdImaging.Benchmarks [iterations]

var iterations = 3;
var includeSlow = false;
string? jsonPath = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "slow": includeSlow = true; break;
        case "--json" when i + 1 < args.Length: jsonPath = args[++i]; break;
        default: iterations = int.Parse(args[i]); break;
    }
}
var results = new List<Result>();

const int Width = 3840;
const int Height = 2160;

using var source = Image.Create(Width, Height, WorkingFormat.Rgba8);
var pixels = source.WritablePixels;
for (var i = 0; i < pixels.Length; i++)
    pixels[i] = (byte)((i * 37 + 11) & 0xFF);

// 1080p twin for the seconds-class operators (non-local means): 4K would turn one sample into a minute.
using var half = Image.Create(1920, 1080, WorkingFormat.Rgba8);
var halfPixels = half.WritablePixels;
for (var i = 0; i < halfPixels.Length; i++)
    halfPixels[i] = (byte)((i * 37 + 11) & 0xFF);

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

// Cross-guided: four box passes per channel instead of two plus the cross product, so the price of an
// external guide is visible next to the line above. The guide is a blurred copy, i.e. a realistic one.
using var guideImage = source.GaussianBlur(3f);
Measure("guided.cross 4K r=4", () =>
{
    using var result = source.GuidedFilter(guideImage, 4, 1e-4f);
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

// Distance transform: a 1080p mask of 8-pixel squares, so the field holds real distances (up to 4 px)
// instead of the all-foreground field a pseudo-random alpha would produce. The operator reads alpha only.
using var mask = Image.Create(1920, 1080, WorkingFormat.Rgba8);
var maskPixels = mask.WritablePixels;
for (var y = 0; y < 1080; y++)
    for (var x = 0; x < 1920; x++)
        maskPixels[(y * 1920 + x) * 4 + 3] = (((x / 8) + (y / 8)) & 1) == 0 ? (byte)255 : (byte)0;

Measure("dt 1080p 8px checker", () =>
{
    using var result = mask.DistanceTransform();
    return result.Width * result.Height;
});

// Seconds-class operator: 1080p by default, 4K only with the `slow` argument. Two points on the cost
// curve: the default radii (5.8 s measured) and the 21×21 / 5×5 quality setting (16.1 s measured).
Measure("nlm 1080p S=3 P=1 h=0.1", () =>
{
    using var result = half.NonLocalMeans(3, 1, 0.1f);
    return result.Width * result.Height;
});

Measure("nlm 1080p S=5 P=2 h=0.1", () =>
{
    using var result = half.NonLocalMeans(5, 2, 0.1f);
    return result.Width * result.Height;
});

Measure("ll 1080p L=5 S=32", () =>
{
    using var result = half.LocalLaplacian();
    return result.Width * result.Height;
});

// Deconvolution: ten iterations, so the reported time is for the whole call, not one pass.
Measure("rl 1080p sigma=1.5 it=10", () =>
{
    using var result = half.RichardsonLucy(1.5f, 10);
    return result.Width * result.Height;
});

Measure("rl.motion 1080p len=15 it=10", () =>
{
    using var result = half.RichardsonLucyMotion(15, 25f, 10);
    return result.Width * result.Height;
});

// One energy + programme pass per seam; 50 columns is enough to read the per-seam cost off the total.
Measure("seam 1080p remove 50 columns", () =>
{
    using var result = half.RemoveSeams(SeamAxis.Vertical, 50);
    return result.Width * result.Height;
});

// ICC: the matrix/TRC fast path against the LUT path at a 33-point grid, which is what a real profile
// uses. Both transform the same 1080p image, so the difference is the CLUT evaluation itself.
var lutProfile = BuildLut16Profile(33);
Measure("icc 1080p lut16 grid=33", () =>
{
    using var result = half.IccTransform(lutProfile);
    return result.Width * result.Height;
});

// Same pipeline with a two-point grid: 24 samples, so the cube is in L1. The gap to grid=33 is the
// price of the gather, not of the arithmetic — which is what decides whether a vector body can help.
var tinyLut = BuildLut16Profile(2);
Measure("icc 1080p lut16 grid=2", () =>
{
    using var result = half.IccTransform(tinyLut);
    return result.Width * result.Height;
});

Measure("icc 1080p matrix/trc", () =>
{
    using var result = half.IccTransform(Sdcb.SimdCodecs.Color.Icc.IccProfiles.SRgbV4.Span);
    return result.Width * result.Height;
});

// Gain maps: reconstruction (a quarter-size map upsampled onto 1080p) and the paired geometry path.
using var gainMap = Image.Create(480, 270, WorkingFormat.Rgba8);
var gainPixels = gainMap.WritablePixels;
for (var i = 0; i < gainPixels.Length; i++)
    gainPixels[i] = (byte)((i * 53 + 7) & 0xFF);

Measure("gainmap.reconstruct 1080p", () =>
{
    using var result = HdrOps.ReconstructGainMap(half[0].Pixels.View, gainMap[0].Pixels.View,
        GainMapParameters.Default, 4f);
    return result.Width * result.Height;
});

using var pair = new GainMappedImage(Image.Create(1920, 1080, WorkingFormat.Rgba8),
    Image.Create(480, 270, WorkingFormat.Rgba8));
Measure("gainmap resize 1080p->720p", () =>
{
    using var result = pair.Resize(1280, 720);
    return result.Base.Width * result.Base.Height;
});

// Memory budget: what forty distinct resize sizes cost the caches, before and after a trim. The point of
// the byte budget is that this number stops growing; the point of the LRU is that it stops rebuilding.
for (var i = 0; i < 40; i++)
{
    using var resized = half.Resize(480 + i * 7, 270 + i * 3, Resampler.Bicubic);
}

var distinct = MemoryBudget.Snapshot();

// A realistic workload repeats a handful of sizes; that is what the LRU is for.
for (var round = 0; round < 4; round++)
{
    for (var i = 0; i < 12; i++)
    {
        using var resized = half.Resize(320 + i * 11, 180 + i * 7, Resampler.Bicubic);
    }
}

var warm = MemoryBudget.Snapshot();
MemoryBudget.Trim();
var trimmed = MemoryBudget.Snapshot();

Console.WriteLine($"memory: 40 distinct sizes -> entries={distinct.WeightCache.Entries} " +
                  $"bytes={distinct.WeightCache.Bytes} hit-rate={distinct.WeightCache.HitRate:P1} " +
                  $"evictions={distinct.WeightCache.Evictions}");
Console.WriteLine($"memory: weight-cache entries={warm.WeightCache.Entries} bytes={warm.WeightCache.Bytes} " +
                  $"hit-rate={warm.WeightCache.HitRate:P1} evictions={warm.WeightCache.Evictions}; " +
                  $"scratch retained={warm.Scratch.RetainedBytes} buffers={warm.Scratch.Buffers} " +
                  $"hit-rate={warm.Scratch.HitRate:P1}");
Console.WriteLine($"memory: after Trim entries={trimmed.WeightCache.Entries} bytes={trimmed.WeightCache.Bytes} " +
                  $"scratch retained={trimmed.Scratch.RetainedBytes}");
Console.WriteLine();

if (includeSlow)
{
    Measure("nlm 4K S=5 P=2 h=0.1", () =>
    {
        using var result = source.NonLocalMeans(5, 2, 0.1f);
        return result.Width * result.Height;
    });

    Measure("seam 4K remove 50 columns", () =>
    {
        using var result = source.RemoveSeams(SeamAxis.Vertical, 50);
        return result.Width * result.Height;
    });
}

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
    results.Add(new Result(name, Math.Round(best, 3), Math.Round(megapixelsPerSecond, 2), allocated));
    Console.WriteLine($"{name,-30} {best,8:F1} ms   {megapixelsPerSecond,7:F1} MPix/s   alloc {allocated / 1024.0,8:F0} KiB");
}

if (jsonPath is not null)
    BaselineJson.Write(jsonPath, results, iterations, includeSlow);

// A minimal lut16 profile with an identity A2B0 CLUT, built here rather than shipped as a fixture:
// the benchmark needs a realistic grid size, not a file.
static byte[] BuildLut16Profile(byte grid)
{
    const int headerSize = 128 + 16;   // header + one tag-table entry
    var samples = grid * grid * grid * 3;
    var tagSize = 48 + 3 * 256 * 2 + samples * 2 + 3 * 256 * 2;
    var profile = new byte[headerSize + tagSize];

    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(0, 4), profile.Length);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8, 4), 0x02300000);
    "scnr"u8.CopyTo(profile.AsSpan(12));
    "RGB "u8.CopyTo(profile.AsSpan(16));
    "XYZ "u8.CopyTo(profile.AsSpan(20));
    "acsp"u8.CopyTo(profile.AsSpan(36));
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(128, 4), 1);
    "A2B0"u8.CopyTo(profile.AsSpan(132));
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(136, 4), headerSize);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(140, 4), tagSize);

    var tag = profile.AsSpan(headerSize);
    "mft2"u8.CopyTo(tag);
    tag[8] = 3;
    tag[9] = 3;
    tag[10] = grid;
    for (var i = 0; i < 9; i++)
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(tag.Slice(12 + i * 4, 4), i % 4 == 0 ? 65536 : 0);

    var offset = 48;
    for (var channel = 0; channel < 3; channel++)
        for (var i = 0; i < 256; i++, offset += 2)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(i * 257));

    for (var r = 0; r < grid; r++)
        for (var g = 0; g < grid; g++)
            for (var b = 0; b < grid; b++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(r * 32768 / (grid - 1)));
                offset += 2;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(g * 32768 / (grid - 1)));
                offset += 2;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(b * 32768 / (grid - 1)));
                offset += 2;
            }

    for (var channel = 0; channel < 3; channel++)
        for (var i = 0; i < 256; i++, offset += 2)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(i * 257));

    return profile;
}
