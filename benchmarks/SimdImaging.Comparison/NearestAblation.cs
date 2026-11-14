using System.Globalization;
using System.Runtime.CompilerServices;

namespace SimdImaging.Comparison;

/// <summary>
/// Ablation for the Rgba8 nearest-neighbour fast path (Resize.cs): does collapsing four byte
/// load/stores per pixel into one 32-bit move actually move the needle, BEFORE touching shipped code?
/// <para>Uses the repo's own <see cref="Timing.Time"/> (interleaved rounds, median) — not a new ruler.
/// Same paradigm as <c>Profile.MatrixSchemeBenchmark</c>: replicate only the inner loop, vary its shape,
/// prove byte equality, then decide. This is a measurement, not an optimization claim.</para>
/// </summary>
internal static class NearestAblation
{
    public static void Run()
    {
        // 1080p source -> 960x540 destination, the exact shape of the `perf`/`sizes` nearest case.
        const int srcW = 1920, dstW = 960, rows = 1080;
        var src = new byte[srcW * 4 * rows];
        new Random(20261115).NextBytes(src);

        var dstByte = new byte[dstW * 4 * rows];
        var dstUint = new byte[dstW * 4 * rows];
        var dstSlice = new byte[dstW * 4 * rows];

        // Nearest downscale by 2: destination column x reads source column 2x (a gather, not a
        // contiguous run), so the fastest possible memcpy is NOT available — this is the real shape.
        var starts = new int[dstW];
        for (var x = 0; x < dstW; x++)
            starts[x] = x * 2;

        ByteByByte();
        UintOnce();
        SliceCopy();
        var byteEqual = dstByte.AsSpan().SequenceEqual(dstUint) && dstByte.AsSpan().SequenceEqual(dstSlice);
        Console.WriteLine($"byte-identical across all three shapes: {byteEqual}");
        if (!byteEqual)
        {
            Console.WriteLine("ABORT: a shape changed the bytes; never ship it.");
            return;
        }

        var runners = new[]
        {
            new Runner("byte x4 (current)", ByteByByte),
            new Runner("uint once", UintOnce),
            new Runner("Slice.CopyTo", SliceCopy),
        };
        var times = Timing.Time(runners, rounds: 9);

        Console.WriteLine();
        Console.WriteLine($"{"shape",-22} {"ms",10} {"vs current",12}");
        foreach (var r in runners)
        {
            var ratio = times[r.Name] == 0 ? 0 : times["byte x4 (current)"] / times[r.Name];
            Console.WriteLine($"{r.Name,-22} {Timing.F(times[r.Name]),10} " +
                              $"{ratio.ToString("F2", CultureInfo.InvariantCulture) + "x",12}");
        }
        Console.WriteLine();
        Console.WriteLine("ratio = current_ms / shape_ms (>1 means the shape is faster).");
        Console.WriteLine("Run twice: a single cell that moves 2x between runs is not a number.");

        void ByteByByte()
        {
            for (var y = 0; y < rows; y++)
            {
                var from = src.AsSpan(y * srcW * 4, srcW * 4);
                var to = dstByte.AsSpan(y * dstW * 4, dstW * 4);
                for (var x = 0; x < dstW; x++)
                {
                    var f = starts[x] * 4;
                    var t = x * 4;
                    to[t] = from[f];
                    to[t + 1] = from[f + 1];
                    to[t + 2] = from[f + 2];
                    to[t + 3] = from[f + 3];
                }
            }
        }

        void UintOnce()
        {
            for (var y = 0; y < rows; y++)
            {
                var from = src.AsSpan(y * srcW * 4, srcW * 4);
                var to = dstUint.AsSpan(y * dstW * 4, dstW * 4);
                for (var x = 0; x < dstW; x++)
                {
                    var f = starts[x] * 4;
                    var t = x * 4;
                    Unsafe.WriteUnaligned(ref to[t], Unsafe.ReadUnaligned<uint>(ref from[f]));
                }
            }
        }

        void SliceCopy()
        {
            for (var y = 0; y < rows; y++)
            {
                var from = src.AsSpan(y * srcW * 4, srcW * 4);
                var to = dstSlice.AsSpan(y * dstW * 4, dstW * 4);
                for (var x = 0; x < dstW; x++)
                {
                    var f = starts[x] * 4;
                    var t = x * 4;
                    from.Slice(f, 4).CopyTo(to.Slice(t, 4));
                }
            }
        }
    }
}
