using System.Diagnostics;
using System.Globalization;
using SimdImaging;
using SimdImaging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>
/// The file-level path: PNG in, PNG out, three ways. Our transcode streams decoded rows through the plan and straight
/// into the encoder; our frame path loads, processes, saves; the oracle loads, mutates, saves.
/// <para>
/// This is the one comparison the in-memory modes cannot make. <c>perfpipe</c> and <c>randomchain</c> both start from
/// an already-decoded image and end with a new buffer, so they never pay — or save — the cost of holding a whole frame
/// (or not). Here the interesting column is peak resident growth, measured in a fresh process per cell, and the honest
/// question is whether streaming the rows is worth what it costs per row.
/// </para>
/// <para>
/// Parity is checked between our own two paths, byte for byte, before anything is timed. The oracle is not compared
/// that way: its <c>GaussianBlur</c> and <c>Contrast</c> already differ by ≤1 LSB at the edges, so a byte comparison
/// there would be a fixture, not a finding.
/// </para>
/// </summary>
internal static class Transcode
{
    /// <summary>The chains, expressed once so the timed table and the fresh-process children run the same work.</summary>
    private static (string Name, string Chain, Func<ImageTranscode, ImageTranscode> Stream,
        Func<OurImage, OurImage> Whole, Action<TheirImage> Oracle)[] Cases() =>
    [
        // No operators: the floor is the codec pair plus the copies, and it is what streaming has to beat before a
        // chain is worth discussing.
        ("copy", "（无算子：解码 + 编码）",
            t => t,
            image => image.Clone(),
            _ => { }),

        ("point-3", "Invert → Contrast 1.1 → Grayscale",
            t => t.Invert().Contrast(1.1f).Grayscale(),
            image => image.Pipe().Invert().Contrast(1.1f).Grayscale().ToImage(),
            image => image.Mutate(x => x.Invert().Contrast(1.1f).Grayscale(GrayscaleMode.Bt601))),

        // A halo step: the streaming window is band + 2 × 5 rows, so this is where "no whole frame" either pays for
        // itself or does not.
        ("blur-invert", "Gaussian σ1.5 → Invert",
            t => t.GaussianBlur(1.5f).Invert(),
            image => image.Pipe().GaussianBlur(1.5f).Invert().ToImage(),
            image => image.Mutate(x => x.GaussianBlur(1.5f).Invert())),
    ];

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, "T 组 · 文件级转码（PNG → PNG，流式文件路径）");
        report.Line($"> 输入是 {width}×{height} `Rgba8` 的临时 PNG，三侧都是「文件进、文件出」：");
        report.Line("> 我方 `ImageTranscode`（行流式，源不整图驻留）、我方 `Load → Pipe → Save`（整图）、");
        report.Line("> 对手 `Load → Mutate → Save`。比值 = 对手 ÷ 我方，>1 表示我方更快。");
        report.Line("> 我方流式先与整图**逐字节比对**；对手侧不参与字节比对（滤波与对比度在边缘本就有 ≤1 LSB 差）。");
        report.Line();

        var root = Directory.CreateTempSubdirectory("simd-transcode-");
        try
        {
            var input = Path.Combine(root.FullName, "in.png");
            var streamedPath = Path.Combine(root.FullName, "streamed.png");
            var framePath = Path.Combine(root.FullName, "frame.png");
            var oraclePath = Path.Combine(root.FullName, "oracle.png");

            using (var source = Fixture.Ours(Fixture.Make(width, height), width, height))
                OurImage.Save(source, input);

            var cases = Cases();

            report.Heading(3, "时间与分配（同进程交错）");
            var rows = new List<string[]>();
            double streamLog = 0;
            double frameLog = 0;
            var parity = 0;

            foreach (var testCase in cases)
            {
                var runners = new[]
                {
                    new Runner("stream", () => Run("stream", testCase, input, streamedPath)),
                    new Runner("frame", () => Run("frame", testCase, input, framePath)),
                    new Runner("theirs", () => Run("theirs", testCase, input, oraclePath)),
                };

                var mode = Run("stream", testCase, input, streamedPath);
                Run("frame", testCase, input, framePath);
                if (File.ReadAllBytes(streamedPath).AsSpan().SequenceEqual(File.ReadAllBytes(framePath)))
                    parity++;

                var allocated = new Dictionary<string, long>();
                var times = Timing.Time(runners, rounds: 5, allocated: allocated);
                var streamRatio = times["theirs"] / times["stream"];
                var frameRatio = times["theirs"] / times["frame"];
                streamLog += Math.Log(streamRatio);
                frameLog += Math.Log(frameRatio);

                rows.Add([testCase.Name, testCase.Chain, Timing.F(times["stream"]), Timing.F(times["frame"]),
                    Timing.F(times["theirs"]),
                    streamRatio.ToString("F2", CultureInfo.InvariantCulture) + "×",
                    frameRatio.ToString("F2", CultureInfo.InvariantCulture) + "×",
                    MiB(allocated["stream"]), MiB(allocated["frame"]), MiB(allocated["theirs"]),
                    $"{mode.Mode}{(mode.FullyStreamed ? " (全链路)" : string.Empty)}"]);
            }

            report.Table(
                ["场景", "链路", "流式 ms", "整图 ms", "对手 ms", "对手÷流式", "对手÷整图",
                    "流式分配/op", "整图分配/op", "对手分配/op", "流式模式"],
                rows);
            report.Line($"**流式对对手：{GeometricMean(streamLog, cases.Length)}**；"
                + $"整图对对手：{GeometricMean(frameLog, cases.Length)}（几何平均，{cases.Length} 场景）。");
            report.Line($"> 我方流式 vs 整图逐字节一致：**{parity}/{cases.Length}**。"
                + "「全链路」= 计划、解码器、编码器三者都按行工作；只有计划流式时 PNG 编码器仍会把行攒成整帧。");
            report.Line("> 分配是 `GC.GetTotalAllocatedBytes`（进程级）；我方流式的窗口来自池，稳态只剩池未命中时的首次触碰。");
            report.Line();

            WritePeak(report, cases, input, streamedPath, framePath, oraclePath, width, height);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Peak resident growth, one fresh process per cell. In-process sampling cannot answer this question: after the
    /// first run the pools are warm, so the second run's growth is whatever the allocator did not already commit —
    /// measured here at 48 KiB for a path that allocates 33 MiB per call. A child that starts cold pays for what it
    /// needs and nothing else, and the number is the same one <c>peakmem</c> reports.
    /// </summary>
    private static void WritePeak(Report report,
        (string Name, string Chain, Func<ImageTranscode, ImageTranscode> Stream, Func<OurImage, OurImage> Whole,
            Action<TheirImage> Oracle)[] cases,
        string input, string streamedPath, string framePath, string oraclePath, int width, int height)
    {
        report.Heading(3, "峰值常驻增长（独立子进程，冷启动）");
        report.Line("> 每格起一个全新进程：打开输入 → GC 收敛 → 记基线 → 每 ~1 ms 读 `WorkingSet64` → 跑**一次** → 取峰值增量；3 次取中位。");
        report.Line("> 三侧都没有池预热优势，也都把 JIT 与首次触碰算在内——这是「一台冷机器跑一次转码要多大内存」的口径。");
        report.Line();

        var rows = new List<string[]>();
        long streamSum = 0, frameSum = 0, theirsSum = 0;
        foreach (var testCase in cases)
        {
            var stream = MeasureFresh("stream", testCase.Name, width, height, input, streamedPath);
            var frame = MeasureFresh("frame", testCase.Name, width, height, input, framePath);
            var theirs = MeasureFresh("theirs", testCase.Name, width, height, input, oraclePath);
            streamSum += stream;
            frameSum += frame;
            theirsSum += theirs;

            rows.Add([testCase.Name, MiB(stream), MiB(frame), MiB(theirs),
                Ratio(stream, frame), Ratio(stream, theirs)]);
        }

        report.Table(["场景", "流式", "整图", "对手", "流式÷整图", "流式÷对手"], rows);
        report.Line($"> 合计：流式 {MiB(streamSum)}、整图 {MiB(frameSum)}、对手 {MiB(theirsSum)}；"
            + "比值 <1 表示我方更省。");
        report.Line("> 流式那一列不该随图高增长；它随图高增长说明解码、编码或计划里有一处把整帧攒住了。8K 直接跑本模式。");
        report.Line();
    }

    /// <summary>Runs one side of one case. Shared by the timed table, the parity check and the child process.</summary>
    private static ProcessReport Run(string side,
        (string Name, string Chain, Func<ImageTranscode, ImageTranscode> Stream, Func<OurImage, OurImage> Whole,
            Action<TheirImage> Oracle) testCase,
        string input, string output)
    {
        switch (side)
        {
            case "stream":
                return testCase.Stream(ImageTranscode.From(input)).Run(output);

            // The same chain with the fusion table overruled. Used by the ablation below: section boundaries are a
            // decision, and the only way to price a decision is to run the road not taken.
            case "fused":
                return testCase.Stream(ImageTranscode.From(input)
                        .WithOptions(new TranscodeOptions
                        {
                            Execute = new ExecuteOptions { ForceFusion = true },
                        }))
                    .Run(output);

            case "frame":
                using (var loaded = OurImage.Load(input))
                using (var result = testCase.Whole(loaded))
                    OurImage.Save(result, output);

                break;

            default:
                using (var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(input))
                {
                    testCase.Oracle(image);
                    image.Save(output, new PngEncoder());
                }

                break;
        }

        // Only the transcode entry has a report; the other two sides return the default value.
        return default;
    }

    /// <summary>
    /// The fusion decision, ablated on the streaming path. <c>ShouldFuse</c> prices a section boundary as a
    /// materialised frame, which is what a boundary costs the whole-image executor; the row pump pays a stage instead
    /// — two band windows plus one copy of the rows into the next stage's cache. Whether that difference changes a
    /// verdict is a measurement, not a derivation, so the same file-level chain runs twice: as decided, and with
    /// fusion forced. Blur+invert is the row that matters — 2 sections as decided, 1 when fused, at both sizes.
    /// </summary>
    public static void WriteFusionAb(Report report, int width, int height)
    {
        report.Heading(2, $"F 组 · 融合决策 A/B（`{width}×{height}`，我方流式文件路径）");
        report.Line("> 同一条链跑两遍：一遍按 `ShouldFuse` 的决策，一遍 `ForceFusion = true`。分段只换窗口、不许换字节——先比对再计时。");
        report.Line("> 要定价的是「段边界的代价」：`ShouldFuse` 按整图路径记账（省下的整帧 > 64 MiB），而流式路径的边界是一个");
        report.Line("> stage（2 块窗口 + 一次整帧行拷贝），不是一张物化帧。时间比与峰值比都 <1 才说明决策表低估了融合。");
        report.Line();

        var root = Directory.CreateTempSubdirectory("simd-fusionab-");
        try
        {
            var input = Path.Combine(root.FullName, "in.png");
            var decidedPath = Path.Combine(root.FullName, "decided.png");
            var fusedPath = Path.Combine(root.FullName, "fused.png");
            using (var source = Fixture.Ours(Fixture.Make(width, height), width, height))
                OurImage.Save(source, input);

            var rows = new List<string[]>();
            foreach (var testCase in Cases())
            {
                var decided = Run("stream", testCase, input, decidedPath);
                var fused = Run("fused", testCase, input, fusedPath);
                var identical = File.ReadAllBytes(decidedPath).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(fusedPath));

                var times = Timing.Time(
                [
                    new Runner("decided", () => Run("stream", testCase, input, decidedPath)),
                    new Runner("fused", () => Run("fused", testCase, input, fusedPath)),
                ], rounds: 5);

                var decidedPeak = MeasureFresh("stream", testCase.Name, width, height, input, decidedPath);
                var fusedPeak = MeasureFresh("fused", testCase.Name, width, height, input, fusedPath);

                rows.Add([testCase.Name,
                    decided.Execution.Steps.Count.ToString(CultureInfo.InvariantCulture),
                    fused.Execution.Steps.Count.ToString(CultureInfo.InvariantCulture),
                    Timing.F(times["decided"]), Timing.F(times["fused"]),
                    (times["fused"] / times["decided"]).ToString("F2", CultureInfo.InvariantCulture) + "×",
                    MiB(decidedPeak), MiB(fusedPeak),
                    ((double)fusedPeak / decidedPeak).ToString("F2", CultureInfo.InvariantCulture) + "×",
                    identical ? "一致" : "**不一致**"]);
            }

            report.Table(
                ["场景", "决策段数", "融合段数", "决策 ms", "融合 ms", "融合÷决策（时间）",
                    "决策峰值", "融合峰值", "融合÷决策（峰值）", "字节"],
                rows);
            report.Line("> 段数 1 = 整条链一段融合。时间比 <1 表示强制融合更快，峰值比 <1 表示更省；两项都 <1 时，决策表的");
            report.Line("> 「省下的整帧」在这条路径上就是错的单位。");
            report.Line();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>A child that runs one side once and prints <c>peak allocated</c> in bytes.</summary>
    public static void Child(string[] args)
    {
        var side = args[1];
        var name = args[2];
        var width = int.Parse(args[3], CultureInfo.InvariantCulture);
        var height = int.Parse(args[4], CultureInfo.InvariantCulture);
        var input = args[5];
        var output = args[6];

        var testCase = Cases().First(candidate => candidate.Name == name);

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
                {
                    if (current > peak)
                        peak = current;
                }

                Thread.Sleep(1);
            }
        });

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Run(side, testCase, input, output);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        cancel.Cancel();
        sampler.Wait();
        lock (gate)
            Console.WriteLine($"{peak - baseline} {allocated}");
    }

    private static long MeasureFresh(string side, string name, int width, int height, string input, string output,
        int repeats = 3)
    {
        var values = new List<long>(repeats);
        var self = typeof(Transcode).Assembly.Location;
        for (var i = 0; i < repeats; i++)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            // `dotnet run` launches through dotnet.exe and needs the dll first; a published exe must not get it.
            if (!Path.GetFileName(Environment.ProcessPath!)
                    .StartsWith("SimdImaging.Comparison", StringComparison.Ordinal))
                psi.ArgumentList.Add(self);
            psi.ArgumentList.Add("transcode-child");
            psi.ArgumentList.Add(side);
            psi.ArgumentList.Add(name);
            psi.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(height.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(input);
            psi.ArgumentList.Add(output);

            using var process = Process.Start(psi)!;
            var text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"transcode child failed ({side} {name}): {text}");
            values.Add(long.Parse(text.Split(' ')[0], CultureInfo.InvariantCulture));
        }

        values.Sort();
        return values[values.Count / 2];
    }

    private static string Ratio(long mine, long other) =>
        other == 0 ? "—" : (mine / (double)other).ToString("F2", CultureInfo.InvariantCulture) + "×";

    private static string GeometricMean(double logSum, int count) =>
        (Math.Exp(logSum / count)).ToString("F4", CultureInfo.InvariantCulture) + "×";

    private static string MiB(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MiB"
            : (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KiB";
}
