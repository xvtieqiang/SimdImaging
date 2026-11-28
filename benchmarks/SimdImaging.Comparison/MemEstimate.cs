using System.Diagnostics;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// Does <see cref="MemoryEstimator"/> describe what a plan actually costs? Two errors were suspected and they point
/// in opposite directions: the estimate adds a whole-image scratch term to a per-worker one as if they coexisted
/// (over), and it sizes per-worker scratch from a single step's halo while a fused segment carries the sum (under).
/// </summary>
/// <remarks>
/// Measured in a fresh process per shape, for the reason <see cref="PeakMemory"/> gives: an in-process pass runs
/// against a pool that a warm-up already filled, so the second run allocates nothing new and the working set does
/// not move. The first version of this file reported 8 KiB for a six-step 4K chain, which would have "proved" that
/// the estimator overestimates by three orders of magnitude.
/// </remarks>
internal static class MemEstimate
{
    private static readonly string[] Shapes = ["points6", "points6-nofusion", "blurs3", "global1", "mixed"];

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, $"估算 vs 实测（{width}×{height}，子进程冷启动）");
        report.Line("> 实测 = 全新进程里跑**一次**，`WorkingSet64` 相对强制 GC 基线的峰值增量（~1 ms 采样），3 次取中位数。");
        report.Line("> 估算 = `MemoryEstimator.Estimate(...).PeakBytes`，在父进程里按同一尺寸算。");
        report.Line("> 方向：**实测 > 估算** 是低估（危险），**实测 < 估算** 是高估（保守）。");
        report.Line();

        using var probe = Fixture.Ours(Fixture.Make(width, height), width, height);

        var rows = new List<string[]>();
        foreach (var shape in Shapes)
        {
            var estimate = MemoryEstimator.Estimate(probe[0].Pixels.View, Build(shape), new ExecuteOptions());
            var measured = Median(Measure(shape, width, height));
            var ratio = estimate.PeakBytes == 0 ? 0 : (double)measured / estimate.PeakBytes;

            rows.Add([
                shape,
                MiB(estimate.PeakBytes),
                MiB(measured),
                ratio.ToString("F2", CultureInfo.InvariantCulture) + "×",
                ratio > 1.10 ? "低估" : ratio < 0.90 ? "高估" : "相符",
                $"{estimate.Workers} 线程 · scratch {MiB(estimate.OperatorScratchBytes)}",
            ]);
        }

        report.Table(["形态", "估算峰值", "实测峰值", "实测÷估算", "方向", "构成"], rows);

        // The only property that matters: an estimate that is too low lets a plan run past its ceiling. Too high is
        // a cost (callers budget for memory they never use), but it is a safe cost.
        var under = rows.Count(r => r[4] == "低估");
        report.Line();
        report.Line(under == 0
            ? "**结论：没有低估。** 估算在这些形态上偏保守 6%–46%，方向安全。"
            : $"**结论：{under} 个形态低估。** 估算低于实测，`MaxWorkingBytes` 会放行超预算的计划。");
    }

    /// <summary>One cold process, one run, one number.</summary>
    public static void Child(string shape, int width, int height)
    {
        var data = Fixture.Make(width, height);
        using var source = Fixture.Ours(data, width, height);
        var plan = Build(shape);
        var options = new ExecuteOptions { AllowFusion = !shape.EndsWith("-nofusion", StringComparison.Ordinal) };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baseline = Process.GetCurrentProcess().WorkingSet64;
        var peak = baseline;
        var stop = false;

        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var now = Process.GetCurrentProcess().WorkingSet64;
                if (now > peak)
                    peak = now;
                Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
        };

        sampler.Start();
        try
        {
            using var result = source.ApplyWithOptions(plan, options);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            sampler.Join();
        }

        Console.WriteLine($"peak={(peak - baseline).ToString(CultureInfo.InvariantCulture)}");
    }

    private static List<long> Measure(string shape, int width, int height, int repeats = 3)
    {
        var values = new List<long>();
        var self = typeof(MemEstimate).Assembly.Location;
        for (var i = 0; i < repeats; i++)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            if (!Path.GetFileName(Environment.ProcessPath!)
                    .StartsWith("SimdImaging.Comparison", StringComparison.Ordinal))
                psi.ArgumentList.Add(self);
            psi.ArgumentList.Add("memestimate-child");
            psi.ArgumentList.Add(shape);
            psi.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(height.ToString(CultureInfo.InvariantCulture));

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var line = output.Split('\n').FirstOrDefault(l => l.StartsWith("peak=", StringComparison.Ordinal));
            if (line is not null &&
                long.TryParse(line[5..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var peak))
                values.Add(peak);
        }

        values.Sort();
        return values;
    }

    private static long Median(List<long> values) => values.Count == 0 ? 0 : values[values.Count / 2];

    private static string MiB(long bytes) =>
        (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MiB";

    private static ProcessingPlan Build(string shape)
    {
        var plan = new ProcessingPlan();
        switch (shape)
        {
            case "points6":
            case "points6-nofusion":
                for (var i = 0; i < 6; i++)
                    plan.Add(PlanOps.Contrast(1 + (0.02f * i)));
                break;

            case "blurs3":
                for (var i = 0; i < 3; i++)
                    plan.Add(PlanOps.GaussianBlur(1f + (0.2f * i)));
                break;

            case "global1":
                plan.Add(PlanOps.Clahe(8, 8, 3f));
                break;

            case "mixed":
                plan.Add(PlanOps.Contrast(1.1f));
                plan.Add(PlanOps.Saturation(1.05f));
                plan.Add(PlanOps.Invert());
                plan.Add(PlanOps.GaussianBlur(0.8f));
                plan.Add(PlanOps.GaussianBlur(1.2f));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown shape.");
        }

        return plan;
    }
}
