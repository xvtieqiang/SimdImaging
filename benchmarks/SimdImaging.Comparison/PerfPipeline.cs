using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SixLabors.ImageSharp.Processing;
using TheirImage = SixLabors.ImageSharp.Image;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// Pipeline scenarios, which the twelve single-operator cases deliberately avoided.
/// <para>The structural difference is real but easy to overstate: <c>ProcessingPlan</c> runs every same-size
/// step inside one row-band pass, so a three-step chain never materialises an intermediate image. The oracle
/// also mutates in place through its own processing context, so the honest question is not "do we fuse and
/// they don't" but "does our fusion actually beat their in-place chain". Only a measurement answers that.</para>
/// </summary>
internal static class PerfPipeline
{
    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, "E 组 · 流水线端到端 A/B（`ProcessingPlan` 对对手的处理链）");
        report.Line($"> 输入 {width}×{height} `Rgba8`。两边都从共享源产出新图；我方用 `ProcessingPlan` 单次行带执行，");
        report.Line("> 对手用 `Clone(ctx => ctx.A()...)` 的原地处理链。加速比 = 对手 ÷ 我方，>1 表示我方更快。");
        report.Line();

        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);

        var cases = new (string Name, string Chain, ProcessingPlan Plan,
            Func<TheirImage, TheirImage> Oracle)[]
        {
            ("point-3", "Contrast 1.2 → Sepia → Grayscale (BT.601)",
                new ProcessingPlan()
                    .Add("contrast", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.2f)))
                    .Add("sepia", OpCategory.Point, PlanSteps.Point(v => PointOps.Sepia(v, 1f)))
                    .Add("grayscale", OpCategory.Point, PlanSteps.Point(v => PointOps.Grayscale(v))),
                i => i.Clone(x => x.Contrast(1.2f).Sepia().Grayscale(GrayscaleMode.Bt601))),

            ("point-5", "Invert → Contrast 0.9 → Sepia → Contrast 1.1 → Grayscale",
                new ProcessingPlan()
                    .Add("invert", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)))
                    .Add("contrast1", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 0.9f)))
                    .Add("sepia", OpCategory.Point, PlanSteps.Point(v => PointOps.Sepia(v, 1f)))
                    .Add("contrast2", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.1f)))
                    .Add("grayscale", OpCategory.Point, PlanSteps.Point(v => PointOps.Grayscale(v))),
                i => i.Clone(x => x.Invert().Contrast(0.9f).Sepia().Contrast(1.1f)
                    .Grayscale(GrayscaleMode.Bt601))),

            // The same five per-channel steps, once as separate point steps and once collapsed into one table.
            // Point ops are the only chain shape where this library used to lose to the oracle.
            ("point-5-steps", "Invert → Contrast .9 → Brightness .02 → Contrast 1.1 → Invert (five steps)",
                new ProcessingPlan()
                    .Add("invert", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)))
                    .Add("contrast1", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 0.9f)))
                    .Add("brightness", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.02f)))
                    .Add("contrast2", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.1f)))
                    .Add("invert2", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v))),
                i => i.Clone(x => x.Invert().Contrast(0.9f).Brightness(0.02f).Contrast(1.1f).Invert())),

            ("point-5-collapsed", "Invert → Contrast .9 → Brightness .02 → Contrast 1.1 → Invert (one table)",
                new ProcessingPlan().Add(PlanOps.PointChain(
                    PointOps.InvertRow(),
                    PointOps.ContrastRow(0.9f),
                    PointOps.BrightnessRow(0.02f),
                    PointOps.ContrastRow(1.1f),
                    PointOps.InvertRow())),
                i => i.Clone(x => x.Invert().Contrast(0.9f).Brightness(0.02f).Contrast(1.1f).Invert())),

            ("blur-point", "Gaussian σ3 → Sepia → Grayscale",
                new ProcessingPlan()
                    .Add("blur", OpCategory.Neighborhood,
                        PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 3f), 9, 9), 9, 9)
                    .Add("sepia", OpCategory.Point, PlanSteps.Point(v => PointOps.Sepia(v, 1f)))
                    .Add("grayscale", OpCategory.Point, PlanSteps.Point(v => PointOps.Grayscale(v))),
                i => i.Clone(x => x.GaussianBlur(3f).Sepia().Grayscale(GrayscaleMode.Bt601))),
        };

        var rows = new List<string[]>();
        double logSum = 0;
        var wins = 0;

        foreach (var testCase in cases)
        {
            var runners = new[]
            {
                new Runner("ours", () => { using var r = ours.Apply(testCase.Plan); GC.KeepAlive(r); }),
                new Runner("theirs", () => { using var r = testCase.Oracle(theirs); GC.KeepAlive(r); }),
            };

            var allocated = new Dictionary<string, long>();
            var times = Timing.Time(runners, rounds: 7, allocated: allocated);
            var ratio = times["theirs"] / times["ours"];
            logSum += Math.Log(ratio);
            if (ratio > 1) wins++;

            rows.Add([testCase.Name, testCase.Chain, testCase.Plan.Steps.Count.ToString(CultureInfo.InvariantCulture),
                Timing.F(times["ours"]), Timing.F(times["theirs"]),
                ratio.ToString("F2", CultureInfo.InvariantCulture) + "×",
                (allocated["ours"] / 1024).ToString(CultureInfo.InvariantCulture) + " KiB",
                (allocated["theirs"] / 1024).ToString(CultureInfo.InvariantCulture) + " KiB",
                ratio > 1 ? "我方快" : "对手快"]);
        }

        report.Table(["场景", "链路", "步数", "我方 ms", "对手 ms", "加速比", "我方分配/op", "对手分配/op", "胜负"], rows);
        report.Line($"**流水线超越判定：{(Math.Exp(logSum / cases.Length)).ToString("F4", CultureInfo.InvariantCulture)}×**（{wins} / {cases.Length} 项领先）。");
        report.Line("> 分配为 `GC.GetTotalAllocatedBytes`（进程级），口径与单算子表一致。");
        report.Line();

        WriteRandomChains(report, ours, theirs, width, height);
    }

    // The matched operator pool lives in RandomChain, which also drives the short/medium/long bands; keeping
    // one pool means a chain drawn here is the same work as a chain drawn there.

    /// <summary>
    /// The five hand-picked chains above are selected cases, not a statistical claim about the engine. This
    /// section generates seeded random chains (length 2–16, repeats allowed) from a matched operator pool and
    /// measures the same way — one new image per side, interleaved rounds, process-wide allocation. It answers
    /// "does the engine hold up on chains nobody chose", reporting time and memory separately so a kernel that
    /// wins on time but leaks buffers per step cannot hide behind the headline ratio.
    /// </summary>
    private static void WriteRandomChains(Report report, OurImage ours, TheirImage theirs, int width, int height)
    {
        const int cases = 48;
        const int seed = 20261127;

        report.Heading(3, "随机算子链路（`ProcessingPlan` 对对手，时间 + 内存）");
        report.Line($"> {cases} 条由固定种子 {seed} 生成的随机链，长度 2–16、随机重复；两侧都只产出一张新图。");
        report.Line("> 时间比 = 对手 ÷ 我方，>1 我方快；分配比 = 对手 ÷ 我方，>1 我方分配更少。");
        report.Line("> inv 反相 / con 对比度 / bri 亮度 / sep 褐调 / gry 灰度 / sat 饱和 / gb 高斯 / bb 盒模糊 / shp 锐化。");
        report.Line();

        var random = new Random(seed);
        var rows = new List<string[]>();
        double timeLog = 0, allocLog = 0;
        var timeWins = 0;
        var allocWins = 0;
        var slowest = new List<(double Ratio, int Len, string Chain)>();
        // Separate point-only chains from chains containing a filter. A filter allocates a per-step float
        // workspace on our side; mixing the two classes into one geometric mean would report an "engine"
        // number that is really about kernel scratch. Keep each class's own account.
        var byClass = new Dictionary<string, (int N, double TimeLog, double AllocLog, long OurAlloc)>
        {
            ["纯点"] = (0, 0, 0, 0),
            ["含滤波"] = (0, 0, 0, 0),
        };

        for (var c = 0; c < cases; c++)
        {
            var len = random.Next(2, 17);
            var chain = new int[len];
            for (var i = 0; i < len; i++)
                chain[i] = random.Next(RandomChain.Pool.Length);

            var plan = new ProcessingPlan();
            var steps = new RandomChain.OracleStep[len];
            for (var i = 0; i < len; i++)
            {
                plan.Add(RandomChain.Pool[chain[i]].Ours());
                steps[i] = RandomChain.Pool[chain[i]].Theirs;
            }

            var label = string.Join("→", chain.Select(i => RandomChain.Pool[i].Name));
            var hasFilter = chain.Any(i => RandomChain.IsFilter(RandomChain.Pool[i].Name));
            var className = hasFilter ? "含滤波" : "纯点";
            var runners = new[]
            {
                new Runner("ours", () => { using var r = ours.Apply(plan); GC.KeepAlive(r); }),
                new Runner("theirs", () => { using var r = theirs.Clone(ctx => { foreach (var s in steps) s(ctx); }); GC.KeepAlive(r); }),
            };

            var times = Timing.Time(runners, rounds: 5, sampleMs: 20);
            var timeRatio = times["theirs"] / times["ours"];

            // Allocation needs its own protocol. Timing.Time's `allocated` is a single precise:false reading
            // taken inside the warm-up foreach, where one side can absorb a GC or a thread-pool allocation; that
            // made the fused chain look like it allocated megabytes. Measure a batch of runs with precise:true,
            // interleave the two sides, repeat, and take the median — the same discipline as the timing loop.
            const int allocBatch = 8;
            const int allocSamples = 5;
            var oursSamples = new long[allocSamples];
            var theirsSamples = new long[allocSamples];
            for (var s = 0; s < allocSamples; s++)
            {
                var before = GC.GetTotalAllocatedBytes(precise: true);
                for (var i = 0; i < allocBatch; i++)
                {
                    using var r = ours.Apply(plan);
                    GC.KeepAlive(r);
                }
                oursSamples[s] = (GC.GetTotalAllocatedBytes(precise: true) - before) / allocBatch;

                before = GC.GetTotalAllocatedBytes(precise: true);
                for (var i = 0; i < allocBatch; i++)
                {
                    using var r = theirs.Clone(ctx => { foreach (var st in steps) st(ctx); });
                    GC.KeepAlive(r);
                }
                theirsSamples[s] = (GC.GetTotalAllocatedBytes(precise: true) - before) / allocBatch;
            }
            Array.Sort(oursSamples);
            Array.Sort(theirsSamples);
            var ourAlloc = oursSamples[allocSamples / 2];
            var theirAlloc = theirsSamples[allocSamples / 2];
            var allocRatio = (double)theirAlloc / Math.Max(1, ourAlloc);
            timeLog += Math.Log(timeRatio);
            allocLog += Math.Log(allocRatio);
            if (timeRatio > 1) timeWins++;
            if (allocRatio > 1) allocWins++;
            if (timeRatio <= 1) slowest.Add((timeRatio, len, label));

            var acc = byClass[className];
            byClass[className] = (acc.N + 1, acc.TimeLog + Math.Log(timeRatio),
                acc.AllocLog + Math.Log(allocRatio), acc.OurAlloc + ourAlloc);

            rows.Add([(c + 1).ToString(CultureInfo.InvariantCulture), len.ToString(CultureInfo.InvariantCulture),
                className, label, Timing.F(times["ours"]), Timing.F(times["theirs"]),
                timeRatio.ToString("F2", CultureInfo.InvariantCulture) + "×",
                (ourAlloc / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + "K",
                (theirAlloc / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + "K",
                allocRatio.ToString("F2", CultureInfo.InvariantCulture) + "×", timeRatio > 1 ? "快" : "慢"]);
        }

        report.Table(["#", "步长", "类别", "随机链", "我方 ms", "对手 ms", "时间比", "我方分配", "对手分配", "分配比", "胜负"], rows);
        var timeGeo = Math.Exp(timeLog / cases);
        var allocGeo = Math.Exp(allocLog / cases);
        report.Line($"**随机链判定：时间 {timeGeo.ToString("F4", CultureInfo.InvariantCulture)}×**（{timeWins}/{cases} 领先），" +
                    $"**分配 {allocGeo.ToString("F4", CultureInfo.InvariantCulture)}×**（{allocWins}/{cases} 我方更省）。");
        report.Line("> 但 10 个算子里 4 个是滤波，随机链几乎必然含滤波，混合判定不代表「引擎的链」。按类别拆开：");
        foreach (var (name, acc) in byClass.OrderByDescending(kv => kv.Value.N))
        {
            if (acc.N == 0) continue;
            var classTime = Math.Exp(acc.TimeLog / acc.N);
            var classAlloc = Math.Exp(acc.AllocLog / acc.N);
            report.Line($"> - **{name} {acc.N} 条**：时间 {classTime.ToString("F2", CultureInfo.InvariantCulture)}×，" +
                        $"分配 {classAlloc.ToString("F2", CultureInfo.InvariantCulture)}×，我方分配中位 {((acc.OurAlloc / (double)acc.N) / 1024).ToString("F0", CultureInfo.InvariantCulture)}K/op。");
        }
        report.Line("> 分配用 `precise:true`、8 次一批 × 5 批取中位（不是 `Timing.Time` 的单次采样）；两侧同协议。");
        if (slowest.Count > 0)
        {
            report.Line("> 时间落后样例（最慢 5 条）：");
            foreach (var s in slowest.OrderBy(x => x.Ratio).Take(5))
                report.Line($"> - {s.Ratio.ToString("F2", CultureInfo.InvariantCulture)}× · {s.Len} 步 · {s.Chain}");
        }

        report.Line();
    }
}
