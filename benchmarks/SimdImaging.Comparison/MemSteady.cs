using System.Diagnostics;
using System.Globalization;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Comparison;

/// <summary>
/// Answers the one question `peakmem` cannot: is the cold-start peak a one-off pool warm-up, or does the
/// resident footprint keep growing with repeated calls?
///
/// <para>Each cell is a FRESH process built exactly like `PeakMemory.Child` (same fixture, both images in
/// the baseline, same ~1 ms working-set sampler). The only variable between cells is how many times the
/// same operator runs before the peak and the post-GC live set are read. Every result is disposed after
/// its call, so retained pool buffers are the only thing that can survive.</para>
///
/// <para>This is a measuring instrument, not an optimisation. It deliberately does not rank the two sides
/// on "who uses less memory" — a cold process, one call, is not a workload — and it changes no product
/// code. What it decides is whether the 8K cold-start deficit is a recurring cost at all.</para>
/// </summary>
internal static class MemSteady
{
    private static readonly string[] CaseIds = ["resize-bicubic", "gaussian-blur", "grayscale", "invert"];
    private static readonly int[] Repeats = [1, 3, 10];
    private const int SamplesPerCell = 3;

    /// <summary>Convergence read-out threshold, not a physical constant: 1 MiB pool granularity plus
    /// GC/working-set sampling noise are well below it.</summary>
    private const long GrowthNoiseMiB = 8;

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, "C3b 稳态内存：冷启动峰值是否随调用次数增长");
        report.Line($"> 输入 {width}×{height} `Rgba8`。**每一格都是一支全新子进程**：与 `peakmem` 完全相同的夹具"
                  + "（同一份确定性图案、两侧图像都在基线内）、GC 收敛后取基线、采样线程每 ~1 ms 读 `WorkingSet64`。");
        report.Line("> 格与格之间**只差一件事**：同一算子连续跑 1 / 3 / 10 次，每次的结果在调用后立刻 `Dispose()`。"
                  + "峰值是相对基线的 `WorkingSet64` 最大增量；`live` 是跑完并强制 GC 后"
                  + "`GC.GetTotalMemory(forceFullCollection: true)` 相对基线的增量（只算托管存活对象）。");
        report.Line("> `live` 里剩下只能是池保留的缓冲：输出图归还 `ArrayPixelPool`（每尺寸最多 2 张）、"
                  + "以及 `[ThreadStatic]` 的 `FloatScratch`。**如果这些是一次性预热，1→10 次不应再涨。**");
        report.Line($"> 每格 {SamplesPerCell} 次独立子进程取中位数。**判定看收敛，不是看 1→10 的总差**："
                  + $"池在前几次调用被填满（预热 Δ 1→3），之后 |Δlive(3−10)| ≤ {GrowthNoiseMiB} MiB 即为收敛"
                  + "（池粒度 1 MiB，噪声远低于此）。");
        report.Line("> ⚠ `live` 是**托管堆**口径（`GC.GetTotalMemory`）：对手实现的大像素缓冲由它自己的分配器管理，"
                  + "该读数恒为 0，**对对手侧无意义**，表中标 `n/a`。两侧内存高低一律以 `peakmem` 的进程级"
                  + "WorkingSet 为准；这里的 `live` 只用来判断**我方**池是否随调用累积。");
        report.Line("> ⚠ 本模式**不回答「谁更省内存」**：冷进程单次调用不是工作负载。"
                  + "它只判定 8K 冷启动峰值差额是否会随调用次数累积；不改任何产品代码。");
        report.Line();

        var rows = new List<string[]>();
        var verdicts = new List<string[]>();

        foreach (var id in CaseIds)
        {
            var liveOurs = new Dictionary<int, long>();
            var workers = new Dictionary<int, long>();

            foreach (var n in Repeats)
            {
                var ours = Median(Measure("ours", id, width, height, n));
                var theirs = Median(Measure("theirs", id, width, height, n));
                liveOurs[n] = ours.Live;
                workers[n] = ours.Workers;

                rows.Add([
                    id, n.ToString(CultureInfo.InvariantCulture),
                    MiB(ours.Peak), MiB(theirs.Peak),
                    MiB(ours.Live), "n/a",
                    ours.Workers.ToString(CultureInfo.InvariantCulture),
                ]);
            }

            var warmup = liveOurs[3] - liveOurs[1];
            var settle = liveOurs[10] - liveOurs[3];
            verdicts.Add([
                id,
                MiB(liveOurs[1]), MiB(liveOurs[3]), MiB(liveOurs[10]),
                SignedMiB(warmup), SignedMiB(settle),
                workers[10].ToString(CultureInfo.InvariantCulture),
                Classify(settle, warmup),
            ]);
        }

        report.Table(["场景", "调用次数", "我方峰值 MiB", "对手峰值 MiB", "我方 live MiB", "对手 live MiB",
            "我方 workers"], rows);
        report.Line();
        report.Heading(3, "判定：池是否收敛（看 3→10，不是 1→10）");
        report.Table(["场景", "我方 live@1", "@3", "@10", "预热 Δ(1→3)", "收敛 Δ(3→10)",
            "我方 workers@10", "判定"], verdicts);
        report.Line("> `workers` 是 `RowParallel.LastWorkerCount` 的实测值，即 `ForBands` / `For` 的**计划** worker 数"
                  + "（不等于实际被调度到的线程数；对手侧不暴露该数字）。它修正了此前「~11 套 scratch」的推算："
                  + "计划值是 16，因此单套大小不能简单用峰值 ÷ workers 得出。");
    }

    private static string Classify(long settle, long warmup)
    {
        var noise = GrowthNoiseMiB * 1048576L;
        if (Math.Abs(settle) <= noise)
            return $"3 次后收敛（预热 {MiB(warmup)} MiB），不随调用增长";
        return settle > 0 ? "3→10 仍在增长，需定位" : "3→10 下降（回收），非增长";
    }

    private static string MiB(long bytes) =>
        (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);

    private static string SignedMiB(long bytes) =>
        (bytes >= 0 ? "+" : "") + MiB(bytes);

    private readonly record struct Sample(long Peak, long Live, long Workers);

    private static Sample Median(List<Sample> values)
    {
        values.Sort((a, b) => a.Peak.CompareTo(b.Peak));
        return values[values.Count / 2];
    }

    private static List<Sample> Measure(string side, string caseId, int width, int height, int repeats)
    {
        var values = new List<Sample>();
        var self = typeof(MemSteady).Assembly.Location;
        for (var i = 0; i < SamplesPerCell; i++)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            if (!Path.GetFileName(Environment.ProcessPath!)
                    .StartsWith("SimdImaging.Comparison", StringComparison.Ordinal))
                psi.ArgumentList.Add(self);
            psi.ArgumentList.Add("memsteady-child");
            psi.ArgumentList.Add(side);
            psi.ArgumentList.Add(caseId);
            psi.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(height.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(repeats.ToString(CultureInfo.InvariantCulture));

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"memsteady child failed ({side} {caseId} x{repeats}): {output}");

            var parts = output.Split('\n').Last().Trim().Split(' ');
            values.Add(new Sample(
                long.Parse(parts[0], CultureInfo.InvariantCulture),
                long.Parse(parts[1], CultureInfo.InvariantCulture),
                long.Parse(parts[2], CultureInfo.InvariantCulture)));
        }

        return values;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public static void Child(string[] args)
    {
        var side = args[1];
        var caseId = args[2];
        var width = int.Parse(args[3], CultureInfo.InvariantCulture);
        var height = int.Parse(args[4], CultureInfo.InvariantCulture);
        var repeats = int.Parse(args[5], CultureInfo.InvariantCulture);

        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);
        var c = Cases.Build(width, height).First(x => x.Id == caseId);

        Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var baselineWs = process.WorkingSet64;
        var baselineLive = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(data);

        long peak = baselineWs;
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

        long workers = 0;
        for (var i = 0; i < repeats; i++)
        {
            object? result = side == "ours" ? c.Ours(ours) : c.Theirs(theirs);
            if (side == "ours") workers = RowParallel.LastWorkerCount;
            GC.KeepAlive(result);
            (result as IDisposable)?.Dispose();
        }

        cancel.Cancel();
        sampler.Wait();
        long peakRise;
        lock (gate) peakRise = peak - baselineWs;

        Collect();
        var liveRise = GC.GetTotalMemory(forceFullCollection: true) - baselineLive;

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{peakRise} {liveRise} {workers}"));
    }
}
