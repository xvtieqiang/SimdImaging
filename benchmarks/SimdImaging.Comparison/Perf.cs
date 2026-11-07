using System.Diagnostics;
using System.Globalization;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>C2 end-to-end throughput and C3 allocations / peak working set. Same-process A/B,
/// interleaved rounds, medians — the SimdCodecs rule. A kernel that is faster in isolation does not count.</summary>
internal static class Perf
{
    public static List<(string Id, double Ratio)> Write(Report report, int width, int height)
    {
        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);
        var cases = Cases.Build(width, height);

        report.Heading(2, "C 组 · 运行时与工程");
        report.Heading(3, "C2/C3 端到端同进程 A/B");
        report.Line($"> 输入：{width}×{height} `Rgba8`（{width * height / 1_000_000.0:F2} MPix），自生成确定性图案。");
        report.Line("> 口径：两边都**从共享源产生一张新图**（对手用 `Clone(ctx => ...)`，我方用算子返回新图），含输出缓冲分配；对手 `MaxDegreeOfParallelism=1`，我方单线程。");
        report.Line("> **不做计时外预热**。每个场景先单独计时**第一次调用**（cold，含 JIT 分层编译 / 动态 PGO / 大对象页提交），再按 SimdCodecs 规矩热身 ≥300 ms、7 轮交错采样取稳态中位数（steady）。");
        report.Line("> 两笔账**各算各的、不互相抵消**：cold 是各自首次调用的完整成本（含各自预热），steady 是算子内核的真实吞吐。**「超越」判定一律以 steady 为准** —— 对手初始化慢不等于我方算子快。");
        report.Line($"> 对手 `MaxDegreeOfParallelism = {Fixture.OracleConfiguration.MaxDegreeOfParallelism}`"
                  + "（1 = SimdCodecs 单线程口径；>1 = 放开对手真实能力）。我方算子单线程。");
        report.Line("> 加速比 = 对手 ms ÷ 我方 ms，>1 表示我方更快。分配为 `GC.GetTotalAllocatedBytes` 单次操作净值（**进程级**，含线程池线程；两边交替采样，背景噪声对称）。");
        report.Line();

        var rows = new List<string[]>();
        var perCaseRatios = new List<(string Id, double Ratio)>();
        var peakCases = new List<(string Id, long Ours, long Theirs)>();
        var steadyRatios = new List<double>();
        var coldRatios = new List<double>();
        foreach (var c in cases)
        {
            var allocated = new Dictionary<string, long>();
            var runners = new[]
            {
                new Runner("ours", () => { using var r = c.Ours(ours); GC.KeepAlive(r); }),
                new Runner("theirs", () => { using var r = c.Theirs(theirs); GC.KeepAlive(r); }),
            };

            // First-ever call for this operator path, timed in full. No warm-up is hidden here.
            var coldOurs = TimeOnce(runners[0].Run);
            var coldTheirs = TimeOnce(runners[1].Run);

            Dictionary<string, double> times;
            try { times = Timing.Time(runners, allocated: allocated); }
            catch (Exception e)
            {
                rows.Add([c.Id, c.Description, "—", "—", "—", "—", "—", "—", "—", "—", "—", "失败: " + e.GetType().Name]);
                continue;
            }

            var ourMs = times["ours"];
            var theirMs = times["theirs"];
            var ourMp = c.OutputPixels / 1_000_000.0 / (ourMs / 1000.0);
            var theirMp = c.OutputPixels / 1_000_000.0 / (theirMs / 1000.0);
            allocated.TryGetValue("ours", out var ourAlloc);
            allocated.TryGetValue("theirs", out var theirAlloc);
            steadyRatios.Add(theirMs / ourMs);
            coldRatios.Add(coldTheirs / coldOurs);

            perCaseRatios.Add((c.Id, theirMs / ourMs));

            rows.Add([c.Id, c.Description,
                Timing.F(coldOurs), Timing.F(coldTheirs),
                Timing.F(ourMs), Timing.F(theirMs),
                ourMp.ToString("F1", CultureInfo.InvariantCulture), theirMp.ToString("F1", CultureInfo.InvariantCulture),
                Timing.Ratio(ourMs, theirMs),
                (ourAlloc / 1024.0).ToString("F0", CultureInfo.InvariantCulture),
                (theirAlloc / 1024.0).ToString("F0", CultureInfo.InvariantCulture),
                ourMs < theirMs ? "我方快" : "对手快"]);

            if (c.Id is "resize-bicubic" or "gaussian-blur" or "crop" or "grayscale")
            {
                var ourPeak = MeasurePeak(() => { using var r = c.Ours(ours); GC.KeepAlive(r); });
                var theirPeak = MeasurePeak(() => { using var r = c.Theirs(theirs); GC.KeepAlive(r); });
                peakCases.Add((c.Id, ourPeak, theirPeak));
            }
        }

        report.Table(["场景", "语义", "首次我方 ms", "首次对手 ms", "稳态我方 ms", "稳态对手 ms",
            "我方 MP/s", "对手 MP/s", "稳态加速比", "我方 KiB/op", "对手 KiB/op", "胜负"], rows);
        report.Line($"**超越判定（steady）：{GeometricMean(steadyRatios):F4}×**"
                  + $"（{steadyRatios.Count(r => r > 1)} / {steadyRatios.Count} 项领先）；"
                  + $"cold 单独记账：**{GeometricMean(coldRatios):F4}×**。");
        report.Line("> cold 只回答「第一次调用要付多少」，**不参与超越判定**；两边各自的预热成本各算各的，不能拿对手初始化慢来抵算子慢。");
        report.Line("> ⚠ **单一分辨率的结论不算定论**：同一份代码在 1080p 与 4K 的名次会翻转"
                  + "（`resize-nearest` 实测 1080p 0.84× / 4K 1.40×），因为 1080p 更容易被每次调用的固定开销主导。"
                  + "对外引用前必须两个尺寸都跑过。");
        report.Line();

        report.Heading(3, "C3 峰值工作集（同进程采样，仅作方向性参考）");
        report.Line("> 计时与内存测量分开跑。采样线程每 ~1 ms 读一次 `WorkingSet64`，取相对基线的峰值增量，3 次取最大。");
        report.Line("> **正式口径是 `peakmem` 模式的独立子进程冷启动测量**；这里同进程采样会被两边已预热的池污染，只作横向参考，不用于胜负结论。");
        report.Line();
        report.Table(["场景", "我方峰值增量 MiB", "对手峰值增量 MiB", "比值（对手÷我方）"],
            peakCases.Select(p => new[]
            {
                p.Id,
                (p.Ours / 1048576.0).ToString("F1", CultureInfo.InvariantCulture),
                (p.Theirs / 1048576.0).ToString("F1", CultureInfo.InvariantCulture),
                p.Ours == 0 ? "—" : (p.Theirs / (double)p.Ours).ToString("F2", CultureInfo.InvariantCulture) + "×",
            }));

        report.Heading(3, "C4 交付事实（反射与文件实测）");
        var ourAsm = typeof(OurImage).Assembly;
        var theirAsm = typeof(SixLabors.ImageSharp.Image).Assembly;
        var ourTfm = ourAsm.GetCustomAttributes(false).OfType<System.Runtime.Versioning.TargetFrameworkAttribute>()
            .FirstOrDefault()?.FrameworkName ?? "—";
        var theirTfm = theirAsm.GetCustomAttributes(false).OfType<System.Runtime.Versioning.TargetFrameworkAttribute>()
            .FirstOrDefault()?.FrameworkName ?? "—";
        report.Table(["项", "ImageSharp 4.1.3", "SimdImaging"],
        [
            ["目标框架", theirTfm, ourTfm],
            ["程序集文件", theirAsm.Location, ourAsm.Location],
            ["程序集大小", new FileInfo(theirAsm.Location).Length.ToString("N0") + " B",
                new FileInfo(ourAsm.Location).Length.ToString("N0") + " B"],
            ["许可", "Six Labors Split License 1.0（构建期校验密钥）", "MIT"],
            ["NativeAOT", "4.1 为 ICO/CUR/QOI 补 AOT seeds", "全库 `IsAotCompatible` 构建门禁"],
        ]);

        return perCaseRatios;
    }

    private static double TimeOnce(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double GeometricMean(List<double> values) =>
        Math.Exp(values.Average(Math.Log));

    /// <summary>Peak working-set rise while one operation runs, sampled from a helper thread.
    /// Kept out of the timing loop so the sampler never contaminates a median.</summary>
    private static long MeasurePeak(Action run, int repeats = 3)
    {
        using var process = Process.GetCurrentProcess();
        long peak = 0;
        for (var i = 0; i < repeats; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            var baseline = process.WorkingSet64;
            long localPeak = baseline;
            var gate = new object();
            using var cancel = new CancellationTokenSource();
            var sampler = Task.Run(() =>
            {
                while (!cancel.IsCancellationRequested)
                {
                    process.Refresh();
                    var current = process.WorkingSet64;
                    lock (gate) if (current > localPeak) localPeak = current;
                    Thread.Sleep(1);
                }
            });
            run();
            cancel.Cancel();
            sampler.Wait();
            lock (gate) peak = Math.Max(peak, localPeak - baseline);
        }

        return peak;
    }
}
