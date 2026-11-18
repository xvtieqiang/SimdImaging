using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Sobel, Prewitt and Scharr differ only in their weights, so they share one implementation — which is exactly why
/// the reference here computes each of them by hand. A shared kernel that quietly uses the wrong weights would still
/// pass a behavioural test ("it responds at an edge") and fail this one.
/// </summary>
public class GradientTests
{
    private static Image Pattern(int width = 19, int height = 11)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)((i * 61) % 251);

        return image;
    }

    private static byte[] Reference(Image image, float top, float middle, float bottom, float scale)
    {
        var width = image.Width;
        var height = image.Height;
        var source = image.WritablePixels.ToArray();

        var luma = new float[width * height];
        for (var i = 0; i < luma.Length; i++)
        {
            var o = i * 4;
            luma[i] = ((0.299f * source[o]) + (0.587f * source[o + 1]) + (0.114f * source[o + 2])) * (1f / 255f);
        }

        var expected = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var l = Math.Clamp(x - 1, 0, width - 1);
                var r = Math.Clamp(x + 1, 0, width - 1);
                var t = Math.Clamp(y - 1, 0, height - 1);
                var b = Math.Clamp(y + 1, 0, height - 1);

                var gx = ((top * luma[(t * width) + r]) + (middle * luma[(y * width) + r]) + (bottom * luma[(b * width) + r]))
                         - ((top * luma[(t * width) + l]) + (middle * luma[(y * width) + l]) + (bottom * luma[(b * width) + l]));
                var gy = ((top * luma[(b * width) + l]) + (middle * luma[(b * width) + x]) + (bottom * luma[(b * width) + r]))
                         - ((top * luma[(t * width) + l]) + (middle * luma[(t * width) + x]) + (bottom * luma[(t * width) + r]));

                var magnitude = MathF.Sqrt((gx * gx) + (gy * gy)) * scale;
                if (magnitude > 1f)
                    magnitude = 1f;

                var o = ((y * width) + x) * 4;
                var level = (byte)Math.Min(255f, (magnitude * 255f) + 0.5f);
                expected[o] = level;
                expected[o + 1] = level;
                expected[o + 2] = level;
                expected[o + 3] = source[o + 3];
            }
        }

        return expected;
    }

    private static void Check(Image image, Image actual, byte[] expected, string name)
    {
        var width = image.Width;
        for (var y = 0; y < image.Height; y++)
        {
            actual.WritablePixels.Slice(y * width * 4, width * 4).ToArray()
                .ShouldBe(expected.AsSpan(y * width * 4, width * 4).ToArray(), $"{name} row {y} differs");
        }
    }

    [Fact]
    public void Sobel_MatchesTheNaiveReference()
    {
        using var image = Pattern();
        using var actual = image.Pipe().Sobel().ToImage();
        Check(image, actual, Reference(image, 1f, 2f, 1f, 0.25f), "sobel");
    }

    [Fact]
    public void Prewitt_MatchesTheNaiveReference()
    {
        using var image = Pattern();
        using var actual = image.Pipe().Prewitt().ToImage();
        Check(image, actual, Reference(image, 1f, 1f, 1f, 1f / 3f), "prewitt");
    }

    [Fact]
    public void Scharr_MatchesTheNaiveReference()
    {
        using var image = Pattern();
        using var actual = image.Pipe().Scharr().ToImage();
        Check(image, actual, Reference(image, 3f, 10f, 3f, 1f / 16f), "scharr");
    }
}
