using System.Diagnostics;
using System.Globalization;
using Sdcb.SimdCodecs.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>B1 output parity, B2 robustness, B3 basic reproducibility. Every number here comes from
/// an actual call into both libraries — nothing is inferred from documentation.</summary>
internal static class Parity
{
    public static void Write(Report report)
    {
        const int width = 256, height = 192;
        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);
        using var theirs = Fixture.Theirs(data, width, height);
        var cases = Cases.Build(width, height);

        report.Heading(2, "B 组 · 行为与质量");
        report.Heading(3, "B1 输出一致性对拍");
        report.Line($"> 输入：自生成确定性图案 {width}×{height} `Rgba8`，两边从**同一份字节**构造。");
        report.Line("> PSNR 只算 RGB、忽略 alpha（与 SimdCodecs `Contestants.PsnrRgb` 同定义）。重采样核与参数单位不同导致的偏差是**事实**，照实列出，不调黄金、不掩盖。");
        report.Line();

        var parityRows = new List<string[]>();
        foreach (var c in cases)
        {
            byte[] a, b;
            string theirShape, ourShape;
            try
            {
                using var oursResult = c.Ours(ours);
                a = Fixture.Bytes(oursResult);
                ourShape = $"{oursResult.Width}x{oursResult.Height}";
            }
            catch (Exception e)
            {
                parityRows.Add([c.Id, c.Description, "—", "—", "—", "—", "—", "我方抛 " + e.GetType().Name]);
                continue;
            }

            try
            {
                using var theirsResult = c.Theirs(theirs);
                b = Fixture.Bytes(theirsResult);
                theirShape = $"{theirsResult.Width}x{theirsResult.Height}";
            }
            catch (Exception e)
            {
                parityRows.Add([c.Id, c.Description, "—", "—", "—", "—", "—", "对手抛 " + e.GetType().Name]);
                continue;
            }

            if (a.Length != b.Length)
            {
                parityRows.Add([c.Id, c.Description, ourShape, theirShape, "—", "—", "—", "尺寸不一致"]);
                continue;
            }

            var p = Metrics.Compare(a, b);
            var psnr = double.IsPositiveInfinity(p.PsnrRgb) ? "∞" :
                double.IsNaN(p.PsnrRgb) ? "—" : p.PsnrRgb.ToString("F2", CultureInfo.InvariantCulture);
            parityRows.Add([c.Id, c.Description, ourShape, theirShape,
                p.DifferentPixels.ToString(CultureInfo.InvariantCulture),
                p.MaxChannelDiff.ToString(CultureInfo.InvariantCulture),
                p.MeanAbsDiff.ToString("F2", CultureInfo.InvariantCulture) + " / " + psnr,
                Metrics.Grade(p, (long)width * height)]);
        }

        report.Table(["场景", "语义", "我方尺寸", "对手尺寸", "不同像素", "最大通道差", "平均差 / PSNR(dB)", "判定"], parityRows);

        report.Heading(3, "B2 鲁棒性与安全（非法参数对照）");
        report.Line("> 同一组非法输入喂给两边，记录异常类型与耗时。**静默接受**比抛异常更危险，单独标出。");
        report.Line();
        var robustness = new (string Name, Action Ours, Action Theirs)[]
        {
            ("BoxBlur(-1)", () => { using var r = ours.BoxBlur(-1); },
                () => { using var r = theirs.Clone(x => x.BoxBlur(-1)); }),
            ("GaussianBlur(NaN)", () => { using var r = ours.GaussianBlur(float.NaN); },
                () => { using var r = theirs.Clone(x => x.GaussianBlur(float.NaN)); }),
            ("Brightness(NaN)", () => { using var r = ours.Brightness(float.NaN); },
                () => { using var r = theirs.Clone(x => x.Brightness(float.NaN)); }),
            ("Resize(0,0)", () => { using var r = ours.Resize(0, 0); },
                () => { using var r = theirs.Clone(x => x.Resize(0, 0)); }),
            ("Crop(0x0)", () => { using var r = ours.Crop(new PixelRect(0, 0, 0, 0)); },
                () => { using var r = theirs.Clone(x => x.Crop(new Rectangle(0, 0, 0, 0))); }),
        };
        var robustRows = new List<string[]>();
        foreach (var (name, ourAction, theirAction) in robustness)
        {
            var (ourResult, ourMs) = Capture(ourAction);
            var (theirResult, theirMs) = Capture(theirAction);
            var verdict = ourResult.StartsWith("接受", StringComparison.Ordinal) || theirResult.StartsWith("接受", StringComparison.Ordinal)
                ? "⚠ 存在静默接受"
                : "双方均拒绝";
            robustRows.Add([name, ourResult, theirResult,
                ourMs.ToString("F2", CultureInfo.InvariantCulture), theirMs.ToString("F2", CultureInfo.InvariantCulture), verdict]);
        }

        report.Table(["非法输入", "我方", "对手", "我方 ms", "对手 ms", "判定"], robustRows);

        report.Heading(3, "B3 可复现性（同进程重跑两次）");
        report.Line("> 我方跨 SIMD 档位的逐字节一致性由 CI 四档矩阵 + AOT 冒烟覆盖，结果见本报告 C1 与「外部命令实测」章节；这里只测同档位重跑。");
        report.Line();
        var reproRows = new List<string[]>();
        foreach (var c in cases.Where(c => c.Id is "invert" or "resize-bicubic" or "gaussian-blur"))
        {
            using var o1 = c.Ours(ours); using var o2 = c.Ours(ours);
            using var t1 = c.Theirs(theirs); using var t2 = c.Theirs(theirs);
            var ourSame = Fixture.Bytes(o1).AsSpan().SequenceEqual(Fixture.Bytes(o2));
            var theirSame = Fixture.Bytes(t1).AsSpan().SequenceEqual(Fixture.Bytes(t2));
            reproRows.Add([c.Id, ourSame ? "逐字节一致" : "不一致", theirSame ? "逐字节一致" : "不一致"]);
        }

        report.Table(["场景", "SimdImaging 重跑", "ImageSharp 4.1.3 重跑"], reproRows);
    }

    private static (string Result, double Milliseconds) Capture(Action action)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            action();
            watch.Stop();
            return ("接受（无异常）", watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception e)
        {
            watch.Stop();
            return (e.GetType().Name, watch.Elapsed.TotalMilliseconds);
        }
    }
}
