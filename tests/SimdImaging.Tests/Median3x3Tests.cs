using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The 3×3 median used to be a per-pixel insertion sort, which fully sorted all nine values and whose cost
/// depended on how sorted the data already was (130 ms on a flat chart, 575 ms on a real photograph at the
/// same size). It is now Paeth's 19-compare-exchange selection network, which is faster and data-independent —
/// but a selection network is exactly the kind of change that silently returns a wrong pixel if one
/// compare-exchange is misplaced. This test holds it against a full sort, on deliberately noisy data.
/// </summary>
public class Median3x3Tests
{
    private const int Width = 23;      // odd, not a multiple of 4, so block edges are exercised
    private const int Height = 17;

    /// <summary>
    /// The byte-domain kernel is only allowed to exist because the float round trip is the identity on bytes:
    /// the scalar paths read <c>b * (1/255f)</c> and write back <c>clamp(v*255 + 0.5)</c>. If that ever stops
    /// being exact for some byte, the two domains would disagree and the SIMD path would be wrong while still
    /// looking plausible. Exhaustive over all 256 inputs, so it is a proof, not a sample.
    /// </summary>
    [Fact]
    public void ByteToFloatRoundTrip_IsIdentity_ForEveryByte()
    {
        for (var value = 0; value < 256; value++)
        {
            var asFloat = value * (1f / 255f);
            var scaled = asFloat * 255f + 0.5f;
            var back = scaled <= 0f ? 0 : scaled >= 255f ? 255 : (int)scaled;
            Assert.Equal(value, back);
        }
    }

    /// <summary>
    /// Several widths on purpose: the byte-domain kernel handles four pixels per block between x=1 and
    /// x=width-5, so an odd width, a width that is not a multiple of four and a one-block-wide row each take a
    /// different mix of vector and scalar paths.
    /// </summary>
    [Theory]
    [InlineData(23, 17)]
    [InlineData(101, 7)]
    [InlineData(64, 40)]
    [InlineData(9, 9)]
    public void Median3x3_MatchesFullSort_OnRgba8(int width, int height)
    {
        var data = MakeBytes(width, height);
        var expected = Reference(data, width, height, 1);

        using var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba8);
        data.AsSpan().CopyTo(image.WritablePixels);

        using var filtered = image.Median3x3();
        Assert.Equal(expected, filtered.WritablePixels.ToArray());
    }

    [Fact]
    public void Median3x3_MatchesFullSort_OnRgba16()
    {
        var data = MakeBytes16(Width, Height);   // 16-bit needs 8 bytes per pixel, not 4
        var expected16 = Reference16(data, Width, Height);

        using var image = SimdImaging.Image.Create(Width, Height, WorkingFormat.Rgba16);
        data.AsSpan().CopyTo(image.WritablePixels);

        using var filtered = image.Median3x3();
        Assert.Equal(expected16, filtered.WritablePixels.ToArray());
    }

    /// <summary>Noisy bytes: a monotone ramp would hide a misplaced compare-exchange because almost any
    /// selection would agree with the sorted order.</summary>
    private static byte[] MakeBytes(int width, int height)
    {
        var data = new byte[width * height * 4];
        uint state = 0x9E3779B9;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)(state >> 16);
        }

        return data;
    }

    private static byte[] MakeBytes16(int width, int height)
    {
        var data = new byte[width * height * 8];
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(data.AsSpan());
        uint state = 0x85EBCA6B;
        for (var i = 0; i < samples.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            samples[i] = (ushort)(state >> 16);
        }

        return data;
    }

    private static byte[] Reference(byte[] source, int width, int height, int _)
    {
        var result = new byte[source.Length];
        Span<byte> window = stackalloc byte[9];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var c = 0; c < 4; c++)
        {
            var k = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var sx = Math.Clamp(x + dx, 0, width - 1);
                var sy = Math.Clamp(y + dy, 0, height - 1);
                window[k++] = source[(sy * width + sx) * 4 + c];
            }

            window.Sort();
            result[(y * width + x) * 4 + c] = window[4];
        }

        return result;
    }

    /// <summary>The same reference in the float domain, which is what the 16-bit path reads and writes.</summary>
    private static byte[] Reference16(byte[] source, int width, int height)
    {
        const float Scale = 1f / 65535f;
        var input = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(source.AsSpan());
        var result = new byte[source.Length];
        var output = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(result.AsSpan());
        Span<float> window = stackalloc float[9];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var c = 0; c < 4; c++)
        {
            var k = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var sx = Math.Clamp(x + dx, 0, width - 1);
                var sy = Math.Clamp(y + dy, 0, height - 1);
                window[k++] = input[(sy * width + sx) * 4 + c] * Scale;
            }

            window.Sort();
            var median = window[4];
            var scaled = median * 65535f + 0.5f;
            output[(y * width + x) * 4 + c] = scaled <= 0f ? (ushort)0
                : scaled >= 65535f ? (ushort)65535 : (ushort)scaled;
        }

        return result;
    }
}
