using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// <c>Convolve2D</c> is the shared 2D path behind GaussianSharpen and Emboss. It now accumulates shifted rows
/// (one row clamp per tap, eight floats per vector op) instead of stepping every tap per pixel. Taps are still
/// visited ky-major / kx-minor, so the sums should be bit-identical — this holds it against the per-pixel
/// version, including images narrower than the kernel where every tap clamps.
/// </summary>
public class Convolve2DTests
{
    [Theory]
    [InlineData(15, 11, 1f, 0.8f)]
    [InlineData(9, 13, 1.5f, 0.5f)]
    [InlineData(6, 5, 1f, 1f)]       // radius 3 against a 6-px wide image: everything clamps
    public void GaussianSharpen_MatchesThePerPixelReference(int width, int height, float sigma, float amount)
    {
        var source = MakeBytes(width, height);
        var expected = Reference(source, width, height, sigma, amount);

        using var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba8);
        source.AsSpan().CopyTo(image.WritablePixels);

        using var sharpened = image.GaussianSharpen(amount, sigma);
        Assert.Equal(expected, sharpened.WritablePixels.ToArray());
    }

    private static byte[] MakeBytes(int width, int height)
    {
        var data = new byte[width * height * 4];
        uint state = 0xC2B2AE35;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)(state >> 16);
        }

        return data;
    }

    /// <summary>The kernel GaussianSharpen builds, then the old per-pixel convolution.</summary>
    private static byte[] Reference(byte[] source, int width, int height, float sigma, float amount)
    {
        var radius = Math.Max(1, (int)MathF.Ceiling(3f * sigma));
        var side = radius * 2 + 1;

        var gaussian = new float[side];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var weight = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            gaussian[i + radius] = weight;
            sum += weight;
        }

        for (var i = 0; i < side; i++)
            gaussian[i] /= sum;

        var kernel = new float[side * side];
        for (var ky = 0; ky < side; ky++)
        for (var kx = 0; kx < side; kx++)
            kernel[ky * side + kx] = -amount * gaussian[ky] * gaussian[kx];
        kernel[radius * side + radius] = 1f + amount - amount * gaussian[radius] * gaussian[radius];

        var premultiplied = new float[width * height * 4];
        for (var i = 0; i < premultiplied.Length; i += 4)
        {
            var alpha = source[i + 3] * (1f / 255f);
            premultiplied[i] = source[i] * (1f / 255f) * alpha;
            premultiplied[i + 1] = source[i + 1] * (1f / 255f) * alpha;
            premultiplied[i + 2] = source[i + 2] * (1f / 255f) * alpha;
            premultiplied[i + 3] = alpha;
        }

        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            float sumR = 0f, sumG = 0f, sumB = 0f;
            for (var ky = 0; ky < side; ky++)
            {
                var sy = Math.Clamp(y + ky - radius, 0, height - 1);
                for (var kx = 0; kx < side; kx++)
                {
                    var weight = kernel[ky * side + kx];
                    var sx = Math.Clamp(x + kx - radius, 0, width - 1);
                    var index = (sy * width + sx) * 4;
                    sumR += weight * premultiplied[index];
                    sumG += weight * premultiplied[index + 1];
                    sumB += weight * premultiplied[index + 2];
                }
            }

            var centre = (y * width + x) * 4;
            var alpha = premultiplied[centre + 3];
            var inverse = alpha > 0f ? 1f / alpha : 0f;
            result[centre] = ToByte(sumR * inverse);
            result[centre + 1] = ToByte(sumG * inverse);
            result[centre + 2] = ToByte(sumB * inverse);
            result[centre + 3] = ToByte(alpha);
        }

        return result;
    }

    private static byte ToByte(float value)
    {
        var scaled = value * 255f + 0.5f;
        if (scaled <= 0f) return 0;
        if (scaled >= 255f) return 255;
        return (byte)scaled;
    }
}
