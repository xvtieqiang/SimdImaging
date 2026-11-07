using System.Diagnostics;
using System.Globalization;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Comparison;

/// <summary>
/// Are we winning because the kernel is faster, or because we use more cores?
///
/// <para><see cref="RowParallel.For"/> gates on work and caps workers at one per 32 rows, so at 640×360 it
/// already runs 12 workers. The question this section answers is not "is the gate right" but "what happens when
/// the parallel win is taken away" — and, separately, "does the oracle use more than one core", because if it
/// does not, then the headline ratio is many-cores against one and saying otherwise would be misleading.</para>
///
/// <para>Both sides are the same operator; only <c>DegreeOverride</c> (1 vs default) differs. That is the
/// project's own hook, so no part of the code under test is re-implemented for the sake of a number.</para>
/// </summary>
internal static class ParallelOverhead
{
    private static readonly string[] Ids =
        ["grayscale", "sepia", "invert", "contrast", "box-blur", "gaussian-blur", "resize-nearest"];

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, $"并行收益（{width}×{height}，同进程）");
        report.Line("> ⚠ **本节的「每核」与「平均并发度」两列不可信，已被 `cores` 模式取代。**"
                  + "`TotalProcessorTime` 是进程级的，我方并行留下的线程池线程会污染对手的测量"
                  + "（同一算子两次读到 1.22 核和 7.86 核）。要核数结论请跑 `cores`。");
        report.Line("> 本节仍可信的部分：`并行收益`（我方单线程 ÷ 我方默认，两侧都是我们自己的代码）。");
        report.Line("> 我方单线程 = `DegreeOverride = 1`；我方默认 = 按工作量自动；对手 = ImageSharp 默认。");
        report.Line("> `并行收益` = 我方单线程 ÷ 我方默认，越大说明并行赚得越多。");
        report.Line("> `我方单线程 ÷ 对手` 把并行从我们这侧剥掉，只看每核：**大于 1 表示我们更慢**。");
        report.Line();

        var cases = Cases.Build(width, height);
        var data = Fixture.Make(width, height);
        using var source = Fixture.Ours(data, width, height);
        using var theirSource = Fixture.Theirs(data, width, height);

        var rows = new List<string[]>();

        foreach (var id in Ids)
        {
            var testCase = cases.FirstOrDefault(c => c.Id == id);
            if (testCase is null)
                continue;

            var runners = new[]
            {
                new Runner("theirs", () => { using var r = testCase.Theirs(theirSource); GC.KeepAlive(r); }),
                new Runner("single", SingleThread(testCase, source)),
                new Runner("auto", () =>
                {
                    RowParallel.DegreeOverride = 0;
                    using var r = testCase.Ours(source);
                    GC.KeepAlive(r);
                }),
            };

            var times = Timing.Time(runners, rounds: 7);
            var parallelGain = times["single"] / times["auto"];
            var perCore = times["single"] / times["theirs"];

            string workers;
            RowParallel.DegreeOverride = 0;
            using (var probe = testCase.Ours(source))
                workers = RowParallel.LastWorkerCount.ToString(CultureInfo.InvariantCulture);

            rows.Add([id, Timing.F(times["single"]), Timing.F(times["auto"]), Timing.F(times["theirs"]),
                Ratio(parallelGain), Ratio(perCore), workers,
                perCore > 1.2 ? "**每核落后**" : perCore < 0.8 ? "每核领先" : "每核持平"]);
        }

        report.Table(["场景", "我方单线程 ms", "我方默认 ms", "对手默认 ms", "并行收益", "我方单线程 ÷ 对手", "worker", "判定"], rows);
        report.Line("> `worker` 是我方默认配置下 `LastWorkerCount` 的实际值。");
        report.Line();

        // The follow-up question this table always raises: are both sides using the same number of cores?
        // CPU-time over wall-time answers it without guessing. 1.0 means one thread did all the work.
        report.Heading(3, "平均并发度（CPU 时间 ÷ 墙钟时间）");
        report.Line("> 1.0 = 单线程在跑；明显大于 1 说明确实用了多个核。");
        report.Line("> 若对手这一列接近 1.0，则「我方默认 ÷ 对手」本质上是**多核对单核**，"
                  + "「我方单线程 ÷ 对手」才是同核数对比。两个都要报。");
        report.Line();

        var concurrency = new List<string[]>();
        foreach (var id in new[] { "grayscale", "gaussian-blur", "resize-nearest" })
        {
            var testCase = cases.FirstOrDefault(c => c.Id == id);
            if (testCase is null)
                continue;

            var auto = Concurrency(() =>
            {
                RowParallel.DegreeOverride = 0;
                using var r = testCase.Ours(source);
            });
            var single = Concurrency(() => SingleThread(testCase, source)());
            var theirs = Concurrency(() => { using var r = testCase.Theirs(theirSource); });

            concurrency.Add([id, Ratio(auto), Ratio(single), Ratio(theirs)]);
        }

        report.Table(["场景", "我方默认 平均核数", "我方单线程 平均核数", "对手 平均核数"], concurrency);
        report.Line("> ⚠ **这一列只作方向性参考，不能当结论。** `TotalProcessorTime` 是进程级的，"
                  + "我方并行调用结束后线程池里的线程还会活跃一小段时间，会污染紧接着的对手测量"
                  + "（同一算子两次分别测出 1.22 和 7.86 核）。要拿到干净数字必须走独立子进程。");
        report.Line("> 可信的是第 2 段那两列：我方单线程 `1.0×` 左右（serial 路径，自证），"
                  + "以及并行收益 4–5×。");
        report.Line();

        // The matrix pack is already vectorised, but it resolves each pixel's contribution through a table
        // lookup. For a plain 3×3 colour matrix that may cost more than the arithmetic it replaces; the repo
        // ships a hook for exactly this comparison, so use it rather than guess.
        report.Heading(3, "矩阵打包：向量 vs 标量（同进程）");
        report.Line("> `PointLut.ForceScalarMatrixPack` 是仓库自带的测试钩子，两行是同一个算子。");
        report.Line();

        var packRows = new List<string[]>();
        foreach (var id in new[] { "grayscale", "sepia", "contrast" })
        {
            var testCase = cases.FirstOrDefault(c => c.Id == id);
            if (testCase is null)
                continue;

            var packRunners = new[]
            {
                new Runner("vector", () =>
                {
                    PointLut.ForceScalarMatrixPack = false;
                    using var r = testCase.Ours(source);
                    GC.KeepAlive(r);
                }),
                new Runner("scalar", () =>
                {
                    PointLut.ForceScalarMatrixPack = true;
                    try { using var r = testCase.Ours(source); GC.KeepAlive(r); }
                    finally { PointLut.ForceScalarMatrixPack = false; }
                }),
            };

            var packTimes = Timing.Time(packRunners, rounds: 7);
            packRows.Add([id, Timing.F(packTimes["vector"]), Timing.F(packTimes["scalar"]),
                Ratio(packTimes["vector"] / packTimes["scalar"]),
                packTimes["scalar"] < packTimes["vector"] ? "**标量更快**" : "向量更快"]);
        }

        report.Table(["场景", "向量 ms", "标量 ms", "向量 ÷ 标量", "判定"], packRows);
        report.Line("> 这一列 >1 表示向量打包反而是负担——若如此，简单颜色矩阵应当绕开查表直接算。");
        report.Line();
    }

    private static Action SingleThread(Case testCase, SimdImaging.Image source) => () =>
    {
        RowParallel.DegreeOverride = 1;
        try
        {
            using var r = testCase.Ours(source);
            GC.KeepAlive(r);
        }
        finally
        {
            RowParallel.DegreeOverride = 0;
        }
    };

    /// <summary>Average cores in use: CPU milliseconds per wall millisecond over repeated runs.</summary>
    private static double Concurrency(Action action)
    {
        for (var warm = 0; warm < 3; warm++)
            action();

        // Time-boxed rather than a fixed iteration count: a sub-millisecond operator over eight runs produces
        // a CPU-time delta lost in the process's own bookkeeping (the first version reported 0.00 for
        // resize-nearest and 120 cores for the oracle). Keep going until there is enough wall time to divide.
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        do
        {
            action();
        }
        while (watch.Elapsed.TotalMilliseconds < 60);
        watch.Stop();
        process.Refresh();

        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        return watch.Elapsed.TotalMilliseconds <= 0 ? 0 : cpu / watch.Elapsed.TotalMilliseconds;
    }

    private static string Ratio(double value) => value.ToString("F2", CultureInfo.InvariantCulture) + "×";
}
