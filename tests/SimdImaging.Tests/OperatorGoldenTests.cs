using System.Security.Cryptography;
using System.Text;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// Every operator that a performance change touches must keep producing the same bytes. The suite proves behaviour
/// (two paths agree, a naive reference agrees); this proves the <b>output value</b> did not move between commits,
/// which no path-comparison can see.
/// </summary>
/// <remarks>
/// <para>
/// The file is <c>tests/Fixtures/operator-golden.txt</c>, one <c>name TAB hash</c> per line. It is deliberately
/// checked in: changing a hash has to be an edit someone reviews, not a side effect of a benchmark run.
/// </para>
/// <para>
/// To update it after a change that <b>intends</b> to alter output, run the suite once with
/// <c>SIMDIMAGING_WRITE_GOLDEN=1</c> and read the diff of that file — that diff is the complete list of operators
/// whose bytes moved. Never set the variable in CI.
/// </para>
/// </remarks>
public sealed class OperatorGoldenTests
{
    private static readonly string GoldenPath =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "operator-golden.txt");

    /// <summary>The operators this round touched, plus controls that must never move.</summary>
    private static readonly (string Name, Func<PixelView, WorkingBuffer> Run)[] Cases =
    [
        ("emboss 0.6", v => FilterOps.Emboss(v, 0.6f, null)),
        ("emboss 1.0", v => FilterOps.Emboss(v, 1f, null)),
        ("erode", v => FilterOps.Erode3x3(v, null)),
        ("dilate", v => FilterOps.Dilate3x3(v, null)),
        ("sharpen 0.6/1", v => FilterOps.Sharpen(v, 0.6f, 1f, null)),
        ("sharpen 1/1.5", v => FilterOps.Sharpen(v, 1f, 1.5f, null)),
        ("glow 0.6/6/0.8", v => FilterOps.Glow(v, 0.6f, 6f, 0.8f, null)),
        ("glow 0.7/2/0.5", v => FilterOps.Glow(v, 0.7f, 2f, 0.5f, null)),
        ("vignette 0.75/1", v => FilterOps.Vignette(v, 0.75f, 1f, null)),
        ("vignette 0.5/0.5", v => FilterOps.Vignette(v, 0.5f, 0.5f, null)),
        ("median 2", v => FilterOps.Median(v, 2, null)),
        ("median 3", v => FilterOps.Median(v, 3, null)),
        ("median3x3", v => FilterOps.Median3x3(v, null)),
        ("sobel", v => FilterOps.Sobel(v, false, null)),
        ("convolve clamp", v => Convolve(v, BorderMode.Clamp)),
        ("convolve mirror", v => Convolve(v, BorderMode.Mirror)),
        ("convolve wrap", v => Convolve(v, BorderMode.Wrap)),
        ("convolve constant", v => Convolve(v, BorderMode.Constant)),
        ("box-blur 5", v => FilterOps.BoxBlur(v, 5, null)),
        ("gaussian 2", v => FilterOps.GaussianBlur(v, 2f, null)),
        ("invert", v => PointOps.Invert(v)),
        ("brightness", v => PointOps.Brightness(v, 0.05f)),
    ];

    [Fact]
    public void EveryOperatorProducesTheStoredHash()
    {
        var lines = ActualLines();

        if (Environment.GetEnvironmentVariable("SIMDIMAGING_WRITE_GOLDEN") == "1")
        {
            File.WriteAllLines(GoldenPath, lines, new UTF8Encoding(false));
            return;
        }

        var expected = ReadGolden();
        var moved = new List<string>();
        foreach (var line in lines)
        {
            var name = line[..line.IndexOf('\t')];
            if (!expected.TryGetValue(name, out var hash))
            {
                moved.Add($"{name}: not in the golden file");
                continue;
            }

            if (hash != line[(line.IndexOf('\t') + 1)..])
                moved.Add($"{name}: {hash} -> {line[(line.IndexOf('\t') + 1)..]}");
        }

        moved.ShouldBeEmpty(
            "Output bytes moved. If that was intended, run with SIMDIMAGING_WRITE_GOLDEN=1 and review the diff of "
            + "tests/Fixtures/operator-golden.txt; otherwise the change is a regression:/n  "
            + string.Join("\n  ", moved));
    }

    [Fact]
    public void TheGoldenFileCoversEveryCase()
    {
        if (!File.Exists(GoldenPath))
            return;

        var expected = ReadGolden();
        var missing = Cases.Select(c => c.Name).Where(n => !expected.ContainsKey(n)).ToList();
        missing.ShouldBeEmpty("the golden file is missing cases: " + string.Join(", ", missing));
    }

    private static List<string> ActualLines()
    {
        using var image = Pattern(1920, 1080);
        var view = image[0].Pixels.View;

        var lines = new List<string>(Cases.Length);
        foreach (var (name, run) in Cases)
        {
            using var result = run(view);
            lines.Add($"{name}\t{Convert.ToHexString(SHA256.HashData(result.View.Span))}");
        }

        return lines;
    }

    private static Dictionary<string, string> ReadGolden()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(GoldenPath))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var tab = line.IndexOf('\t');
            if (tab > 0)
                map[line[..tab]] = line[(tab + 1)..];
        }

        return map;
    }

    /// <summary>
    /// Deterministic, and shaped to exercise the paths these operators branch on: a horizontal and a vertical ramp,
    /// a channel that cycles through every value, and a few semi-transparent pixels so the premultiply round trip is
    /// covered as well.
    /// </summary>
    private static Image Pattern(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (byte)((x * 255) / (width - 1));
            pixels[i + 1] = (byte)((y * 255) / (height - 1));
            pixels[i + 2] = (byte)((x * 7) + (y * 13));
            pixels[i + 3] = (byte)(((x + y) % 97) == 0 ? 128 : 255);
        }

        return image;
    }

    private static WorkingBuffer Convolve(PixelView view, BorderMode border)
    {
        var kernel = new float[25];
        Array.Fill(kernel, 0.04f);
        return FilterOps.Convolve(view, kernel, 2, border, 0.01f, 0.25f, null);
    }
}
