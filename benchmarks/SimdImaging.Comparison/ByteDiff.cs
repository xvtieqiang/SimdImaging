using System.Security.Cryptography;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// One hash per operator and parameter set, so a change's effect on the output bytes is a diff of two runs rather
/// than a claim. Run it on one tree, stash, run it again, compare.
/// </summary>
internal static class ByteDiff
{
    public static void Run(int width, int height)
    {
        var data = Fixture.Make(width, height);
        using var image = Fixture.Ours(data, width, height);
        var view = image[0].Pixels.View;

        var cases = new (string Name, Func<WorkingBuffer> Run)[]
        {
            ("emboss 0.6", () => FilterOps.Emboss(view, 0.6f, null)),
            ("emboss 1.0", () => FilterOps.Emboss(view, 1f, null)),
            ("erode", () => FilterOps.Erode3x3(view, null)),
            ("dilate", () => FilterOps.Dilate3x3(view, null)),
            ("sharpen 0.6/1", () => FilterOps.Sharpen(view, 0.6f, 1f, null)),
            ("sharpen 1/1.5", () => FilterOps.Sharpen(view, 1f, 1.5f, null)),
            ("glow 0.6/6/0.8", () => FilterOps.Glow(view, 0.6f, 6f, 0.8f, null)),
            ("glow 0.7/2/0.5", () => FilterOps.Glow(view, 0.7f, 2f, 0.5f, null)),
            ("vignette 0.75/1", () => FilterOps.Vignette(view, 0.75f, 1f, null)),
            ("vignette 0.5/0.5", () => FilterOps.Vignette(view, 0.5f, 0.5f, null)),
            ("median 2", () => FilterOps.Median(view, 2, null)),
            ("median 3", () => FilterOps.Median(view, 3, null)),
            ("median3x3", () => FilterOps.Median3x3(view, null)),
            ("sobel", () => FilterOps.Sobel(view, false, null)),
            ("convolve clamp", () => Convolve(view, BorderMode.Clamp)),
            ("convolve mirror", () => Convolve(view, BorderMode.Mirror)),
            ("convolve wrap", () => Convolve(view, BorderMode.Wrap)),
            ("convolve constant", () => Convolve(view, BorderMode.Constant)),
            ("box-blur 5", () => FilterOps.BoxBlur(view, 5, null)),
            ("gaussian 2", () => FilterOps.GaussianBlur(view, 2f, null)),
            ("invert", () => PointOps.Invert(view)),
            ("brightness", () => PointOps.Brightness(view, 0.05f)),
        };

        foreach (var (name, run) in cases)
        {
            using var result = run();
            Console.WriteLine($"{name,-20} {Convert.ToHexString(SHA256.HashData(result.View.Span))[..16]}");
        }
    }

    private static WorkingBuffer Convolve(PixelView view, BorderMode border)
    {
        var kernel = new float[25];
        Array.Fill(kernel, 0.04f);
        return FilterOps.Convolve(view, kernel, 2, border, 0.01f, 0.25f, null);
    }
}
