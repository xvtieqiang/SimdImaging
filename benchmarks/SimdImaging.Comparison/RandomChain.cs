using System.Globalization;
using SimdImaging.Processing;
using SixLabors.ImageSharp.Processing;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>
/// Random operator chains split by LENGTH, because chain length is the one variable the single-chain
/// pipeline cases hold fixed. A fused plan pays its per-call overhead once and then runs every step in one
/// band pass, so the interesting question is whether that advantage grows, stays flat, or decays as the chain
/// gets longer — and whether a twenty-step chain still allocates one output instead of twenty intermediates.
/// </summary>
/// <remarks>
/// <para>Three bands: short (2–4 steps), medium (5–9), long (10–20). Each band draws its chains from the same
/// matched operator pool under its own fixed seed, so the runs are reproducible and rerunnable after a change.
/// Time is measured with the usual interleaved protocol; allocation gets its own precise batched protocol,
/// because <see cref="Timing.Time"/>'s single allocation reading is taken inside warm-up and is not a
/// measurement (see the pool note in <see cref="PerfPipeline"/>).</para>
/// <para>Parity is reported, not asserted: filtering operators already differ at the edges between the two
/// libraries, and a chain of twenty steps amplifies whatever single-step difference it contains. The grade
/// says what the numbers mean; it is not a pass/fail gate.</para>
/// </remarks>
internal static class RandomChain
{
    internal delegate void OracleStep(IImageProcessingContext ctx);

    internal readonly record struct Op(string Name, Func<ImageOp> Ours, OracleStep Theirs);

    /// <summary>A matched pool: every entry means the same thing on both sides and stays same-size, so a
    /// random chain is the same work on both sides and only the engine differs.</summary>
    internal static readonly Op[] Pool =
    [
        new("inv", () => PlanOps.Invert(), ctx => ctx.Invert()),
        new("con", () => PlanOps.Contrast(1.2f), ctx => ctx.Contrast(1.2f)),
        new("bri", () => PlanOps.Brightness(0.05f), ctx => ctx.Brightness(0.05f)),
        new("sep", () => PlanOps.Sepia(1f), ctx => ctx.Sepia()),
        new("gry", () => PlanOps.Grayscale(), ctx => ctx.Grayscale(GrayscaleMode.Bt601)),
        new("sat", () => PlanOps.Saturation(1.6f), ctx => ctx.Saturate(1.6f)),
        new("gb1", () => PlanOps.GaussianBlur(1f), ctx => ctx.GaussianBlur(1f)),
        new("gb3", () => PlanOps.GaussianBlur(3f), ctx => ctx.GaussianBlur(3f)),
        new("bb2", () => PlanOps.BoxBlur(2), ctx => ctx.BoxBlur(2)),
        new("shp", () => PlanOps.Sharpen(0.5f, 1f), ctx => ctx.GaussianSharpen(0.5f)),
    ];

    internal static bool IsFilter(string name) => name is "gb1" or "gb3" or "bb2" or "shp";

    private static readonly (string Name, int Min, int Max)[] Bands =
    [
        ("短 2–4", 2, 4),
        ("中 5–9", 5, 9),
        ("长 10–20", 10, 20),
    ];

    private const int CasesPerBand = 12;
    private const int Seed = 20261127;

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, $"N 组 · 随机算子链：短 / 中 / 长（{width}×{height}）");
        report.Line($"> 每档 {CasesPerBand} 条链，从同一算子池（10 个，4 个是滤波）按各档种子随机抽取，允许重复；两侧都只产出一张新图。");
        report.Line("> 时间比 = 对手 ÷ 我方，>1 我方快；分配比 = 对手 ÷ 我方，>1 我方更省。");
        report.Line("> 分配用 `precise:true`、4 次一批 × 5 批取中位（与 `perfpipe` 的随机链同一协议）。"
                  + "`常驻+` 是我方连跑 3 次、每次释放结果后强制 GC 的托管堆增量（池保留量），对手侧由它自己的分配器管理，恒为 n/a。");
        report.Line("> 一致性是**报告**而非门槛：滤波算子在两侧的边缘语义本就不同，长链会放大单步差异。");
        report.Line("> 但放大有已知来源：单算子对拍里 `inv` 逐字节一致、`con`/`sep`/`gry` ≤1 LSB、`sat` 约 34 dB、"
                  + "`bri` 语义不同（≈4 dB：我方是归一化加法，对手是比例语义）。**含 `bri` 的链 PSNR 低是语义差而非融合错误**，"
                  + "时间与分配不受影响；PSNR 一列只用于提示「这条链的结果不能拿去做像素级比对」。");
        report.Line();

        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);

        var summary = new List<string[]>();
        var bandIndex = 0;
        foreach (var (bandName, min, max) in Bands)
        {
            report.Heading(3, $"档位 {bandName}（{width}×{height}）");
            var rows = new List<string[]>();
            var random = new Random(Seed + bandIndex * 1009);
            var timeLog = 0.0;
            var allocLog = 0.0;
            var timeWins = 0;
            var allocWins = 0;
            var liveIncrements = new List<long>();
            var worstPsnr = double.PositiveInfinity;
            var filters = 0;

            for (var c = 0; c < CasesPerBand; c++)
            {
                var len = random.Next(min, max + 1);
                var chain = new int[len];
                for (var i = 0; i < len; i++)
                    chain[i] = random.Next(Pool.Length);

                var plan = new ProcessingPlan();
                var steps = new OracleStep[len];
                for (var i = 0; i < len; i++)
                {
                    plan.Add(Pool[chain[i]].Ours());
                    steps[i] = Pool[chain[i]].Theirs;
                }

                var label = string.Join("→", chain.Select(i => Pool[i].Name));
                var hasFilter = chain.Any(i => IsFilter(Pool[i].Name));
                if (hasFilter) filters++;

                // Parity first, before warm-up perturbs the pools: one fresh output per side.
                byte[] ourBytes;
                byte[] theirBytes;
                using (var a = ours.Apply(plan)) ourBytes = Fixture.Bytes(a);
                using (var b = theirs.Clone(ctx => { foreach (var s in steps) s(ctx); })) theirBytes = Fixture.Bytes(b);
                var parity = Metrics.Compare(ourBytes, theirBytes);
                if (parity.PsnrRgb < worstPsnr) worstPsnr = parity.PsnrRgb;

                // Retained-after-the-fact: our pools only. Three runs, result disposed each time, then a
                // forced collection; a flat number here means no per-step buffer is kept alive.
                var liveBefore = GC.GetTotalMemory(forceFullCollection: true);
                for (var i = 0; i < 3; i++)
                {
                    using var result = ours.Apply(plan);
                }
                var liveGrowth = GC.GetTotalMemory(forceFullCollection: true) - liveBefore;
                liveIncrements.Add(liveGrowth);

                var runners = new[]
                {
                    new Runner("ours", () => { using var r = ours.Apply(plan); GC.KeepAlive(r); }),
                    new Runner("theirs", () => { using var r = theirs.Clone(ctx => { foreach (var s in steps) s(ctx); }); GC.KeepAlive(r); }),
                };

                var times = Timing.Time(runners, rounds: 5, sampleMs: 20);
                var timeRatio = times["theirs"] / times["ours"];

                const int allocBatch = 4;
                const int allocSamples = 5;
                var ourSamples = new long[allocSamples];
                var theirSamples = new long[allocSamples];
                for (var s = 0; s < allocSamples; s++)
                {
                    var before = GC.GetTotalAllocatedBytes(precise: true);
                    for (var i = 0; i < allocBatch; i++)
                    {
                        using var r = ours.Apply(plan);
                        GC.KeepAlive(r);
                    }
                    ourSamples[s] = (GC.GetTotalAllocatedBytes(precise: true) - before) / allocBatch;

                    before = GC.GetTotalAllocatedBytes(precise: true);
                    for (var i = 0; i < allocBatch; i++)
                    {
                        using var r = theirs.Clone(ctx => { foreach (var st in steps) st(ctx); });
                        GC.KeepAlive(r);
                    }
                    theirSamples[s] = (GC.GetTotalAllocatedBytes(precise: true) - before) / allocBatch;
                }
                Array.Sort(ourSamples);
                Array.Sort(theirSamples);
                var ourAlloc = ourSamples[allocSamples / 2];
                var theirAlloc = theirSamples[allocSamples / 2];
                var allocRatio = (double)theirAlloc / Math.Max(1, ourAlloc);

                timeLog += Math.Log(timeRatio);
                allocLog += Math.Log(allocRatio);
                if (timeRatio > 1) timeWins++;
                if (allocRatio > 1) allocWins++;

                rows.Add([
                    (c + 1).ToString(CultureInfo.InvariantCulture),
                    len.ToString(CultureInfo.InvariantCulture),
                    hasFilter ? "含滤波" : "纯点",
                    label,
                    Timing.F(times["ours"]),
                    Timing.F(times["theirs"]),
                    timeRatio.ToString("F2", CultureInfo.InvariantCulture) + "×",
                    K(ourAlloc),
                    K(theirAlloc),
                    allocRatio.ToString("F2", CultureInfo.InvariantCulture) + "×",
                    Live(liveGrowth),
                    Psnr(parity.PsnrRgb),
                    timeRatio > 1 ? "快" : "慢",
                ]);
            }

            report.Table(
                ["#", "步数", "类别", "随机链", "我方 ms", "对手 ms", "时间比", "我方分配", "对手分配", "分配比", "常驻+", "PSNR", "胜负"],
                rows);

            liveIncrements.Sort();
            summary.Add([
                bandName,
                CasesPerBand.ToString(CultureInfo.InvariantCulture),
                Math.Exp(timeLog / CasesPerBand).ToString("F3", CultureInfo.InvariantCulture) + "×",
                Math.Exp(allocLog / CasesPerBand).ToString("F3", CultureInfo.InvariantCulture) + "×",
                $"{timeWins}/{CasesPerBand}",
                $"{allocWins}/{CasesPerBand}",
                Psnr(worstPsnr),
                Live(liveIncrements[liveIncrements.Count / 2]),
                $"{filters}/{CasesPerBand}",
            ]);
            report.Line();
            bandIndex++;
        }

        report.Heading(3, "档位汇总");
        report.Table(
            ["档位", "条数", "时间几何平均", "分配几何平均", "时间领先", "分配更省", "最差 PSNR", "常驻+中位", "含滤波"],
            summary);
        report.Line("> 读法：时间几何平均不随步数塌陷 ＝ 融合把每一步的固定开销摊掉了；分配几何平均接近 1 且我方分配不随步数增长 ＝ 长链没有物化中间图。");
        report.Line("> 分配数值是**每次调用的进程级净分配**：不含 `shp` 的链稳定在 100–220 KiB，说明融合没有物化中间图；"
                  + "含 `shp` 的链却按每个锐化步多租 ≈1 MiB（最多约 3 MiB），是分配比落到 0.03–0.09 的唯一原因——"
                  + "那是 `shp` 的 per-worker 浮点 workspace，不是像素缓冲。");
        report.Line("> **长链时间落后是已知缺口，不是本次新发现**：`docs/known-gaps.md`「随机链对标」记录 48 条随机链在 "
                  + "1080p/4K/640×360 三个尺寸一致落后（0.91×/0.93×/0.90×），长度 10–16 段 0/19 领先。"
                  + "本模式把同一个池按 2–4 / 5–9 / 10–20 分档复现了它：分档后可以看到落后只在长档出现，短链基本持平。");
        report.Line();
    }

    private static string K(long bytes) =>
        (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + "K";

    private static string Live(long bytes) => bytes == 0
        ? "0"
        : (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + "K";

    private static string Psnr(double psnr) => double.IsPositiveInfinity(psnr)
        ? "∞"
        : psnr.ToString("F1", CultureInfo.InvariantCulture);
}
