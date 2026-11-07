using System.Globalization;
using System.Runtime.InteropServices;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SixLabors.ImageSharp.Processing;
using TheirImage = SixLabors.ImageSharp.Image;
using TheirPixel = SixLabors.ImageSharp.PixelFormats.Rgba64;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// The Rgba8 table answers "is the shape fast"; this one answers "does it stay fast when a sample is 16 bits".
/// Rgba16 doubles the bytes moved per pixel and routes the row readers through a different switch arm, so the
/// balance between the compute and the memory traffic changes — that has to be measured, not extrapolated.
/// <para>The oracle side uses <c>Rgba64</c>, which is the same 16-bit-per-channel layout. Both sides still
/// allocate a new image per call, so the numbers are end-to-end the same way the Rgba8 table is.</para>
/// </summary>
internal static class PerfRgba16
{
    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, "D 组 · Rgba16 端到端同进程 A/B");
        report.Line($"> 输入 {width}×{height} `Rgba16`（每样本 16 位，每像素 8 字节）；对手用 `Rgba64`，同为 16 位/通道。");
        report.Line("> 口径与 Rgba8 表一致：两边都产生新图、含输出缓冲分配，热身 ≥300 ms，交错轮次取中位数，加速比 = 对手 ÷ 我方。");
        report.Line();

        var data = MakeSamples(width, height);
        var pixels = ToPixels(data);

        using var ours = CreateOurs(data, width, height);
        using var theirs = TheirImage.LoadPixelData<TheirPixel>(Fixture.OracleConfiguration, pixels, width, height);

        var cases = new (string Name, string Semantics, Func<OurImage, OurImage> Ours,
            Func<TheirImage, TheirImage> Theirs)[]
        {
            ("gaussian-blur", "Gaussian blur, sigma 3",
                i => i.GaussianBlur(3f), i => i.Clone(x => x.GaussianBlur(3f))),
            ("box-blur", "Box blur, radius 3",
                i => i.BoxBlur(3), i => i.Clone(x => x.BoxBlur(3))),
            ("grayscale", "Grayscale (BT.601)",
                i => i.Grayscale(),
                i => i.Clone(x => x.Grayscale(GrayscaleMode.Bt601))),
            ("sepia", "Sepia, full strength",
                i => i.Sepia(1f), i => i.Clone(x => x.Sepia(1f))),
            ("invert", "Invert colour channels",
                i => i.Invert(), i => i.Clone(x => x.Invert())),
            ("resize-bicubic", $"Resize {width}x{height} -> {width / 2}x{height / 2}, bicubic",
                i => i.Resize(width / 2, height / 2, Resampler.Bicubic),
                i => i.Clone(x => x.Resize(width / 2, height / 2, KnownResamplers.Bicubic))),
            ("rotate90", "Rotate 90 degrees",
                i => i.Rotate90(), i => i.Clone(x => x.Rotate(RotateMode.Rotate90))),
        };

        var rows = new List<string[]>();
        double logSum = 0;
        var wins = 0;

        foreach (var testCase in cases)
        {
            var runners = new[]
            {
                new Runner("ours", () => { using var r = testCase.Ours(ours); GC.KeepAlive(r); }),
                new Runner("theirs", () => { using var r = testCase.Theirs(theirs); GC.KeepAlive(r); }),
            };

            var allocated = new Dictionary<string, long>();
            var times = Timing.Time(runners, rounds: 7, allocated: allocated);
            var ratio = times["theirs"] / times["ours"];
            logSum += Math.Log(ratio);
            if (ratio > 1) wins++;

            rows.Add([testCase.Name, testCase.Semantics, Timing.F(times["ours"]), Timing.F(times["theirs"]),
                ratio.ToString("F2", CultureInfo.InvariantCulture) + "×",
                (allocated["ours"] / 1024).ToString(CultureInfo.InvariantCulture) + " KiB",
                (allocated["theirs"] / 1024).ToString(CultureInfo.InvariantCulture) + " KiB",
                ratio > 1 ? "我方快" : "对手快"]);
        }

        report.Table(["场景", "语义", "我方 ms", "对手 ms", "加速比", "我方分配/op", "对手分配/op", "胜负"], rows);
        report.Line($"**Rgba16 超越判定：{(Math.Exp(logSum / cases.Length)).ToString("F4", CultureInfo.InvariantCulture)}×**（{wins} / {cases.Length} 项领先）。");
        report.Line("> 两边都是克隆/产生新图，含输出缓冲分配；对手 `Grayscale` 用 `GrayscaleMode.Bt601` 对齐我方 BT.601 权重。");
        report.Line();
    }

    private static byte[] MakeSamples(int width, int height)
    {
        var bytes = new byte[checked(width * height * 8)];
        var samples = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            samples[i] = (ushort)((x * 131 + y * 977) & 0xFFFF);
            samples[i + 1] = (ushort)((x * x / 3 + y * 61) & 0xFFFF);
            samples[i + 2] = (ushort)(((x ^ y) * 401 + 12000) & 0xFFFF);
            samples[i + 3] = (ushort)(48000 + (x + y) % 16000);
        }

        return bytes;
    }

    private static TheirPixel[] ToPixels(byte[] data)
    {
        var samples = MemoryMarshal.Cast<byte, ushort>(data.AsSpan());
        var pixels = new TheirPixel[samples.Length / 4];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new TheirPixel(samples[i * 4], samples[i * 4 + 1], samples[i * 4 + 2], samples[i * 4 + 3]);
        return pixels;
    }

    private static OurImage CreateOurs(byte[] data, int width, int height)
    {
        var image = OurImage.Create(width, height, WorkingFormat.Rgba16);
        data.AsSpan().CopyTo(image.WritablePixels);
        return image;
    }
}
