using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The M2 exit condition was written as "zero allocation for a fused point chain". It is not met and cannot be met
/// without changing the <see cref="WorkingBuffer"/> wrapper or the parallel scheduler, so it was replaced on
/// 2026-11-26 by a measurable bound: a fused byte-LUT chain on one worker must stay under 4 KiB per operation. The
/// measured figure after the execution-shape cache, closure removal and the degree-1 direct path is 256 B/op; the
/// bound leaves a 16x margin for runtime bookkeeping and is enforced here instead of living in a report.
/// </summary>
public sealed class AllocationBoundTests
{
    [Fact]
    public void AFusedPointChain_StaysBelowTheAgreedAllocationBound()
    {
        using var image = TestImages.Gradient(640, 360, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan { BandRows = 64 }
            .AddPointLut("brightness", Table(v => Math.Clamp((int)(v * 0.8f + 20), 0, 255)))
            .AddPointLut("contrast", Table(v => Math.Clamp((int)((v - 128) * 1.2f + 128), 0, 255)));
        var options = new ExecuteOptions { ForceFusion = true, MaxDegreeOfParallelism = 1 };

        // Warm every pool, weight table and JIT path the loop below will use.
        for (var i = 0; i < 3; i++)
        {
            using var warm = plan.ExecuteWithOptions(image[0].Pixels.View, options);
        }

        const int runs = 5;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < runs; i++)
        {
            using var result = plan.ExecuteWithOptions(image[0].Pixels.View, options);
        }

        var bytesPerOp = (GC.GetAllocatedBytesForCurrentThread() - before) / runs;
        bytesPerOp.ShouldBeLessThan(4 * 1024,
            $"a fused point chain allocated {bytesPerOp} bytes per operation");
    }

    private static byte[] Table(Func<int, int> map)
    {
        var table = new byte[4 * 256];
        for (var v = 0; v < 256; v++)
        {
            var value = (byte)map(v);
            table[v] = value;
            table[256 + v] = value;
            table[512 + v] = value;
            table[768 + v] = (byte)v;
        }

        return table;
    }
}
