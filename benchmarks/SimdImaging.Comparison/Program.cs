using System.Globalization;
using SimdImaging.Comparison;

// Comparison harness. Usage:
//   dotnet run -c Release --project benchmarks/SimdImaging.Comparison [all|surface|parity|perf|sizes|cores|peakmem|memsteady|perf16|perfpipe|budget|profile] [WxH]
// Not part of SimdImaging.slnx on purpose: the shipped solution must stay buildable without the
// git-ignored Six Labors licence file (AGENTS.md clean-room rules).

// A fresh-process worker for peak working-set measurement (SimdCodecs' PeakMemory protocol).
if (args.Length > 0 && args[0] == "cores-child")
{
    Cores.Child(args);
    return;
}

if (args.Length > 0 && args[0] == "peakmem-child")
{
    PeakMemory.Child(args);
    return;
}

if (args.Length > 0 && args[0] == "memsteady-child")
{
    MemSteady.Child(args);
    return;
}

var mode = args.Length > 0 ? args[0] : "all";
var width = 1920;
var height = 1080;
if (args.Length > 1)
{
    var parts = args[1].Split('x');
    if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
        !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
        throw new ArgumentException("Size must look like 1920x1080");
}

var report = new Report();
report.Heading(1, "SimdImaging vs ImageSharp 4.1.3 — 实测原始结果");
report.Line($"- 日期：**{Env.Date}**");
report.Line($"- 机器：`{Env.MachineLine}`");
report.Line($"- 对手：`SixLabors.ImageSharp` NuGet **4.1.3**，程序集信息版本 `{Env.OracleVersion}`");
report.Line($"- 参数：`mode={mode}`，性能输入 {width}×{height}");
report.Line("- 方法：SimdCodecs 范式 —— 同进程 A/B、交错轮次取中位数、先正确性后计时、机器随结果记录。");
report.Line("- 合规：**只**通过公开 API / 公开元数据与黑盒二进制对比；未读取、反编译或复述对手源码。密钥只按路径引用。");
report.Line();

try
{
    if (mode is "all" or "surface")
    {
        Console.WriteLine("[1/3] feature surface (public API metadata)...");
        FeatureSurface.Write(report);
    }

    if (mode is "all" or "parity")
    {
        Console.WriteLine("[2/3] output parity + robustness + reproducibility...");
        Parity.Write(report);
    }

    if (mode is "all" or "perf")
    {
        Console.WriteLine($"[3/3] same-process A/B at {width}x{height} (this takes a few minutes)...");
        Perf.Write(report, width, height);
    }

    // A single resolution is not a conclusion: `resize-nearest` measured 0.84x at 1080p and 1.40x at 4K for
    // the same code, because at 1080p the per-call overhead dominates. This mode runs the whole table at every
    // size and prints where the ranking flips, which is the only way to see that.
    if (mode is "cores")
    {
        Cores.Write(report, width, height);
    }

    if (mode is "sizes")
    {
        (int W, int H)[] sizes = [(640, 360), (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160)];
        var bySize = new List<(string Size, List<(string Id, double Ratio)> Ratios)>();

        foreach (var (w, h) in sizes)
        {
            Console.WriteLine($"[sizes] {w}x{h} ...");
            var ratios = Perf.Write(report, w, h);
            bySize.Add(($"{w}×{h}", ratios));
        }

        report.Heading(2, "D 跨尺寸矩阵（每格是对手 ÷ 我方，>1 我方更快）");
        report.Line("> 同一份代码换个尺寸名次就变。**出现翻面的算子单列出来**——只在单一尺寸测过就下结论，"
                  + "会把开销问题当成吞吐问题。");
        report.Line();

        var ids = bySize[0].Ratios.Select(r => r.Id).ToList();
        var matrix = new List<string[]>();
        var flipped = new List<string>();

        foreach (var id in ids)
        {
            var cells = new List<string> { id };
            var values = new List<double>();
            foreach (var (_, ratios) in bySize)
            {
                var value = ratios.FirstOrDefault(r => r.Id == id).Ratio;
                values.Add(value);
                cells.Add(value.ToString("F2", CultureInfo.InvariantCulture));
            }

            var flips = values.Any(v => v > 1) && values.Any(v => v < 1);
            if (flips)
                flipped.Add($"{id}（{values.Min():F2}× … {values.Max():F2}×）");

            cells.Add(flips ? "**翻面**" : values.All(v => v > 1) ? "稳定领先" : "稳定落后");
            matrix.Add([.. cells]);
        }

        var headers = new List<string> { "场景" };
        headers.AddRange(bySize.Select(s => s.Size));
        headers.Add("判定");
        report.Table([.. headers], matrix);
        report.Line(flipped.Count == 0
            ? "**没有算子在不同尺寸间翻面。**"
            : $"**翻面 {flipped.Count} 项**：" + string.Join("；", flipped));
        report.Line();
    }

    if (mode is "budget")
    {
        Console.WriteLine($"[budget] resize row-band sweep at {width}x{height} (several minutes)...");
        Budget.Write(report, width, height);
    }

    if (mode is "peakmem")
    {
        Console.WriteLine($"[peakmem] fresh-process peak working set at {width}x{height}...");
        PeakMemory.Write(report, width, height);
    }

    if (mode is "memsteady")
    {
        Console.WriteLine($"[memsteady] fresh-process peak/live at {width}x{height}, 1/3/10 calls...");
        MemSteady.Write(report, width, height);
    }

    if (mode is "perf16")
    {
        Console.WriteLine($"[perf16] Rgba16 same-process A/B at {width}x{height}...");
        PerfRgba16.Write(report, width, height);
    }

    if (mode is "perfpipe")
    {
        Console.WriteLine($"[perfpipe] pipeline same-process A/B at {width}x{height}...");
        PerfPipeline.Write(report, width, height);
    }

    if (mode is "profile")
    {
        Console.WriteLine($"[profile] bottleneck ablation at {width}x{height}...");
        Profile.Write(report, width, height);
    }

    report.Line("---");
    report.Line("本文件由 `benchmarks/SimdImaging.Comparison` 自动生成，是原始数字；结论与人工复核见 `docs/compare-4.1.3-*.md`。");
    // A tag lets each optimisation step keep its own same-process A/B report for before/after comparison.
    var tag = Environment.GetEnvironmentVariable("SIMDIMAGING_COMPARE_TAG");
    var stem = Env.Date + (string.IsNullOrEmpty(tag) ? "" : "." + tag) + "." + (mode == "all" ? "raw" : mode);
    var path = report.Save(stem + ".md");
    Console.WriteLine($"report: {path}");
}
catch (Exception e)
{
    report.Line();
    report.Line($"**运行中断**：`{e.GetType().Name}`: {e.Message}");
    var failed = report.Save(Env.Date + "." + mode + ".failed.md");
    Console.Error.WriteLine($"FAILED: {e}");
    Console.Error.WriteLine($"partial report: {failed}");
    Environment.ExitCode = 1;
}
