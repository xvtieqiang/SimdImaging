using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Is the small-image gap in pixelate the accumulate loop or the fill? Three rewrites of the same block average,
/// interleaved in one process, each checked byte for byte against the library's own output.
/// </summary>
internal static class PixelateAblate
{
    private const int Block = 12;

    public static void Run(int width, int height, int rounds)
    {
        var data = Fixture.Make(width, height);
        using var image = Fixture.Ours(data, width, height);
        var source = image[0].Pixels.View;

        using var reference = FilterOps.Pixelate(source, Block, null);
        var expected = reference.View.Span.ToArray();

        var variants = new (string Name, Func<PixelView, int, WorkingBuffer> Body)[]
        {
            ("库内实现", (v, b) => FilterOps.Pixelate(v, b, null)),
            ("u32 读取", AccumulateU32),
            ("SIMD 填充", SimdFill),
            ("u32 + SIMD", Both),
            ("int 累加", IntSums),
        };

        foreach (var (name, body) in variants)
        {
            using var result = body(source, Block);
            if (!result.View.Span.SequenceEqual(expected))
                throw new InvalidOperationException($"{name} does not match the library byte for byte");
        }

        var samples = variants.ToDictionary(v => v.Name, _ => new List<double>());
        for (var i = 0; i < rounds + 2; i++)
        {
            foreach (var (name, body) in variants)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using (body(source, Block)) { }
                clock.Stop();
                if (i >= 2)
                    samples[name].Add(clock.Elapsed.TotalMilliseconds);
            }
        }

        Console.WriteLine($"{width}x{height} · 块 {Block} · {rounds} 轮交错");
        foreach (var (name, list) in samples)
        {
            list.Sort();
            Console.WriteLine($"  {name,-10} 中位 {list[list.Count / 2]:F4} ms   最小 {list[0]:F4}");
        }
    }

    /// <summary>One 32-bit read per pixel instead of four byte reads, then the same long accumulation.</summary>
    private static WorkingBuffer AccumulateU32(PixelView source, int blockSize) => Build(source, blockSize, u32: true, simd: false);

    /// <summary>The same accumulation, but the block value is written four pixels at a time.</summary>
    private static WorkingBuffer SimdFill(PixelView source, int blockSize) => Build(source, blockSize, u32: false, simd: true);

    private static WorkingBuffer Both(PixelView source, int blockSize) => Build(source, blockSize, u32: true, simd: true);

    /// <summary>32-bit accumulators: a block holds at most 36 x 255 x 255, which cannot overflow an int.</summary>
    private static WorkingBuffer IntSums(PixelView source, int blockSize) => BuildInt(source, blockSize);

    private static WorkingBuffer BuildInt(PixelView source, int blockSize)
    {
        var width = source.Width;
        var height = source.Height;
        var columns = (width + blockSize - 1) / blockSize;
        var blockRows = (height + blockSize - 1) / blockSize;
        var destination = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8, null);
        var view = destination.View;

        Parallel.For(0, blockRows, band =>
        {
            var sums = ArrayPool<int>.Shared.Rent(columns * 4);
            try
            {
                var top = band * blockSize;
                var bottom = Math.Min(top + blockSize, height);
                var rows = bottom - top;
                Array.Clear(sums, 0, columns * 4);

                for (var y = top; y < bottom; y++)
                {
                    var row = source.Row(y);
                    var x = 0;
                    for (var column = 0; column < columns; column++)
                    {
                        var end = Math.Min(x + blockSize, width);
                        var at = column * 4;
                        for (; x < end; x++)
                        {
                            var pixel = Unsafe.ReadUnaligned<uint>(ref row[x * 4]);
                            var alpha = (int)(pixel >> 24);
                            sums[at] += (int)(pixel & 0xFF) * alpha;
                            sums[at + 1] += (int)((pixel >> 8) & 0xFF) * alpha;
                            sums[at + 2] += (int)((pixel >> 16) & 0xFF) * alpha;
                            sums[at + 3] += alpha;
                        }
                    }
                }

                for (var column = 0; column < columns; column++)
                {
                    var at = column * 4;
                    var alphaSum = (long)sums[at + 3];
                    var start = column * blockSize;
                    var end = Math.Min(start + blockSize, width);
                    var area = (long)(end - start) * rows;

                    uint r = 0, g = 0, b = 0;
                    if (alphaSum > 0)
                    {
                        var half = alphaSum / 2;
                        r = (uint)((sums[at] + half) / alphaSum);
                        g = (uint)((sums[at + 1] + half) / alphaSum);
                        b = (uint)((sums[at + 2] + half) / alphaSum);
                    }

                    var a8 = (uint)((alphaSum + (area / 2)) / area);
                    var packed = r | (g << 8) | (b << 16) | (a8 << 24);

                    for (var y = top; y < bottom; y++)
                    {
                        var row = view.Row(y);
                        for (var x = start; x < end; x++)
                            Unsafe.WriteUnaligned(ref row[x * 4], packed);
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(sums);
            }
        });

        return destination;
    }

    private static WorkingBuffer Build(PixelView source, int blockSize, bool u32, bool simd)
    {
        var width = source.Width;
        var height = source.Height;
        var columns = (width + blockSize - 1) / blockSize;
        var blockRows = (height + blockSize - 1) / blockSize;
        var destination = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8, null);
        var view = destination.View;

        Parallel.For(0, blockRows, band =>
        {
            var sums = ArrayPool<long>.Shared.Rent(columns * 4);
            try
            {
                var top = band * blockSize;
                var bottom = Math.Min(top + blockSize, height);
                var rows = bottom - top;
                Array.Clear(sums, 0, columns * 4);

                for (var y = top; y < bottom; y++)
                {
                    var row = source.Row(y);
                    var x = 0;
                    for (var column = 0; column < columns; column++)
                    {
                        var end = Math.Min(x + blockSize, width);
                        var at = column * 4;
                        for (; x < end; x++)
                        {
                            if (u32)
                            {
                                var pixel = Unsafe.ReadUnaligned<uint>(ref row[x * 4]);
                                var alpha = pixel >> 24;
                                sums[at] += (pixel & 0xFF) * alpha;
                                sums[at + 1] += ((pixel >> 8) & 0xFF) * alpha;
                                sums[at + 2] += ((pixel >> 16) & 0xFF) * alpha;
                                sums[at + 3] += alpha;
                            }
                            else
                            {
                                var o = x * 4;
                                var a = row[o + 3];
                                sums[at] += row[o] * a;
                                sums[at + 1] += row[o + 1] * a;
                                sums[at + 2] += row[o + 2] * a;
                                sums[at + 3] += a;
                            }
                        }
                    }
                }

                for (var column = 0; column < columns; column++)
                {
                    var at = column * 4;
                    var alphaSum = sums[at + 3];
                    var start = column * blockSize;
                    var end = Math.Min(start + blockSize, width);
                    var area = (long)(end - start) * rows;

                    uint r = 0, g = 0, b = 0;
                    if (alphaSum > 0)
                    {
                        var half = alphaSum / 2;
                        r = (uint)((sums[at] + half) / alphaSum);
                        g = (uint)((sums[at + 1] + half) / alphaSum);
                        b = (uint)((sums[at + 2] + half) / alphaSum);
                    }

                    var a8 = (uint)((alphaSum + (area / 2)) / area);
                    var packed = r | (g << 8) | (b << 16) | (a8 << 24);

                    for (var y = top; y < bottom; y++)
                    {
                        var row = view.Row(y);
                        var x = start;
                        if (simd)
                        {
                            var quad = Vector128.Create(packed);
                            for (; x + 4 <= end; x += 4)
                                quad.StoreUnsafe(ref Unsafe.As<byte, uint>(ref row[x * 4]));
                        }

                        for (; x < end; x++)
                            Unsafe.WriteUnaligned(ref row[x * 4], packed);
                    }
                }
            }
            finally
            {
                ArrayPool<long>.Shared.Return(sums);
            }
        });

        return destination;
    }
}
