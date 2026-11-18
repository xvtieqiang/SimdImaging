using SimdImaging.Abstractions;
using SimdImaging.Processing;
using System.Diagnostics;
using System.Globalization;

namespace SimdImaging.Comparison;

/// <summary>
/// Peak working set measured in a FRESH PROCESS per operation, mirroring SimdCodecs' PeakMemory:
/// in-process sampling is polluted by both libraries' warmed pools, so the only honest baseline is a
/// child that starts cold. Reports the rise over a collected baseline, median of three runs.
/// </summary>
internal static class PeakMemory
{
    private static readonly string[] CaseIds = ["resize-bicubic", "gaussian-blur", "grayscale", "invert"];

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, "C3 峰值工作集（独立子进程，冷启动）");
        report.Line("> 每个测量起一个全新进程：加载同一份自生成图案 → GC 收敛 → 记录基线 → 采样线程每 ~1 ms 读 `WorkingSet64` → 跑**一次**算子 → 取峰值增量。3 次取中位数。");
        report.Line("> 这样两边都没有池预热优势，也把编解码/运行时之外的分配算进去。数值是**进程级**峰值增量，不是纯算法内存。");
        report.Line();

        var rows = new List<string[]>();
        foreach (var id in CaseIds)
        {
            var ours = Median(Measure("ours", id, width, height));
            var theirs = Median(Measure("theirs", id, width, height));
            rows.Add([
                id,
                (ours / 1048576.0).ToString("F1", CultureInfo.InvariantCulture),
                (theirs / 1048576.0).ToString("F1", CultureInfo.InvariantCulture),
                ours == 0 ? "—" : (theirs / (double)ours).ToString("F1", CultureInfo.InvariantCulture) + "×",
            ]);
        }

        report.Table(["场景", "我方峰值 MiB", "对手峰值 MiB", "对手÷我方"], rows);

        report.Heading(3, "C3.1 融合：峰值与总分配量，同一条链两遍");
        report.Line("> **两个数说的是两件事，不能互相替代。**");
        report.Line("> **峰值**＝进程在某一瞬间真正占着的内存，回答「跑这张图要多大的机器」。");
        report.Line("> **总分配量**＝这次调用累计向 GC 要过多少内存，回答「GC 要为此做多少活」；它还回去的不算占着，");
        report.Line("> 但每一次要都要付分配和回收的代价，所以它和耗时同向。");
        report.Line("> 同一个子进程里两遍都记：`ForceFusion=true` 与 `AllowFusion=false`，三次取中位。");
        report.Line();

        foreach (var (cw, ch) in new[] { (1920, 1080), (3840, 2160), (7680, 4320) })
        {
            var chainRows = new List<string[]>();
            foreach (var (name, _) in ChainPeaks.Cases())
            {
                var fused = MedianOf(MeasureChain("fused", name, cw, ch));
                var stepwise = MedianOf(MeasureChain("stepwise", name, cw, ch));
                chainRows.Add([
                    name,
                    MiB(fused.Peak),
                    MiB(stepwise.Peak),
                    fused.Peak == 0 ? "—" : (stepwise.Peak / (double)fused.Peak).ToString("F2", CultureInfo.InvariantCulture) + "×",
                    MiB(fused.Allocated),
                    MiB(stepwise.Allocated),
                    fused.Allocated == 0 ? "—" : (stepwise.Allocated / (double)fused.Allocated).ToString("F2", CultureInfo.InvariantCulture) + "×",
                ]);
            }

            report.Heading(4, $"{cw}×{ch}");
            report.Table(
                ["链", "融合峰值", "逐步峰值", "逐步÷融合", "融合总分配", "逐步总分配", "逐步÷融合"],
                chainRows);
        }
    }

    private static string MiB(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);

    private static (long Peak, long Allocated) MedianOf(List<(long Peak, long Allocated)> values)
    {
        values.Sort((a, b) => a.Peak.CompareTo(b.Peak));
        return values[values.Count / 2];
    }

    private static List<(long Peak, long Allocated)> MeasureChain(string side, string chainId, int width, int height,
        int repeats = 3)
    {
        var values = new List<(long, long)>();
        var self = typeof(PeakMemory).Assembly.Location;
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
            psi.ArgumentList.Add("chainpeak-child");
            psi.ArgumentList.Add(side);
            psi.ArgumentList.Add(chainId);
            psi.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(height.ToString(CultureInfo.InvariantCulture));

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"chainpeak child failed ({side} {chainId}): {output}");
            var parts = output.Split('\n').Last().Trim().Split(' ');
            values.Add((long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture)));
        }

        return values;
    }

    /// <summary>The chain under test, run in a fresh process with fusion forced on or off.</summary>
    public static void ChainChild(string[] args)
    {
        var side = args[1];
        var chainId = args[2];
        var width = int.Parse(args[3], CultureInfo.InvariantCulture);
        var height = int.Parse(args[4], CultureInfo.InvariantCulture);

        var catalogue = Environment.GetEnvironmentVariable("SIMDIMAGING_SINGLES") == "1"
            ? ChainPeaks.Singles()
            : ChainPeaks.Cases();
        var basePlan = catalogue.First(x => x.Name == chainId).Plan;
        var plan = int.TryParse(Environment.GetEnvironmentVariable("SIMDIMAGING_PLAN_BAND_ROWS"), out var planBandRows)
            && planBandRows > 0
                ? basePlan.WithBandRows(planBandRows)
                : basePlan;
        var data = Fixture.Make(width, height);
        using var image = Fixture.Ours(data, width, height);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var baseline = process.WorkingSet64;
        long peak = baseline;
        var gate = new object();
        using var cancel = new CancellationTokenSource();
        var sampler = Task.Run(() =>
        {
            while (!cancel.IsCancellationRequested)
            {
                process.Refresh();
                var current = process.WorkingSet64;
                lock (gate)
                    if (current > peak) peak = current;
                Thread.Sleep(1);
            }
        });

        var degree = int.TryParse(Environment.GetEnvironmentVariable("SIMDIMAGING_DEGREE"), out var d) && d > 0 ? d : 0;
        var options = side == "fused"
            ? new ExecuteOptions { ForceFusion = true, MaxDegreeOfParallelism = degree }
            : new ExecuteOptions { AllowFusion = false, MaxDegreeOfParallelism = degree };
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var result = image.ApplyWithOptions(plan, options);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        cancel.Cancel();
        sampler.Wait();
        long rise;
        lock (gate) rise = peak - baseline;
        GC.KeepAlive(result);

        // What is still resident once the operation is over and the caches are given the chance to drop: the
        // difference between "this call needed it" and "this process is still holding it".
        result.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var residual = process.WorkingSet64 - baseline;

        Console.WriteLine(
            $"{rise.ToString(CultureInfo.InvariantCulture)} {allocated.ToString(CultureInfo.InvariantCulture)} {residual.ToString(CultureInfo.InvariantCulture)}");
    }

    private static List<long> Measure(string side, string caseId, int width, int height, int repeats = 3)
    {
        var values = new List<long>();
        var self = typeof(PeakMemory).Assembly.Location;
        for (var i = 0; i < repeats; i++)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            // `dotnet run` launches through dotnet.exe and needs the dll as the first argument;
            // a published self-contained exe already has an entry point and must not get one.
            if (!Path.GetFileName(Environment.ProcessPath!)
                    .StartsWith("SimdImaging.Comparison", StringComparison.Ordinal))
                psi.ArgumentList.Add(self);
            psi.ArgumentList.Add("peakmem-child");
            psi.ArgumentList.Add(side);
            psi.ArgumentList.Add(caseId);
            psi.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(height.ToString(CultureInfo.InvariantCulture));

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"peakmem child failed ({side} {caseId}): {output}");
            values.Add(long.Parse(output.Split('\n').Last().Trim().Split(' ')[0], CultureInfo.InvariantCulture));
        }

        return values;
    }

    public static void Child(string[] args)
    {
        var side = args[1];
        var caseId = args[2];
        var width = int.Parse(args[3], CultureInfo.InvariantCulture);
        var height = int.Parse(args[4], CultureInfo.InvariantCulture);

        // Band height is a memory knob for the separable blurs: each worker holds one band of float scratch.
        // Set here rather than in the child's arguments so the peak can be swept per process without changing
        // the parent's invocation shape.
        if (int.TryParse(Environment.GetEnvironmentVariable("SIMDIMAGING_BAND_ROWS"), out var bandRows) && bandRows > 0)
            Processing.FilterOps.BandRowsOverride = bandRows;

        if (int.TryParse(Environment.GetEnvironmentVariable("SIMDIMAGING_RESIZE_BUDGET"), out var resizeBudget)
            && resizeBudget > 0)
            Processing.Geometry.Resample.ScratchBudgetBytes = resizeBudget;

        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);
        var c = Cases.Build(width, height).First(x => x.Id == caseId);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var baseline = process.WorkingSet64;
        long peak = baseline;
        var gate = new object();
        using var cancel = new CancellationTokenSource();
        var sampler = Task.Run(() =>
        {
            while (!cancel.IsCancellationRequested)
            {
                process.Refresh();
                var current = process.WorkingSet64;
                lock (gate)
                    if (current > peak) peak = current;
                Thread.Sleep(1);
            }
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        object? result = side == "ours" ? c.Ours(ours) : c.Theirs(theirs);
        clock.Stop();
        cancel.Cancel();
        sampler.Wait();
        long rise;
        lock (gate) rise = peak - baseline;
        GC.KeepAlive(result);
        (result as IDisposable)?.Dispose();

        Console.WriteLine($"{rise.ToString(CultureInfo.InvariantCulture)} {clock.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}");
    }

    private static long Median(List<long> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
