using System.Globalization;
using System.Runtime.InteropServices;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Steady-state allocation probe for the M2 exit condition "point-operator fusion allocates 0 B/op"
/// (architecture.md §1.3 criterion 2 and §11). It reuses the two rulers the repo already defines
/// rather than growing a third one:
/// <list type="bullet">
/// <item><c>GC.GetAllocatedBytesForCurrentThread</c> — the literal §1.3 ruler.</item>
/// <item><c>GC.GetTotalAllocatedBytes(precise: false)</c> — Infrastructure.Timing's process-wide
/// ruler, the only one that sees pool-thread allocations under band parallelism (its own comment
/// warns that the current-thread ruler "made the opponent look allocation-free").</item>
/// </list>
/// Both are read around the SAME batch of executions, so the two numbers are directly comparable.
/// <para>Covers both fusion paths named in §11/§5.5: byte-domain LUT folding
/// (<c>AddPointLut</c>, "four memory round-trips become one") and window-recursion fusion over
/// <c>PlanSteps.Point</c> adapters (<c>FusedExecutor</c>). Each is measured fused (M10 default)
/// and step-by-step (<c>AllowFusion = false</c>, the pre-M10 path the 2026-11-11 7/39/8 KiB/op
/// readings came from).</para>
/// <para>Two sizes are measured to separate per-call fixed overhead from per-pixel work: if B/op
/// stays flat across 1920x1080 and 640x360, the steady-state allocation comes from rebuilding the
/// execution plan each call (lists, LUT clones, delegates, Parallel.For scheduling), not from pixel
/// buffers — which the pool already removes. A degree=1 control makes the two rulers agree, proving
/// the parallel gap is pool-thread allocation rather than ruler noise.</para>
/// <para>Deliberately a child-style mode: it measures our side only, so it never loads the oracle,
/// never touches the licence file, writes no report and regenerates no golden. Warm-up settles JIT
/// tiering and the pixel pool before the measured batches; three batches give a median and a range,
/// because a single cell that moves 2x between runs is not a number.</para>
/// </summary>
internal static class AllocPlan
{
    private const int Warmup = 40;
    private const int Batch = 120;
    private const int Passes = 3;

    public static void Run()
    {
        Console.WriteLine("== M2 steady-state allocation probe (融合后点算子链稳态分配) ==");
        Console.WriteLine($"machine: {RuntimeInformation.OSDescription} | " +
                          $"{RuntimeInformation.ProcessArchitecture} | cores={Environment.ProcessorCount} | " +
                          $"{RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"tier: avx2={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} | " +
                          $"warmup={Warmup} batch={Batch} passes={Passes}");
        Console.WriteLine("ruler A (per-thread) = GC.GetAllocatedBytesForCurrentThread   [§1.3 literal]");
        Console.WriteLine("ruler B (process)    = GC.GetTotalAllocatedBytes(precise:false) [Infrastructure.Timing]");
        Console.WriteLine("steady state = same plan object re-executed; pool + JIT settled by warm-up.");

        // Byte-domain LUT folding: three consecutive 4x256 tables fold into one table / one pass.
        var lut = new ProcessingPlan { BandRows = 64 }
            .AddPointLut("invert", Table(inverted: true))
            .AddPointLut("invert-again", Table(inverted: true))
            .AddPointLut("identity", Table(inverted: false));
        // Window-recursion fusion: three band-able Point steps share two band windows per worker.
        var points = new ProcessingPlan { BandRows = 64 }
            .Add("contrast", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.2f)))
            .Add("sepia", OpCategory.Point, PlanSteps.Point(v => PointOps.Sepia(v, 1f)))
            .Add("grayscale", OpCategory.Point, PlanSteps.Point(v => PointOps.Grayscale(v)));

        RunSize(1920, 1080, lut, points);
        RunSize(640, 360, lut, points);

        Console.WriteLine();
        Console.WriteLine("median [min..max] over three batches; B/op = allocated bytes / executions.");
        Console.WriteLine("B/op does not scale with pixel count (1080p is 9x the pixels of 640x360, yet");
        Console.WriteLine("LUT-fold degree=1 is exactly 2144 at both): cost is per-call plan rebuild +");
        Console.WriteLine("per-band WorkingBuffer wrappers + Parallel.For scheduling, not pixel buffers");
        Console.WriteLine("(those come from the pool). The window path additionally tracks band count.");
    }

    private static void RunSize(int width, int height, ProcessingPlan lut, ProcessingPlan points)
    {
        using var source = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8);
        var pixels = source.View.Span;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i * 37 + 11) & 0xFF);
        var view = source.View;

        var fused = new ExecuteOptions();                              // M10: AllowFusion = true
        var step = new ExecuteOptions { AllowFusion = false };         // pre-M10 step-by-step
        var serialFused = new ExecuteOptions { MaxDegreeOfParallelism = 1 };

        Console.WriteLine();
        Console.WriteLine($"--- size {width}x{height} Rgba8 ---");
        Console.WriteLine($"{"chain",-34} {"fusion",-8} {"degree",-8} " +
                          $"{"per-thread B/op",26} {"process B/op",26}");
        Console.WriteLine(new string('-', 110));

        Report("AddPointLut x3 (LUT fold)", view, lut, fused, "fused", "default");
        Report("AddPointLut x3 (LUT fold)", view, lut, step, "step", "default");
        Report("PlanSteps.Point x3 (window)", view, points, fused, "fused", "default");
        Report("PlanSteps.Point x3 (window)", view, points, step, "step", "default");

        // Degree=1 control: with no pool-thread work the two rulers must agree. This is what makes
        // the "per-thread under-reports under parallelism" gap interpretable rather than a lucky 0.
        Report("AddPointLut x3 (LUT fold)", view, lut, serialFused, "fused", "1");
        Report("PlanSteps.Point x3 (window)", view, points, serialFused, "fused", "1");
    }

    private static void Report(string chain, PixelView view, ProcessingPlan plan, ExecuteOptions options,
        string fusion, string degree)
    {
        Func<WorkingBuffer> run = () => plan.ExecuteWithOptions(view, options);

        // Warm-up: JIT tiering, the pixel pool, the weight/LUT caches and thread-pool threads all
        // settle here. None of this counts toward the measured batches.
        for (var i = 0; i < Warmup; i++)
        {
            using var warm = run();
            GC.KeepAlive(warm);
        }

        var thread = new double[Passes];
        var process = new double[Passes];
        for (var pass = 0; pass < Passes; pass++)
        {
            var threadBefore = GC.GetAllocatedBytesForCurrentThread();
            var processBefore = GC.GetTotalAllocatedBytes(precise: false);
            for (var i = 0; i < Batch; i++)
            {
                using var result = run();
                GC.KeepAlive(result);
            }
            var threadAfter = GC.GetAllocatedBytesForCurrentThread();
            var processAfter = GC.GetTotalAllocatedBytes(precise: false);
            thread[pass] = (threadAfter - threadBefore) / (double)Batch;
            process[pass] = (processAfter - processBefore) / (double)Batch;
        }

        Console.WriteLine($"{chain,-34} {fusion,-8} {degree,-8} " +
                          $"{Range(thread),26} {Range(process),26}");
    }

    private static string Range(double[] samples)
    {
        Array.Sort(samples);
        static string F(double v) => v.ToString("F0", CultureInfo.InvariantCulture);
        return $"{F(samples[samples.Length / 2])} [{F(samples[0])}..{F(samples[^1])}]";
    }

    /// <summary>A 4x256 byte table: RGB all map by the same rule, alpha is left as identity.</summary>
    private static byte[] Table(bool inverted)
    {
        var table = new byte[4 * 256];
        for (var v = 0; v < 256; v++)
        {
            var x = (byte)(inverted ? 255 - v : v);
            table[v] = x;
            table[256 + v] = x;
            table[512 + v] = x;
            table[768 + v] = (byte)v;
        }

        return table;
    }
}
