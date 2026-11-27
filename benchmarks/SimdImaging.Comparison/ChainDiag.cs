using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SixLabors.ImageSharp.Processing;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>
/// Asks why a long chain loses by running the SAME chain through every path the engine offers:
/// the compiler's decision, fusion forced off, fusion forced on, the direct fluent chain, and the oracle's
/// in-place chain. If forced-off and forced-on land on the same number the engine's segmentation is not the
/// variable; if forced-on is much slower, the fused window's halo recompute is.
/// </summary>
/// <remarks>
/// It also prints the compiler's sections as <c>Image.Explain</c> reports them, because "did it fuse" is a
/// structural question and the answer should come from the compiler, not from a guess made out of timings.
/// </remarks>
internal static class ChainDiag
{
    /// <summary>The worst long chain from the 2026-11-27 random-chain run (20 steps, four sharpeners).</summary>
    private static readonly string[] Chain =
    [
        "sat", "sat", "gb3", "shp", "sep", "gb3", "sep", "bb2", "gry", "bb2",
        "gb1", "gb3", "sat", "shp", "inv", "gry", "con", "gb3", "con", "shp",
    ];

    public static void Run(int width, int height)
    {
        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);

        var plan = new ProcessingPlan();
        var steps = new RandomChain.OracleStep[Chain.Length];
        for (var i = 0; i < Chain.Length; i++)
        {
            var index = Array.FindIndex(RandomChain.Pool, op => op.Name == Chain[i]);
            plan.Add(RandomChain.Pool[index].Ours());
            steps[i] = RandomChain.Pool[index].Theirs;
        }

        var explanation = ours.Explain(plan);
        Console.WriteLine($"[chaindiag] {width}x{height} · {Chain.Length} 步 · 后端 {explanation.Backend}");
        Console.WriteLine($"  工作集预测 {explanation.Workspace.PredictedBytes / 1048576.0:F1} MiB"
                        + (explanation.Workspace.IsLowerBound ? "（下界）" : ""));
        foreach (var term in explanation.Workspace.Terms)
            Console.WriteLine($"    - {term.Kind,-18} {term.Bytes / 1048576.0,8:F1} MiB  {term.Note}");

        // Invert the window term for the rows it holds: two windows per worker, one row = width x 4 bytes.
        var windowTerm = explanation.Workspace.Terms.FirstOrDefault(t => t.Kind == PlanWorkspaceKind.Window);
        if (windowTerm is not null)
        {
            var workers = Math.Min(Environment.ProcessorCount, Math.Max(1, height / 8));
            var rowBytes = (long)width * 4;
            var windowRows = windowTerm.Bytes / (2.0 * workers * rowBytes);
            Console.WriteLine($"  推算窗口 {windowRows:F0} 行（2 × {workers} worker × {width}×4 B）；"
                            + $"按 halo 合计 52 行，带宽 ≈ {windowRows - 104:F0} 行");
        }
        foreach (var section in explanation.Sections)
            Console.WriteLine($"    段 {section.Kind,-12} 步数 {section.StepCount,3} 融合 {section.Fused}");
        foreach (var note in explanation.Notes)
            Console.WriteLine($"    注：{note}");

        var paths = new (string Name, Action Run)[]
        {
            ("编译器决定", () => { using var r = ours.Apply(plan); GC.KeepAlive(r); }),
            ("强制不融合", () => { using var r = ours.ApplyWithOptions(plan, new ExecuteOptions { AllowFusion = false }); GC.KeepAlive(r); }),
            ("强制融合", () => { using var r = ours.ApplyWithOptions(plan, new ExecuteOptions { ForceFusion = true }); GC.KeepAlive(r); }),
            ("仅编译", () => { var e = ours.Explain(plan); GC.KeepAlive(e); }),
            ("直调串链", () => { using var r = Direct(ours); GC.KeepAlive(r); }),
            ("对手原地链", () => { using var r = theirs.Clone(ctx => { foreach (var s in steps) s(ctx); }); GC.KeepAlive(r); }),
        };

        var times = Timing.Time(paths.Select(p => new Runner(p.Name, p.Run)).ToArray(), rounds: 5, sampleMs: 20);
        var all = times["对手原地链"];
        Console.WriteLine("  路径            中位 ms   相对对手   分配/op");
        foreach (var (name, _) in paths)
        {
            var allocated = Alloc(name, paths, width, height);
            Console.WriteLine($"  {name,-12} {times[name],9:F2} {times[name] / all,9:F3}× {allocated / 1024.0,9:F0}K");
        }

        Console.WriteLine("  纯点链（inv→con→sep→gry）同尺寸：");
        {
            var pointSteps = PointChain.Select(name => RandomChain.Pool[Array.FindIndex(RandomChain.Pool, op => op.Name == name)].Theirs).ToArray();
            var pointRunners = new[]
            {
                new Runner("ours-default", () => { using var r = ours.Apply(Build(PointChain, 0)); GC.KeepAlive(r); }),
                new Runner("ours-stepwise", () => { using var r = ours.ApplyWithOptions(Build(PointChain, 0), new ExecuteOptions { AllowFusion = false }); GC.KeepAlive(r); }),
                new Runner("theirs", () => { using var r = theirs.Clone(ctx => { foreach (var s in pointSteps) s(ctx); }); GC.KeepAlive(r); }),
            };
            var pointTimes = Timing.Time(pointRunners, rounds: 5, sampleMs: 20);
            Console.WriteLine($"    编译器决定 {pointTimes["ours-default"],8:F2} ms   {pointTimes["theirs"] / pointTimes["ours-default"],6:F3}×");
            foreach (var (label, bandRows) in new[] { ("自动", 0), ("显式16", 16) })
            {
                using var probe = Build(PointChain, bandRows).ExecuteWithOptions(ours[0].Pixels.View,
                    new ExecuteOptions { Diagnostics = true }, out var probeReport);
                GC.KeepAlive(probe);
                Console.WriteLine($"      带高 {label}: 段 {probeReport!.SegmentCount}，带数 {probeReport.BandCount}，"
                                + $"worker {probeReport.WorkerCount}，融合段 {probeReport.Segments.Count(x => x.Fused)}");
            }
            Console.WriteLine($"    不融合 {pointTimes["ours-stepwise"],8:F2} ms   对手 {pointTimes["theirs"],8:F2} ms   {pointTimes["theirs"] / pointTimes["ours-stepwise"],6:F3}×");
            foreach (var rows in new[] { 8, 12, 16, 24, 32, 64, 67, 135, height })
            {
                var pointPlan = Build(PointChain, rows);
                var runners = new[] { new Runner("fused", () => { using var r = ours.ApplyWithOptions(pointPlan, Fused()); GC.KeepAlive(r); }) };
                var time = Timing.Time(runners, rounds: 5, sampleMs: 20)["fused"];
                Console.WriteLine($"    融合 带高 {rows,4}（{Math.Ceiling(height / (double)rows),3:F0} 带）  {time,8:F2} ms  {pointTimes["theirs"] / time,6:F3}×");
            }
        }

        Console.WriteLine("  融合带高扫描（强制融合）：");
        foreach (var rows in BandRows)
        {
            var banded = Build(rows);
            var runners = new[] { new Runner("fused", () => { using var r = ours.ApplyWithOptions(banded, Fused()); GC.KeepAlive(r); }) };
            var bandedTime = Timing.Time(runners, rounds: 5, sampleMs: 20)["fused"];
            Console.WriteLine($"    带高 {rows,4} 行（{Math.Ceiling(height / (double)rows),3:F0} 带，窗口 {rows + 104,4} 行，重叠 {(rows + 104.0) / rows,4:F1}×）  {bandedTime,8:F2} ms  {bandedTime / all,6:F3}×");
        }
    }

    /// <summary>A halo-free chain: four point steps that mean the same thing on both sides (≤1 LSB), so the
    /// comparison is about the executor's shape, not about pixel semantics.</summary>
    private static readonly string[] PointChain = ["inv", "con", "sep", "gry"];

    private static readonly ExecuteOptions FusionOn = new() { ForceFusion = true };
    private static ExecuteOptions Fused() => FusionOn;

    /// <summary>The band heights to sweep. The fused window is <c>band + 2 × Σhalo</c> rows, so the useful range
    /// spans "several times the halo" to "one band for the image"; the curve, not a single point, is the answer.</summary>
    private static readonly int[] BandRows = [16, 24, 32, 48, 64, 96, 128, 180, 270, 540];

    /// <summary>The same chain with a chosen band height: fusion's halo overhead is
    /// <c>2 × Σhalo ÷ bandRows</c> extra rows per band, so band height is the variable under test.</summary>
    private static ProcessingPlan Build(int bandRows) => Build(Chain, bandRows);

    private static ProcessingPlan Build(IReadOnlyList<string> names, int bandRows)
    {
        var plan = bandRows > 0 ? new ProcessingPlan { BandRows = bandRows } : new ProcessingPlan();
        foreach (var name in names)
        {
            var index = Array.FindIndex(RandomChain.Pool, op => op.Name == name);
            plan.Add(RandomChain.Pool[index].Ours());
        }
        return plan;
    }

    private static long Alloc(string name, (string Name, Action Run)[] paths, int width, int height)    {
        var run = paths.First(p => p.Name == name).Run;
        const int batch = 3;
        const int samples = 5;
        var readings = new long[samples];
        for (var s = 0; s < samples; s++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < batch; i++)
                run();
            readings[s] = (GC.GetTotalAllocatedBytes(precise: true) - before) / batch;
        }
        Array.Sort(readings);
        return readings[samples / 2];
    }

    private static OurImage Direct(OurImage image) =>
        image.Pipe()
            .Saturation(1.6f).Saturation(1.6f).GaussianBlur(3f).Sharpen(0.5f, 1f).Sepia()
            .GaussianBlur(3f).Sepia().BoxBlur(2).Grayscale().BoxBlur(2)
            .GaussianBlur(1f).GaussianBlur(3f).Saturation(1.6f).Sharpen(0.5f, 1f).Invert()
            .Grayscale().Contrast(1.2f).GaussianBlur(3f).Contrast(1.2f).Sharpen(0.5f, 1f)
            .ToImage();
}
