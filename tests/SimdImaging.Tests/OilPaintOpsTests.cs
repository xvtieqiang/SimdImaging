using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Oil paint used to recount its whole window at every pixel and clear four level-sized arrays each time. It
/// now seeds one window per row and slides it, removing the column that leaves and adding the one that enters.
/// That is only equivalent because every counter is an integer, so a removal undoes an addition exactly — and
/// because the clamping is mirrored: the column removed is <c>clamp(x-1-radius)</c>, not <c>x-1-radius</c>.
///
/// <para>The reference below is the old algorithm, verbatim: clear, recount all (2r+1)² samples, pick the
/// lowest bin with the highest count. If the slide ever drifts by one sample the tallies diverge and this
/// fails, which is the whole point of keeping it.</para>
/// </summary>
public class OilPaintOpsTests
{
    [Theory]
    [InlineData(29, 13, 2, 20)]
    [InlineData(11, 9, 6, 8)]      // window wider than the row: the clamped slide path
    [InlineData(64, 40, 1, 4)]
    public void OilPaint_MatchesARecountAtEveryPixel(int width, int height, int radius, int levels)
    {
        var source = MakeBytes(width, height);
        var expected = Reference(source, width, height, radius, levels);

        using var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba8);
        source.AsSpan().CopyTo(image.WritablePixels);

        using var painted = image.OilPaint(radius, levels);
        Assert.Equal(expected, painted.WritablePixels.ToArray());
    }

    /// <summary>Noisy on purpose: a smooth ramp would put almost every sample in one bin and hide a drift.</summary>
    private static byte[] MakeBytes(int width, int height)
    {
        var data = new byte[width * height * 4];
        uint state = 0x2545F491;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)(state >> 16);
        }

        return data;
    }

    private static byte[] Reference(byte[] source, int width, int height, int radius, int levels)
    {
        const float Scale = 1f / 255f;
        var result = new byte[source.Length];
        var counts = new int[levels];
        var sumR = new int[levels];
        var sumG = new int[levels];
        var sumB = new int[levels];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            Array.Clear(counts);
            Array.Clear(sumR);
            Array.Clear(sumG);
            Array.Clear(sumB);

            for (var ky = -radius; ky <= radius; ky++)
            {
                var sy = Math.Clamp(y + ky, 0, height - 1);
                for (var kx = -radius; kx <= radius; kx++)
                {
                    var sx = Math.Clamp(x + kx, 0, width - 1);
                    var offset = (sy * width + sx) * 4;
                    var r = (int)(source[offset] * Scale * 255f + 0.5f);
                    var g = (int)(source[offset + 1] * Scale * 255f + 0.5f);
                    var b = (int)(source[offset + 2] * Scale * 255f + 0.5f);

                    var bin = (r + g + b) / 3 * levels / 256;
                    if (bin >= levels)
                        bin = levels - 1;

                    counts[bin]++;
                    sumR[bin] += r;
                    sumG[bin] += g;
                    sumB[bin] += b;
                }
            }

            var best = 0;
            for (var bin = 1; bin < levels; bin++)
            {
                if (counts[bin] > counts[best])
                    best = bin;
            }

            var count = counts[best];
            var target = (y * width + x) * 4;
            result[target] = ToByte(sumR[best] / (count * 255f));
            result[target + 1] = ToByte(sumG[best] / (count * 255f));
            result[target + 2] = ToByte(sumB[best] / (count * 255f));
            result[target + 3] = source[target + 3];
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
