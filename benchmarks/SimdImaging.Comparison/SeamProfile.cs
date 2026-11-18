using System.Diagnostics;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// Where a seam carve spends its time, and what it costs in memory — measured on the shipped operator, not on
/// a copy of it.
/// </summary>
/// <remarks>
/// <para>
/// The phases come from <see cref="SeamCarving.ProfilePhases"/>, six counters written once per phase per call
/// and four per seam. Reading them out of a reimplementation would repeat the mistake known-gaps records as
/// rule four: a copy measures how faithful the copy is, and the bokeh episode is what that costs (18.82x to
/// 28.42x for an edit that only made the copy more faithful).
/// </para>
/// <para>
/// Every cell runs in a fresh child process, one call for the cold column and a few for the warm one. The peak
/// is the same sampler <c>peakmem</c> uses: baseline after three collections, 1 ms <c>WorkingSet64</c> samples,
/// rise over baseline. In-process numbers were tried and read 2.5 MiB for an operator holding a 33 MiB plane,
/// because the fixture had already committed the pages.
/// </para>
/// </remarks>
internal static class SeamProfile
{
    private static readonly (int Width, int Height, int Seams)[] Sizes =
    [
        (1920, 1080, 50),
        (3840, 2160, 50),
        (7680, 4320, 10),
    ];

    private const int ColdProcesses = 3;
    private const int WarmRounds = 3;

    /// <summary>One measured call in this process; prints one pipe-separated line.</summary>
    public static void Child(string[] args)
    {
        var warm = args[1] == "warm";
        var axis = args[2] == "horizontal" ? SeamAxis.Horizontal : SeamAxis.Vertical;
        var width = int.Parse(args[3], CultureInfo.InvariantCulture);
        var height = int.Parse(args[4], CultureInfo.InvariantCulture);
        var count = int.Parse(args[5], CultureInfo.InvariantCulture);
        var rounds = warm && args.Length > 6 ? int.Parse(args[6], CultureInfo.InvariantCulture) : 1;

        var data = Fixture.Make(width, height);
        using var source = Fixture.Ours(data, width, height);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var baseline = process.WorkingSet64;
        var liveBefore = GC.GetTotalMemory(true);

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
                {
                    if (current > peak)
                        peak = current;
                }

                Thread.Sleep(1);
            }
        });

        var totals = new List<double>();
        double find = 0, prime = 0, cut = 0, shift = 0, copy = 0, crop = 0;
        string sha = string.Empty;

        for (var round = 0; round < rounds; round++)
        {
            SeamCarving.ResetProfile();
            SeamCarving.ProfilePhases = true;
            var start = Stopwatch.GetTimestamp();
            using var carved = source.Pipe().RemoveSeams(axis, count).ToImage();
            var elapsed = Stopwatch.GetTimestamp() - start;
            SeamCarving.ProfilePhases = false;

            totals.Add(elapsed);
            find += SeamCarving.TicksFind;
            prime += SeamCarving.TicksPrime;
            cut += SeamCarving.TicksCut;
            shift += SeamCarving.TicksShift;
            copy += SeamCarving.TicksCopy;
            crop += SeamCarving.TicksCrop;
            sha = Metrics.Sha256(carved.WritablePixels);
        }

        cancel.Cancel();
        sampler.Wait();

        long rise;
        lock (gate)
            rise = peak - baseline;

        var live = GC.GetTotalMemory(true) - liveBefore;
        var perTick = 1000.0 / Stopwatch.Frequency / rounds;

        Console.WriteLine(string.Join('|',
            (totals.Sum() / rounds * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture),
            (find * perTick).ToString("F3", CultureInfo.InvariantCulture),
            (prime * perTick).ToString("F3", CultureInfo.InvariantCulture),
            (cut * perTick).ToString("F3", CultureInfo.InvariantCulture),
            (shift * perTick).ToString("F3", CultureInfo.InvariantCulture),
            (copy * perTick).ToString("F3", CultureInfo.InvariantCulture),
            (crop * perTick).ToString("F3", CultureInfo.InvariantCulture),
            rise.ToString(CultureInfo.InvariantCulture),
            live.ToString(CultureInfo.InvariantCulture),
            sha));
    }

    /// <summary>表格里的一个单元格：三次冷进程取中位，或一次热进程。</summary>
    private sealed record Cell(double Total, double Find, double Prime, double Cut, double Shift,
        double Copy, double Crop, long Peak, long Live, string Sha);

    public static void Write(Report report)
    {
        report.Heading(2, "Seam Carving 分段剖析");
        report.Line("> 相位来自产品算子自己的 `ProfilePhases` 计数器（每阶段一次、每缝四次），不是复刻件的计时。");
        report.Line("> 每一格都是**全新子进程**：三遍 GC 取基线、1 ms 采样 `WorkingSet64`、冷格只调一次、热格调 "
                  + $"{WarmRounds} 次取中位。峰值 = 相对基线的增量。");
        report.Line("> 水平轴要两次朴素转置，相位会失真，**结论只引用 Vertical**；横向只报总时长。");
        report.Line();

        var coldRows = new List<string[]>();
        var warmRows = new List<string[]>();
        var shas = new Dictionary<string, string>();

        foreach (var (width, height, seams) in Sizes)
        {
            var label = $"{width}×{height}，{seams} 缝";
            Console.WriteLine($"[seamprofile] {label} cold...");
            var cold = MedianOf(Cold(width, height, seams));
            shas[$"{width}x{height}c"] = cold.Sha;

            Console.WriteLine($"[seamprofile] {label} warm...");
            var warm = Warm(width, height, seams);
            shas[$"{width}x{height}w"] = warm.Sha;

            var perSeam = cold.Total / seams;
            coldRows.Add([
                label,
                F(cold.Total), F(cold.Copy), F(cold.Prime), F(cold.Find), F(cold.Cut), F(cold.Shift), F(cold.Crop),
                F(perSeam), MiB(cold.Peak), MiB(cold.Live), cold.Sha[..16]]);

            var sum = warm.Find + warm.Prime + warm.Cut + warm.Shift + warm.Copy + warm.Crop;
            warmRows.Add([
                label, F(warm.Total), F(warm.Total / seams), Percent(warm.Find, sum), Percent(warm.Cut, sum),
                Percent(warm.Shift, sum), Percent(warm.Prime + warm.Copy + warm.Crop, sum),
                (sum / warm.Total * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%",
                MiB(warm.Peak), warm.Sha[..16]]);
        }

        report.Heading(3, "A · 冷启动（全新进程、单次调用，3 支取中位）");
        report.Table(
            ["尺寸", "总 ms", "Copy", "PrimeLuma", "FindSeam", "CutSeam", "ShiftLuma", "Crop", "ms/缝", "峰值 RSS", "调用后 live", "SHA-256"],
            coldRows);

        report.Heading(3, "B · 预热（同进程 " + WarmRounds + " 次中位）");
        report.Table(
            ["尺寸", "总 ms/次", "ms/缝", "FindSeam %", "Cut %", "Shift %", "Prime+Copy+Crop %", "Σ相位/总", "峰值 RSS", "SHA-256"],
            warmRows);

        // Cold and warm must agree within a size; across sizes the results are different images, so comparing
        // them against each other would always report a mismatch.
        var mismatched = Sizes
            .Where(s => shas[$"{s.Width}x{s.Height}c"] != shas[$"{s.Width}x{s.Height}w"])
            .Select(s => $"{s.Width}×{s.Height}")
            .ToList();

        report.Line(mismatched.Count == 0
            ? "**SHA 一致性**：三个尺寸各自冷热一致——同一路径、同一字节。"
            : $"**SHA 一致性**：冷热在 {string.Join("、", mismatched)} 上不一致——**那几格作废**。");
    }

    private static List<Cell> Cold(int width, int height, int seams)
    {
        var cells = new List<Cell>();
        for (var i = 0; i < ColdProcesses; i++)
            cells.Add(Run("cold", width, height, seams, 1));
        return cells;
    }

    private static Cell Warm(int width, int height, int seams) => Run("warm", width, height, seams, WarmRounds);

    /// <summary>Median by total time, carrying that process's other fields with it.</summary>
    private static Cell MedianOf(List<Cell> cells)
    {
        cells.Sort((a, b) => a.Total.CompareTo(b.Total));
        return cells[cells.Count / 2];
    }

    private static Cell Run(string mode, int width, int height, int seams, int rounds)
    {
        var self = typeof(SeamProfile).Assembly.Location;
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        if (!Path.GetFileName(Environment.ProcessPath!)
                .StartsWith("SimdImaging.Comparison", StringComparison.Ordinal))
            psi.ArgumentList.Add(self);

        psi.ArgumentList.Add("seamprofile-child");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("vertical");
        psi.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(height.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(seams.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(rounds.ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"seamprofile child failed: {output}");

        var parts = output.Split('|');
        double P(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
        return new Cell(P(0), P(1), P(2), P(3), P(4), P(5), P(6),
            long.Parse(parts[7], CultureInfo.InvariantCulture),
            long.Parse(parts[8], CultureInfo.InvariantCulture),
            parts[9]);
    }

    private static string F(double ms) => ms.ToString(ms < 10 ? "F3" : "F1", CultureInfo.InvariantCulture);

    private static string Percent(double part, double sum) =>
        (sum > 0 ? part / sum * 100.0 : 0).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static string MiB(long bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);
}
