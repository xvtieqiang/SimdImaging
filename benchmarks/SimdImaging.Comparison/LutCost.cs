using System.Diagnostics;
using System.Runtime.InteropServices;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Comparison;

/// <summary>
/// What a 64K two-input table costs to build, and what the float round trip it replaces costs per pixel. The
/// crossover is the only thing that decides whether an operator should have one.
/// </summary>
internal static class LutCost
{
    public static void Run(int rounds)
    {
        // Build only.
        var build = new List<double>();
        for (var i = 0; i < rounds + 2; i++)
        {
            var clock = Stopwatch.StartNew();
            var table = Build(0.6f);
            clock.Stop();
            GC.KeepAlive(table);
            if (i >= 2) build.Add(clock.Elapsed.TotalMilliseconds);
        }
        build.Sort();
        Console.WriteLine($"  建表          {build[build.Count / 2]:F3} ms");

        foreach (var side in new[] { 64, 128, 256, 512, 1024, 2048 })
        {
            using var source = Fill(side);
            using var halo = Fill(side);
            var view = source[0].Pixels.View;
            var haloView = halo[0].Pixels.View;

            var floatMs = Time(rounds, () => { using var r = CombineFloat(view, haloView); });
            var lutMs = Time(rounds, () => { using var r = CombineLut(view, haloView); });

            Console.WriteLine($"  {side,4}x{side,-4}  浮点 {floatMs,7:F3} ms   查表 {lutMs,7:F3} ms   " +
                $"{(floatMs >= lutMs ? "查表更快" : "浮点更快")}");
        }
    }

    private static double Time(int rounds, Action run)
    {
        run();
        var times = new List<double>();
        for (var i = 0; i < rounds + 2; i++)
        {
            var clock = Stopwatch.StartNew();
            run();
            clock.Stop();
            if (i >= 2) times.Add(clock.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    private static byte[] Build(float amount)
    {
        var table = new byte[256 * 256];
        for (var s8 = 0; s8 < 256; s8++)
        {
            var s = s8 * (1f / 255f);
            for (var b8 = 0; b8 < 256; b8++)
            {
                var b = b8 * (1f / 255f);
                var value = s + (amount * (s - b));
                var scaled = (value * 255f) + 0.5f;
                table[(s8 << 8) | b8] = scaled <= 0f ? (byte)0 : scaled >= 255f ? (byte)255 : (byte)scaled;
            }
        }
        return table;
    }

    private static WorkingBuffer CombineFloat(PixelView source, PixelView halo)
    {
        var width = source.Width;
        var destination = WorkingBuffer.Rent(width, source.Height, WorkingFormat.Rgba8, null);
        RowParallel.For(width, source.Height, (start, end) =>
        {
            var a = new float[width * 4];
            var b = new float[width * 4];
            var o = new float[width * 4];
            for (var y = start; y < end; y++)
            {
                Pixels.ReadRowRaw(source.Row(y), width, source.Format, a);
                Pixels.ReadRowRaw(halo.Row(y), width, halo.Format, b);
                for (var i = 0; i < width * 4; i += 4)
                {
                    o[i] = Math.Clamp(a[i] + (0.6f * (a[i] - b[i])), 0f, 1f);
                    o[i + 1] = Math.Clamp(a[i + 1] + (0.6f * (a[i + 1] - b[i + 1])), 0f, 1f);
                    o[i + 2] = Math.Clamp(a[i + 2] + (0.6f * (a[i + 2] - b[i + 2])), 0f, 1f);
                    o[i + 3] = a[i + 3];
                }
                Pixels.WriteRowRaw(o, width, WorkingFormat.Rgba8, destination.View.Row(y));
            }
        });
        return destination;
    }

    private static WorkingBuffer CombineLut(PixelView source, PixelView halo)
    {
        var table = Build(0.6f);
        var width = source.Width;
        var destination = WorkingBuffer.Rent(width, source.Height, WorkingFormat.Rgba8, null);
        RowParallel.For(width, source.Height, (start, end) =>
        {
            for (var y = start; y < end; y++)
            {
                var s = source.Row(y);
                var h = halo.Row(y);
                var o = destination.View.Row(y);
                for (var i = 0; i < width * 4; i += 4)
                {
                    o[i] = table[(s[i] << 8) | h[i]];
                    o[i + 1] = table[(s[i + 1] << 8) | h[i + 1]];
                    o[i + 2] = table[(s[i + 2] << 8) | h[i + 2]];
                    o[i + 3] = s[i + 3];
                }
            }
        });
        return destination;
    }

    private static SimdImaging.Image Fill(int side)
    {
        var image = SimdImaging.Image.Create(side, side, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i * 37) & 0xFF);
        return image;
    }
}
