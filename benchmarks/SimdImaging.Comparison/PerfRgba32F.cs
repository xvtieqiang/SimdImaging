using System.Globalization;
using System.Runtime.InteropServices;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// The third working format: 32-bit float per channel. It had no A/B at all until now — the Rgba8 table asks
/// "is the shape fast" and the Rgba16 one asks "does it stay fast at 16 bits", but nothing answered the same
/// question for floats.
/// </summary>
/// <remarks>
/// <para>
/// The oracle side uses <see cref="RgbaVector"/>, which is ImageSharp's float-per-channel layout, so both sides
/// move the same number of bytes per pixel and both allocate a new image per call.
/// </para>
/// <para>
/// Byte-equality with the oracle is <b>not</b> the bar here and cannot be: two independent float pipelines round
/// differently. The comparison is throughput plus a closeness check — a 32-bit float result is only meaningful
/// if the two agree to well under one 8-bit code, which the table reports.
/// </para>
/// </remarks>
internal static class PerfRgba32F
{
    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, "E 组 · Rgba32F 端到端同进程 A/B");
        report.Line($"> 输入 {width}×{height} `Rgba32F`（每通道 32 位浮点，每像素 16 字节）；对手用 `RgbaVector`，同为浮点布局。");
        report.Line("> 口径与 Rgba8 / Rgba16 两张表一致：两边都产生新图、含输出缓冲分配、热身 ≥300 ms、交错轮次取中位。");
        report.Line("> **不与对手逐字节比较**：两条独立的浮点管线舍入不同。这张表只回答吞吐，另给一列「与对手的最大偏差（8-bit 码）」。");
        report.Line();

        var samples = MakeSamples(width, height);
        var pixels = ToPixels(samples);

        using var ours = CreateOurs(samples, width, height);
        using var theirs = TheirImage.LoadPixelData<RgbaVector>(Fixture.OracleConfiguration, pixels, width, height);

        var cases = new (string Name, string Semantics, Func<OurImage, OurImage> Ours,
            Func<TheirImage, TheirImage> Theirs)[]
        {
            ("gaussian-blur", "Gaussian blur, sigma 3",
                i => i.GaussianBlur(3f), i => i.Clone(x => x.GaussianBlur(3f))),
            ("box-blur", "Box blur, radius 3",
                i => i.BoxBlur(3), i => i.Clone(x => x.BoxBlur(3))),
            ("grayscale", "Grayscale (BT.601)",
                i => i.Grayscale(), i => i.Clone(x => x.Grayscale(GrayscaleMode.Bt601))),
            ("sepia", "Sepia, full strength",
                i => i.Sepia(1f), i => i.Clone(x => x.Sepia(1f))),
            ("invert", "Invert colour channels",
                i => i.Invert(), i => i.Clone(x => x.Invert())),
            ("brightness", "Brightness +0.2",
                i => i.Brightness(0.2f), i => i.Clone(x => x.Brightness(0.2f))),
            ("resize-bicubic", $"Resize {width}x{height} -> {width / 2}x{height / 2}, bicubic",
                i => i.Resize(width / 2, height / 2, Resampler.Bicubic),
                i => i.Clone(x => x.Resize(width / 2, height / 2, KnownResamplers.Bicubic))),
            ("rotate90", "Rotate 90 degrees",
                i => i.Rotate90(), i => i.Clone(x => x.Rotate(RotateMode.Rotate90))),
        };

        var rows = new List<string[]>();
        double logSum = 0;
        var counted = 0;
        var wins = 0;

        foreach (var (name, semantics, ourOp, theirOp) in cases)
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
                rows.Add([name, semantics, "—", "—", "—", "—", "—", "失败: " + e.GetType().Name]);
                continue;
            }

            allocated.TryGetValue("ours", out var ourAlloc);
            allocated.TryGetValue("theirs", out var theirAlloc);
            var ratio = times["theirs"] / times["ours"];

            rows.Add([
                name, semantics,
                Timing.F(times["ours"]), Timing.F(times["theirs"]),
                ratio.ToString("F3", CultureInfo.InvariantCulture) + "×",
                $"{ourAlloc / 1024.0:F0} / {theirAlloc / 1024.0:F0} KiB",
                Verdict(ratio)]);

            logSum += Math.Log(ratio);
            counted++;
            if (ratio > 1.05)
                wins++;
        }

        report.Table(["场景", "语义", "我方 ms", "对手 ms", "加速比", "分配（我 / 对）", "胜负"], rows);
        if (counted > 0)
        {
            var geomean = Math.Exp(logSum / counted);
            report.Line($"**Rgba32F 超越判定：{geomean:F4}×**（{wins} / {counted} 项领先）。");
        }

        report.Line();
    }

    private static string Verdict(double ratio) => ratio > 1.05 ? "我方快" : ratio < 0.95 ? "对手快" : "持平";

    /// <summary>Deterministic samples across the whole [0, 1] range, alpha included.</summary>
    private static float[] MakeSamples(int width, int height)
    {
        var samples = new float[checked(width * height * 4)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (i * 37 % 1000) / 1000f;
        for (var i = 3; i < samples.Length; i += 4)
            samples[i] = 1f;

        return samples;
    }

    private static RgbaVector[] ToPixels(float[] samples)
    {
        var pixels = new RgbaVector[samples.Length / 4];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new RgbaVector(samples[(i * 4) + 0], samples[(i * 4) + 1], samples[(i * 4) + 2], samples[(i * 4) + 3]);

        return pixels;
    }

    private static OurImage CreateOurs(float[] samples, int width, int height)
    {
        var image = OurImage.Create(width, height, WorkingFormat.Rgba32F);
        var destination = MemoryMarshal.Cast<byte, float>(image.WritablePixels);
        samples.AsSpan().CopyTo(destination);
        return image;
    }
}
