using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// A signed kernel is where an absolute value can be taken in the wrong place, so the reference here is explicit:
/// compute luma, difference it by hand, take the magnitude, clamp.
/// </summary>
public class LaplacianTests
{
    private static Image Pattern(int width = 23, int height = 15)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)((i * 53) % 251);

        return image;
    }

    [Fact]
    public void Laplacian_MatchesANaiveReference_ByteForByte()
    {
        using var image = Pattern();
        var width = image.Width;
        var height = image.Height;
        var source = image.WritablePixels.ToArray();

        // Luma, then the second difference with replicated edges.
        var luma = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 4;
                // The operator reads samples as 0..1 (byte * 1/255), so the reference must too.
                luma[(y * width) + x] = ((0.299f * source[o]) + (0.587f * source[o + 1]) + (0.114f * source[o + 2]))
                                        * (1f / 255f);
            }
        }

        var expected = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var left = Math.Clamp(x - 1, 0, width - 1);
                var right = Math.Clamp(x + 1, 0, width - 1);
                var top = Math.Clamp(y - 1, 0, height - 1);
                var bottom = Math.Clamp(y + 1, 0, height - 1);

                var value = (4f * luma[(y * width) + x])
                            - (luma[(y * width) + left] + luma[(y * width) + right]
                               + luma[(top * width) + x] + luma[(bottom * width) + x]);

                var magnitude = MathF.Abs(value);
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

        using var actual = image.Pipe().Laplacian().ToImage();
        for (var y = 0; y < height; y++)
        {
            actual.WritablePixels.Slice(y * width * 4, width * 4).ToArray()
                .ShouldBe(expected.AsSpan(y * width * 4, width * 4).ToArray(), $"row {y} differs");
        }
    }

    /// <summary>A flat image has no second derivative, and alpha passes through.</summary>
    [Fact]
    public void FlatImage_IsBlack_AndAlphaIsKept()
    {
        using var image = Image.Create(9, 7, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = 100;
            span[i + 1] = 120;
            span[i + 2] = 140;
            span[i + 3] = 77;
        }

        using var result = image.Pipe().Laplacian().ToImage();
        for (var i = 0; i < span.Length; i += 4)
        {
            result.WritablePixels[i].ShouldBe((byte)0);
            result.WritablePixels[i + 3].ShouldBe((byte)77);
        }
    }
}
