using System.Buffers.Binary;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Checks the ICC transform without an oracle, because there is none: ImageSharp 4.1.3's profile-to-profile
/// conversion lives in <c>ColorProfileConverterExtensionsIcc</c>, which is not public, so the comparison harness
/// cannot call it and this repository does not read the reference implementation's internals.
/// </summary>
/// <remarks>
/// What is checked instead are properties that must hold for any correct implementation: converting a profile to
/// itself is the identity, converting out and back returns the original within a tight tolerance, and white stays
/// white (both profiles are D65 RGB, so a neutral maps to a neutral). Those catch a broken CLUT, a wrong matrix,
/// or a bad PCS seam without needing a second implementation to agree with.
/// </remarks>
internal static class IccCheck
{
    public static void Write(Report report)
    {
        var directory = Environment.GetEnvironmentVariable("SIMDIMAGING_ICC_DIR")
            ?? Path.Combine("tests", "Fixtures", ".cache", "icc");
        var srgbPath = Path.Combine(directory, "srgb-v4.icc");
        var p3Path = Path.Combine(directory, "DisplayP3-v4.icc");

        report.Heading(2, "K 组 · ICC 变换的自洽验证");
        if (!File.Exists(srgbPath) || !File.Exists(p3Path))
        {
            report.Line($"> 缺 profile：`{srgbPath}` / `{p3Path}`。CC0 来源见 `benchmarks/README.md`。");
            report.Line("> 对拍不可行的原因：ImageSharp 4.1.3 的 profile→profile 转换在 `ColorProfileConverterExtensionsIcc`，");
            report.Line("> 该类型 `public=False`，本仓库只比对公开面，所以这一项改为自洽验证。");
            return;
        }

        var srgb = File.ReadAllBytes(srgbPath);
        var p3 = File.ReadAllBytes(p3Path);
        using var swatches = Swatches();

        var rows = new List<string[]>();

        // 1. Identity.
        using (var identity = swatches.Pipe().IccTransform(srgb, srgb).ToImage())
            rows.Add(["同一 profile（恒等）", MaxDeltaE(swatches, identity), "应 ≈ 0"]);

        // 2. Round trip through Display P3.
        using (var toP3 = swatches.Pipe().IccTransform(srgb, p3).ToImage())
        using (var back = toP3.Pipe().IccTransform(p3, srgb).ToImage())
            rows.Add(["sRGB→P3→sRGB（往返）", MaxDeltaE(swatches, back), "应 < 1.5"]);

        // 3. Neutral stays neutral.
        rows.Add(["中性轴（白/灰）", Neutral(swatches, srgb, p3), "应 < 1.5"]);

        report.Table(["检查", "最大 ΔE2000", "判据"], rows);
        report.Line("> 三条都是**必要**条件，不是充分条件：它们能抓住坏的 CLUT、错的矩阵和 PCS 缝合错误，");
        report.Line("> 但无法证明与另一实现逐像素一致——后者需要对手公开 profile 级入口，目前做不到。");
    }

    /// <summary>A coarse sweep of the RGB cube, which is where a CLUT's tetrahedral interpolation shows up.</summary>
    private static Image Swatches()
    {
        var levels = new byte[] { 0, 32, 64, 96, 128, 160, 192, 224, 255 };
        var image = Image.Create(levels.Length * levels.Length, levels.Length, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var b = 0; b < levels.Length; b++)
        {
            for (var g = 0; g < levels.Length; g++)
            {
                for (var r = 0; r < levels.Length; r++)
                {
                    var x = (g * levels.Length) + r;
                    var o = ((b * image.Width) + x) * 4;
                    span[o] = levels[r];
                    span[o + 1] = levels[g];
                    span[o + 2] = levels[b];
                    span[o + 3] = 255;
                }
            }
        }

        return image;
    }

    private static string MaxDeltaE(Image expected, Image actual)
    {
        var a = expected.WritablePixels;
        var b = actual.WritablePixels;
        var worst = 0.0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var (l1, a1, b1) = Lab(a[i], a[i + 1], a[i + 2]);
            var (l2, a2, b2) = Lab(b[i], b[i + 1], b[i + 2]);
            worst = Math.Max(worst, ColorDifference.DeltaE2000(l1, a1, b1, l2, a2, b2));
        }

        return worst.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string Neutral(Image source, byte[] srgb, byte[] p3)
    {
        using var converted = source.Pipe().IccTransform(srgb, p3).ToImage();
        var expected = source.WritablePixels;
        var actual = converted.WritablePixels;
        var worst = 0.0;
        for (var i = 0; i < expected.Length; i += 4)
        {
            // Neutrals only: R = G = B.
            if (expected[i] != expected[i + 1] || expected[i + 1] != expected[i + 2])
                continue;

            var (l1, a1, b1) = Lab(expected[i], expected[i + 1], expected[i + 2]);
            var (l2, a2, b2) = Lab(actual[i], actual[i + 1], actual[i + 2]);
            worst = Math.Max(worst, ColorDifference.DeltaE2000(l1, a1, b1, l2, a2, b2));
        }

        return worst.ToString("F3", CultureInfo.InvariantCulture);
    }

    /// <summary>sRGB to CIE Lab (D65), the space ΔE2000 is defined in.</summary>
    private static (double L, double A, double B) Lab(byte r, byte g, byte b)
    {
        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        var rl = Linear(r / 255.0);
        var gl = Linear(g / 255.0);
        var bl = Linear(b / 255.0);

        var x = (0.4124564 * rl) + (0.3575761 * gl) + (0.1804375 * bl);
        var y = (0.2126729 * rl) + (0.7151522 * gl) + (0.0721750 * bl);
        var z = (0.0193339 * rl) + (0.1191920 * gl) + (0.9503041 * bl);

        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);

        var fx = F(x / 0.95047);
        var fy = F(y / 1.0);
        var fz = F(z / 1.08883);

        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}
