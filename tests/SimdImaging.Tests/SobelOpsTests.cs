using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Sobel was rewritten for speed: the luma of every row used to be recomputed three times, `outputRow` was
/// allocated once per row (~8 MB of garbage for one 1080p image) and the luma spans were rebuilt inside the
/// per-pixel loop. It now keeps a three-row luma ring per band, computed once per row.
///
/// <para>The ring is where an off-by-one would hide — the top and bottom rows clamp to the edge, so index
/// <c>-1</c> has to land on the same ring slot the reader looks at. The behavioural tests only assert values
/// in the middle of an image, so this compares every pixel of a small one against the old algorithm.</para>
/// </summary>
public class SobelOpsTests
{
    [Theory]
    [InlineData(17, 11)]
    [InlineData(9, 4)]       // shorter than three rows
    [InlineData(40, 25)]
    public void Sobel_MatchesTheUncachedReference(int width, int height)
    {
        var source = MakeBytes(width, height);
        var expected = Reference(source, width, height, bt709: false);

        using var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba8);
        source.AsSpan().CopyTo(image.WritablePixels);

        using var edges = image.Sobel();
        Assert.Equal(expected, edges.WritablePixels.ToArray());
    }

    private static byte[] MakeBytes(int width, int height)
    {
        var data = new byte[width * height * 4];
        uint state = 0x68E31DA4;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)(state >> 16);
        }

        return data;
    }

    /// <summary>The previous shape, verbatim: recompute the three luma rows for every output row.</summary>
    private static byte[] Reference(byte[] source, int width, int height, bool bt709)
    {
        const float Scale = 1f / 255f;
        var (lr, lg, lb) = bt709 ? (0.2126f, 0.7152f, 0.0722f) : (0.299f, 0.587f, 0.114f);
        var luma = new float[3 * width];
        var result = new byte[source.Length];

        for (var y = 0; y < height; y++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                var sy = Math.Clamp(y + dy, 0, height - 1);
                var target = dy + 1;
                for (var x = 0; x < width; x++)
                {
                    var o = (sy * width + x) * 4;
                    luma[target * width + x] = source[o] * Scale * lr
                                             + source[o + 1] * Scale * lg
                                             + source[o + 2] * Scale * lb;
                }
            }

            var top = luma.AsSpan(0, width);
            var middle = luma.AsSpan(width, width);
            var bottom = luma.AsSpan(2 * width, width);

            for (var x = 0; x < width; x++)
            {
                var left = Math.Clamp(x - 1, 0, width - 1);
                var right = Math.Clamp(x + 1, 0, width - 1);

                var gx = (top[right] + 2f * middle[right] + bottom[right])
                         - (top[left] + 2f * middle[left] + bottom[left]);
                var gy = (bottom[left] + 2f * bottom[x] + bottom[right])
                         - (top[left] + 2f * top[x] + top[right]);

                var magnitude = MathF.Sqrt(gx * gx + gy * gy) * 0.25f;
                if (magnitude > 1f)
                    magnitude = 1f;

                var offset = (y * width + x) * 4;
                result[offset] = ToByte(magnitude);
                result[offset + 1] = ToByte(magnitude);
                result[offset + 2] = ToByte(magnitude);
                result[offset + 3] = source[offset + 3];
            }
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
