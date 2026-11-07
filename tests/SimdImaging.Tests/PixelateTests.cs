using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// `Pixelate` was rewritten twice on 2026-11-07: first the per-pixel `x / blockSize` division was removed by
/// walking block-major, then block rows were parallelised. Both changes claim to be byte-identical because each
/// accumulator still sees its samples in the same row-major order — this test is the only thing that makes that
/// claim worth anything.
///
/// <para>The reference below is the pre-2026-11-07 shape, verbatim: one integer division per pixel into
/// <c>sums[x / blockSize * 4 + c]</c>, then per block one reciprocal and a fill. Sizes include a block wider
/// than the image and widths that are not multiples of the block, because those are the paths where the
/// block-major walk has to clamp differently.</para>
/// </summary>
public class PixelateTests
{
    [Theory]
    [InlineData(17, 11, 4)]
    [InlineData(11, 9, 12)]    // block wider than the row: every column clamps to one block
    [InlineData(64, 40, 7)]    // 64 / 7 = 9 blocks with 1 px left over
    [InlineData(8, 8, 8)]      // exactly one block
    public void Pixelate_MatchesThePerPixelReference(int width, int height, int blockSize)
    {
        var source = MakeBytes(width, height);
        var expected = Reference(source, width, height, blockSize);

        using var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba8);
        source.AsSpan().CopyTo(image.WritablePixels);

        using var pixelated = image.Pixelate(blockSize);
        Assert.Equal(expected, pixelated.WritablePixels.ToArray());
    }

    private static byte[] MakeBytes(int width, int height)
    {
        var data = new byte[width * height * 4];
        uint state = 0x7F4A7C15;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)(state >> 16);
        }

        return data;
    }

    /// <summary>Premultiplied read, per-pixel division into the tallies, reciprocal fill, raw write.</summary>
    private static byte[] Reference(byte[] source, int width, int height, int blockSize)
    {
        const float Scale = 1f / 255f;
        var columns = (width + blockSize - 1) / blockSize;
        var premultiplied = new float[width * height * 4];

        for (var i = 0; i < premultiplied.Length; i += 4)
        {
            var alpha = source[i + 3] * Scale;
            premultiplied[i] = source[i] * Scale * alpha;
            premultiplied[i + 1] = source[i + 1] * Scale * alpha;
            premultiplied[i + 2] = source[i + 2] * Scale * alpha;
            premultiplied[i + 3] = alpha;
        }

        var result = new byte[source.Length];
        var sums = new float[columns * 4];

        for (var top = 0; top < height; top += blockSize)
        {
            var bottom = Math.Min(top + blockSize, height);
            var count = bottom - top;
            Array.Clear(sums);

            for (var y = top; y < bottom; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var column = x / blockSize * 4;      // the division this rewrite removed
                    var offset = (y * width + x) * 4;
                    sums[column] += premultiplied[offset];
                    sums[column + 1] += premultiplied[offset + 1];
                    sums[column + 2] += premultiplied[offset + 2];
                    sums[column + 3] += premultiplied[offset + 3];
                }
            }

            for (var column = 0; column < columns; column++)
            {
                var source4 = column * 4;
                var alphaSum = sums[source4 + 3];
                var inverse = alphaSum > 0f ? 1f / alphaSum : 0f;
                var r = sums[source4] * inverse;
                var g = sums[source4 + 1] * inverse;
                var b = sums[source4 + 2] * inverse;
                var a = alphaSum / count;

                var start = column * blockSize;
                var end = Math.Min(start + blockSize, width);
                for (var y = top; y < bottom; y++)
                for (var x = start; x < end; x++)
                {
                    var offset = (y * width + x) * 4;
                    result[offset] = ToByte(r);
                    result[offset + 1] = ToByte(g);
                    result[offset + 2] = ToByte(b);
                    result[offset + 3] = ToByte(a);
                }
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
