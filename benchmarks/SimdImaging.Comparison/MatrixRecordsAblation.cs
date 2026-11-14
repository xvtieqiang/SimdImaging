using System;
using System.Globalization;
using System.Runtime.Intrinsics;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Comparison;

/// <summary>
/// Ablation for the "remaining optimization space" logged in known-gaps.md: ApplyMatrixRgba8 rebuilds a
/// 3×256×4 float records table on EVERY call. Question: for a repeated fixed matrix (sepia/grayscale/
/// saturation in the A/B harness), what fraction of the end-to-end call is that rebuild — i.e. what is the
/// CEILING a records cache could save? Answer with numbers before deciding whether to touch shipped code.
/// <para>Follows the Profile.MatrixSchemeBenchmark pattern: replicate the inner loop, vary one thing,
/// prove byte equality, use the repo's own Timing.Time (interleaved, median). This is a measurement,
/// not an optimization claim and not a second golden ruler.</para>
/// </summary>
internal static class MatrixRecordsAblation
{
    // Fixed sepia matrix, the same coefficients shipped in PointOps.Sepia(1f).
    private static readonly float[] Sepia =
    [
        0.393f, 0.769f, 0.189f, 0f,
        0.349f, 0.686f, 0.168f, 0f,
        0.272f, 0.534f, 0.131f, 0f,
    ];

    public static void Run()
    {
        // Small and large: the rebuild is a fixed cost, so its share must FALL as the image grows.
        foreach (var (w, h) in new[] { (640, 360), (1920, 1080), (3840, 2160) })
            Measure(w, h);
    }

    private static void Measure(int width, int height)
    {
        using var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var src = image[0].Pixels.View;
        new Random(20261115).NextBytes(image.WritablePixels);

        // Both variants write into disjoint destinations so interleaved timing can't hide a store.
        using var dstCurrent = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8);
        using var dstCached = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8);

        // Precompute the cached records once, outside all timed delegates.
        var cached = BuildRecords(Sepia);

        Kernel(src, dstCurrent.View, Rebuild(Sepia));   // correctness fill (current shape)
        Kernel(src, dstCached.View, cached);            // correctness fill (cached shape)
        var identical = dstCurrent.View.Span.SequenceEqual(dstCached.View.Span);
        Console.WriteLine($"{width}x{height}: byte-identical (rebuild vs cached) = {identical}");
        if (!identical)
        {
            Console.WriteLine("  ABORT: shapes differ; a cache would change bytes.");
            return;
        }

        var runners = new[]
        {
            new Runner("rebuild+kernel (current)", () => Kernel(src, dstCurrent.View, Rebuild(Sepia))),
            new Runner("kernel, cached records", () => Kernel(src, dstCached.View, cached)),
        };
        var times = Timing.Time(runners, rounds: 9);
        var cur = times["rebuild+kernel (current)"];
        var cac = times["kernel, cached records"];
        var ceiling = cur == 0 ? 0 : (cur - cac) / cur * 100;
        Console.WriteLine($"{"",-2} current={Timing.F(cur)} ms  cached={Timing.F(cac)} ms  " +
                          $"cache ceiling = {ceiling.ToString("F1", CultureInfo.InvariantCulture)}% of call " +
                          $"(speedup if rebuilt were free: {(cac == 0 ? 0 : cur / cac).ToString("F2", CultureInfo.InvariantCulture)}x)");
        Console.WriteLine();
    }

    /// <summary>The exact rebuild loop from PointLut.ApplyMatrixRgba8, isolated so it can be timed
    /// separately and so the "cached" variant can skip it. Same arithmetic, same order.</summary>
    private static float[] BuildRecords(float[] m)
    {
        var records = new float[3 * 256 * 4];
        const float scale = 1f / 255f;
        for (var input = 0; input < 3; input++)
        {
            var c0 = m[input];
            var c1 = m[4 + input];
            var c2 = m[8 + input];
            var recordBase = input * 256 * 4;
            for (var v = 0; v < 256; v++)
            {
                var sample = v * scale;
                var at = recordBase + v * 4;
                records[at] = c0 * sample;
                records[at + 1] = c1 * sample;
                records[at + 2] = c2 * sample;
                records[at + 3] = 0f;
            }
        }
        return records;
    }

    // Rebuild into a fresh array each call (models the current per-call cost). Reuse is intentionally
    // NOT done here — that is the whole point of the comparison.
    private static float[] Rebuild(float[] m) => BuildRecords(m);

    /// <summary>The value-major pixel loop, replicated from PointLut.ApplyMatrixRgba8 (vector path + scalar
    /// tail). Byte-identical to the shipped kernel by construction; used only to attribute time.</summary>
    private static void Kernel(PixelView source, PixelView destination, float[] records)
    {
        var offsets = Vector128.Create(Sepia[3], Sepia[7], Sepia[11], 0f);
        var alphaLanes = Vector128.Create(0xFF000000u, 0xFF000000u, 0xFF000000u, 0xFF000000u).AsByte();
        var scale = Vector128.Create(255f);
        var half = Vector128.Create(0.5f);
        var max = Vector128.Create(255f);
        var zero = Vector128<float>.Zero;

        RowParallel.For(source.Width, source.Height, (start, end) =>
        {
            var useVector = Vector128.IsHardwareAccelerated;
            for (var y = start; y < end; y++)
            {
                var from = source.Row(y);
                var to = destination.Row(y);
                var length = source.Width * 4;
                var i = 0;
                var vectorEnd = useVector ? length - length % 16 : 0;
                for (; i < vectorEnd; i += 16)
                {
                    var q0 = Clamp(Contribution(from, i, records, offsets), scale, half, max, zero);
                    var q1 = Clamp(Contribution(from, i + 4, records, offsets), scale, half, max, zero);
                    var q2 = Clamp(Contribution(from, i + 8, records, offsets), scale, half, max, zero);
                    var q3 = Clamp(Contribution(from, i + 12, records, offsets), scale, half, max, zero);
                    var low = Vector128.Narrow(q0.AsUInt32(), q1.AsUInt32());
                    var high = Vector128.Narrow(q2.AsUInt32(), q3.AsUInt32());
                    var packed = Vector128.Narrow(low, high);
                    var alpha = Vector128.LoadUnsafe(ref from[i]) & alphaLanes;
                    (packed | alpha).StoreUnsafe(ref to[i]);
                }
                for (; i < length; i += 4)
                {
                    var value = Contribution(from, i, records, offsets);
                    to[i] = ToByte(value.GetElement(0));
                    to[i + 1] = ToByte(value.GetElement(1));
                    to[i + 2] = ToByte(value.GetElement(2));
                    to[i + 3] = from[i + 3];
                }
            }
        });
    }

    private static Vector128<int> Clamp(Vector128<float> value, Vector128<float> scale,
        Vector128<float> half, Vector128<float> max, Vector128<float> zero)
    {
        var scaled = value * scale + half;
        return Vector128.ConvertToInt32(Vector128.Max(Vector128.Min(scaled, max), zero));
    }

    private static Vector128<float> Contribution(ReadOnlySpan<byte> row, int p, float[] records,
        Vector128<float> offsets)
    {
        var r = Vector128.LoadUnsafe(ref records[row[p] * 4]);
        var g = Vector128.LoadUnsafe(ref records[(256 + row[p + 1]) * 4]);
        var b = Vector128.LoadUnsafe(ref records[(512 + row[p + 2]) * 4]);
        return ((r + g) + b) + offsets;
    }

    private static byte ToByte(float v)
    {
        var scaled = v * 255f + 0.5f;
        var clamped = Math.Clamp(scaled, 0f, 255f);
        return (byte)clamped;
    }
}
