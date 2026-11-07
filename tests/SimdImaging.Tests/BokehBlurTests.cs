using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// A fixed reference for <c>BokehBlur</c>, written as the operator is implemented today: premultiplied read,
/// the disc offsets accumulated in list order, one multiply by <c>1 / count</c>, then unpremultiplied write.
///
/// <para>It exists so the kernel can be rewritten (shifted-row accumulation instead of per-pixel taps) with a
/// byte-for-byte net underneath. The rewrite hoists the per-tap <c>Math.Clamp</c> onto row offsets and runs the
/// horizontal shift eight floats at a time; both are exactly the kind of change that stays plausible while
/// being wrong at the edges, which is why the sizes below include a disc wider than the image.</para>
/// </summary>
public class BokehBlurTests
{
    [Theory]
    [InlineData(17, 11, 2)]
    [InlineData(23, 9, 3)]
    [InlineData(8, 8, 1)]
    [InlineData(5, 6, 4)]      // disc wider than the image: every tap clamps
    public void Bokeh_MatchesThePerPixelReference(int width, int height, int radius)
    {
        var source = MakeBytes(width, height);
        var expected = Reference(source, width, height, radius);

        using var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba8);
        source.AsSpan().CopyTo(image.WritablePixels);

        using var blurred = image.BokehBlur(radius);
        Assert.Equal(expected, blurred.WritablePixels.ToArray());
    }

    private static byte[] MakeBytes(int width, int height)
    {
        var data = new byte[width * height * 4];
        uint state = 0x1B873593;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)(state >> 16);
        }

        return data;
    }

    private static byte[] Reference(byte[] source, int width, int height, int radius)
    {
        // The same disc the operator builds: squared radius test, offsets in row-major order.
        var offsets = new List<(int Dx, int Dy)>();
        var squared = radius * radius;
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            if (dx * dx + dy * dy <= squared)
                offsets.Add((dx, dy));
        }

        var scale = 1f / offsets.Count;
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
            float sumR = 0, sumG = 0, sumB = 0, sumA = 0;
            foreach (var (dx, dy) in offsets)
            {
                var sy = Math.Clamp(y + dy, 0, height - 1);
                var sx = Math.Clamp(x + dx, 0, width - 1);
                var index = (sy * width + sx) * 4;
                sumR += premultiplied[index];
                sumG += premultiplied[index + 1];
                sumB += premultiplied[index + 2];
                sumA += premultiplied[index + 3];
            }

            var alphaOut = sumA * scale;
            var inverse = alphaOut > 0f ? 1f / alphaOut : 0f;
            var target = (y * width + x) * 4;
            result[target] = ToByte(sumR * scale * inverse);
            result[target + 1] = ToByte(sumG * scale * inverse);
            result[target + 2] = ToByte(sumB * scale * inverse);
            result[target + 3] = ToByte(alphaOut);
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
