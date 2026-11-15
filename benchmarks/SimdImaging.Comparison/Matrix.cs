using System.Globalization;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>
/// The size × working-format matrix: every case in <see cref="Cases.Build"/> run at three sizes and two
/// working formats, against the oracle, in one report.
/// </summary>
/// <remarks>
/// <para>
/// This adds no new ruler. Each cell is exactly what <see cref="Perf"/> and <see cref="PerfRgba16"/> already
/// measure — cold and steady time, process-wide allocation, peak working set — and each section ends with its
/// own geometric-mean verdict. The mode exists so the numbers land side by side and the size-dependence the
/// single-size runs hide becomes visible: several cases flip sign between 640×360 and 4K, so one resolution
/// has never been a conclusion.
/// </para>
/// <para>
/// It is deliberately opt-in and slow (minutes, not seconds): six full A/B sweeps.
/// </para>
/// </remarks>
internal static class Matrix
{
    /// <summary>Small, common, and large — the three points the flip-flopping cases were observed between.</summary>
    private static readonly (int Width, int Height)[] Sizes =
        [(640, 360), (1920, 1080), (3840, 2160), (7680, 4320)];

    public static void Write(Report report)
    {
        report.Heading(2, "尺寸 × 工作格式矩阵");
        report.Line("> 每个单元格都是一次完整的同进程 A/B（cold + steady 7 轮交错、分配为进程级净值、峰值 WS 子进程隔离），"
                  + "与 `perf` / `perf16` 单尺寸模式的尺子完全相同。");
        report.Line("> **不要拿单一尺寸下结论**：仓库里 17 项中有 7 项在 640×360 与 4K 之间翻面。");
        report.Line();

        foreach (var (width, height) in Sizes)
        {
            var pixels = width * height / 1_000_000.0;
            report.Heading(3, $"{width}×{height}（{pixels.ToString("F2", CultureInfo.InvariantCulture)} MPix）");

            Console.WriteLine($"[matrix] Rgba8  {width}x{height}...");
            Perf.Write(report, width, height);

            Console.WriteLine($"[matrix] Rgba16 {width}x{height}...");
            PerfRgba16.Write(report, width, height);

            report.Line();
        }

        // Quality is a knob, and it interacts with size: a sigma that is cheap at 1080p is not at 8K. The sweep
        // therefore runs at every size rather than once at 1080p.
        foreach (var (width, height) in Sizes)
        {
            Console.WriteLine($"[matrix] 参数档位 {width}x{height}...");
            ParameterSweep(report, width, height);
        }
    }

    /// <summary>
    /// The same two operators at several parameter levels, because "quality" is a knob here, not a constant:
    /// a blur at sigma 1 and at sigma 8 are different amounts of work, and a 16-colour palette is a different
    /// search than a 256-colour one.
    /// </summary>
    private static void ParameterSweep(Report report, int width, int height)
    {
        report.Heading(3, $"参数档位（{width}×{height}）");
        report.Line("> 与上面同一把尺子：cold + steady 7 轮交错，分配为进程级净值，比值为对手 ÷ 我方（>1 我方更快）。");

        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);

        var sweeps = new (string Id, Func<OurImage, OurImage> Ours, Func<TheirImage, TheirImage> Theirs)[]
        {
            ("blur sigma=1", i => i.GaussianBlur(1f), i => i.Clone(x => x.GaussianBlur(1f))),
            ("blur sigma=3", i => i.GaussianBlur(3f), i => i.Clone(x => x.GaussianBlur(3f))),
            ("blur sigma=8", i => i.GaussianBlur(8f), i => i.Clone(x => x.GaussianBlur(8f))),
            ("quantize 16", i => i.Quantize(16, SimdImaging.Processing.Quantizer.Wu),
                i => i.Clone(x => x.Quantize(new SixLabors.ImageSharp.Processing.Processors.Quantization.WuQuantizer(new QuantizerOptions { MaxColors = 16 })))),
            ("quantize 64", i => i.Quantize(64, SimdImaging.Processing.Quantizer.Wu),
                i => i.Clone(x => x.Quantize(new SixLabors.ImageSharp.Processing.Processors.Quantization.WuQuantizer(new QuantizerOptions { MaxColors = 64 })))),
            ("quantize 256", i => i.Quantize(256, SimdImaging.Processing.Quantizer.Wu),
                i => i.Clone(x => x.Quantize(new SixLabors.ImageSharp.Processing.Processors.Quantization.WuQuantizer(new QuantizerOptions { MaxColors = 256 })))),
        };

        var rows = new List<string[]>();
        foreach (var (id, ourOp, theirOp) in sweeps)
        {
            var allocated = new Dictionary<string, long>();
            var runners = new[]
            {
                new Runner("ours", () => { using var r = ourOp(ours); GC.KeepAlive(r); }),
                new Runner("theirs", () => { using var r = theirOp(theirs); GC.KeepAlive(r); }),
            };

            Dictionary<string, double> times;
            try
            {
                times = Timing.Time(runners, allocated: allocated);
            }
            catch (Exception e)
            {
                rows.Add([id, "—", "—", "—", "失败: " + e.GetType().Name]);
                continue;
            }

            allocated.TryGetValue("ours", out var ourAlloc);
            allocated.TryGetValue("theirs", out var theirAlloc);
            rows.Add([id, Timing.F(times["ours"]), Timing.F(times["theirs"]),
                (times["theirs"] / times["ours"]).ToString("F3", CultureInfo.InvariantCulture) + "×",
                $"{ourAlloc / 1024.0 / 1024.0:F1} / {theirAlloc / 1024.0 / 1024.0:F1} MiB"]);
        }

        report.Table(["档位", "我方 ms", "对手 ms", "比值", "分配 MiB（我 / 对）"], rows);
    }
}
