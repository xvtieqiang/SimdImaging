using System.Diagnostics;
using System.Globalization;
using SimdImaging.Processing.Internal;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>
/// The honest head-to-head: a ratio measured by interleaving both sides inside one isolated process, plus a
/// separate measurement of how many cores each side actually used.
///
/// <para>Two earlier attempts at this were wrong, and both failures are worth remembering:</para>
/// <list type="bullet">
/// <item>Measuring our single-threaded path against their default inside the main process produced a "we are
/// 4× behind per core" conclusion that was really thread-pool residue: <c>TotalProcessorTime</c> is
/// process-wide, so our just-finished parallel work kept burning CPU during their turn. The same operator read
/// 1.22 cores in one run and 7.86 in another, and one case printed 120×.</item>
/// <item>Fixing that by giving each side its own process fixed the core count but broke the ratio: each side
/// then ran alone, so warm-up drift landed on the comparison and the per-operator ratio moved between 0.63 and
/// 1.74 across three identical runs.</item>
/// </list>
///
/// <para>So: <b>wall-clock ratio is measured with both sides interleaved in one child</b> (interleaving is what
/// keeps warm-up equal), and <b>core count is measured with one side per child</b> (that is the number a
/// process-wide counter corrupts). Neither number is taken from the other's run.</para>
/// </summary>
internal static class Cores
{
    private const int TargetMs = 500;
    private const int RatioRounds = 21;

    private static readonly string[] Ids =
        ["grayscale", "sepia", "invert", "contrast", "box-blur", "gaussian-blur", "resize-nearest"];

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, $"同条件对比与核数（{width}×{height}）");
        report.Line("> `我方 ms` / `对手 ms` / `直接比`：**同一支子进程内交错 21 轮取最小值**（干扰只单向拉长，最小值最干净），两侧热身条件相同。");
        report.Line("> `核数`：另一支子进程只跑那一侧，用 CPU 时间 ÷ 墙钟时间得出（至少 500 ms，"
                  + "Windows 的 CPU 计时粒度约 15.6 ms）。");
        report.Line("> `同核数比` = 我方强制单线程 ÷ 对手；**大于 1 表示我们更慢**。");
        report.Line();

        var rows = new List<string[]>();
        var ratios = new List<double>();
        var perCore = new List<double>();

        foreach (var id in Ids)
        {
            var both = Measure("both", id, width, height);
            var oursCores = Measure("ours", id, width, height).Cores;
            var theirCores = Measure("theirs", id, width, height).Cores;
            var oursSingle = Measure("ours-single", id, width, height).MsPerRun;
            var theirsSingle = Measure("theirs", id, width, height).MsPerRun;

            var direct = both.OursMs / both.TheirsMs;
            var perCoreRatio = oursSingle / theirsSingle;
            ratios.Add(direct);
            perCore.Add(perCoreRatio);

            rows.Add([id,
                Format(both.OursMs), Format(both.TheirsMs),
                Ratio(direct) + (direct > 1 ? " ✅" : " ❌"),
                Format(oursCores), Format(theirCores),
                Ratio(perCoreRatio),
                perCoreRatio > 1.2 ? "**同核落后**" : perCoreRatio < 0.8 ? "同核领先" : "同核持平"]);
        }

        report.Table(["场景", "我方 ms", "对手 ms", "直接比", "我方核数", "对手核数", "同核数比", "判定"], rows);
        report.Line($"- 直接比几何平均：**{Math.Exp(ratios.Average(Math.Log)):F2}×**"
                  + $"（{ratios.Count(r => r < 1)} / {ratios.Count} 项领先；<1 表示我方更快）。");
        report.Line($"- 同核数比几何平均：{Math.Exp(perCore.Average(Math.Log)):F2}×（>1 我方慢）。");
        report.Line();
    }

    /// <summary>Child entry point. Prints <c>a b runs</c>; meaning of a/b depends on the side.</summary>
    public static void Child(string[] args)
    {
        var side = args[1];
        var caseId = args[2];
        var width = int.Parse(args[3], CultureInfo.InvariantCulture);
        var height = int.Parse(args[4], CultureInfo.InvariantCulture);

        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);
        var testCase = Cases.Build(width, height).First(x => x.Id == caseId);

        if (side == "both")
        {
            RatioChild(testCase, ours, theirs);
            return;
        }

        Action run = side switch
        {
            "ours" => () => { RowParallel.DegreeOverride = 0; using var r = testCase.Ours(ours); },
            "ours-single" => () => { RowParallel.DegreeOverride = 1; using var r = testCase.Ours(ours); },
            "theirs" => () => { using var r = testCase.Theirs(theirs); },
            _ => throw new ArgumentException($"unknown side {side}"),
        };

        for (var warm = 0; warm < 3; warm++)
            run();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;

        var watch = Stopwatch.StartNew();
        var calls = 0;
        do
        {
            run();
            calls++;
        }
        while (watch.Elapsed.TotalMilliseconds < TargetMs);
        watch.Stop();

        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{watch.Elapsed.TotalMilliseconds / Math.Max(1, calls):R} {cpu / Math.Max(1e-9, watch.Elapsed.TotalMilliseconds):R} {calls}"));
    }

    private static void RatioChild(Case testCase, SimdImaging.Image ours, TheirImage theirs)
    {
        for (var warm = 0; warm < 3; warm++)
        {
            RowParallel.DegreeOverride = 0;
            using (var w = testCase.Ours(ours)) { }
            using (var w = testCase.Theirs(theirs)) { }
        }

        var ourTimes = new List<double>(RatioRounds);
        var theirTimes = new List<double>(RatioRounds);

        for (var round = 0; round < RatioRounds; round++)
        {
            var watchOurs = Stopwatch.StartNew();
            RowParallel.DegreeOverride = 0;
            using (var r = testCase.Ours(ours)) { }
            watchOurs.Stop();
            ourTimes.Add(watchOurs.Elapsed.TotalMilliseconds);

            var watchTheirs = Stopwatch.StartNew();
            using (var r = testCase.Theirs(theirs)) { }
            watchTheirs.Stop();
            theirTimes.Add(watchTheirs.Elapsed.TotalMilliseconds);

            // Let both sides' workers drain before the next round, so a round does not inherit the previous
            // round's queue. Cheap next to a 20 ms operation and it removes most of the ordering bias.
            Thread.Sleep(3);
        }

        // Report the MINIMUM, not the median. Interference between two parallel workloads is one-sided: our
        // 9–12 workers are still draining when their turn starts, which inflated their median to 13–14 ms
        // against 4.5–7 ms when measured alone. A minimum is the run least touched by the other side.
        ourTimes.Sort();
        theirTimes.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{ourTimes[0]:R} {theirTimes[0]:R} {RatioRounds}"));
    }

    private static (double MsPerRun, double Cores, double OursMs, double TheirsMs) Measure(string side, string caseId,
        int width, int height)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        // `dotnet run` launches through dotnet.exe and needs the dll as the first argument; a published
        // self-contained exe already has an entry point and must not get one.
        if (!Path.GetFileName(Environment.ProcessPath!)
                .StartsWith("SimdImaging.Comparison", StringComparison.Ordinal))
            psi.ArgumentList.Add(typeof(Cores).Assembly.Location);

        psi.ArgumentList.Add("cores-child");
        psi.ArgumentList.Add(side);
        psi.ArgumentList.Add(caseId);
        psi.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(height.ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"cores child failed ({side} {caseId}): {output}");

        var parts = output.Split('\n').Last().Trim().Split(' ');
        var first = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var second = double.Parse(parts[1], CultureInfo.InvariantCulture);

        return side == "both"
            ? (0, 0, first, second)
            : (first, second, 0, 0);
    }

    private static string Format(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Ratio(double value) => value.ToString("F2", CultureInfo.InvariantCulture) + "×";
}
