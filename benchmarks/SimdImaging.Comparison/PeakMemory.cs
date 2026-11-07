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
            values.Add(long.Parse(output.Split('\n').Last().Trim(), CultureInfo.InvariantCulture));
        }

        return values;
    }

    public static void Child(string[] args)
    {
        var side = args[1];
        var caseId = args[2];
        var width = int.Parse(args[3], CultureInfo.InvariantCulture);
        var height = int.Parse(args[4], CultureInfo.InvariantCulture);

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

        object? result = side == "ours" ? c.Ours(ours) : c.Theirs(theirs);
        cancel.Cancel();
        sampler.Wait();
        long rise;
        lock (gate) rise = peak - baseline;
        GC.KeepAlive(result);
        (result as IDisposable)?.Dispose();

        Console.WriteLine(rise.ToString(CultureInfo.InvariantCulture));
    }

    private static long Median(List<long> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
