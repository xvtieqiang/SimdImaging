using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// The separable blur was reshaped three times in one afternoon -- planar horizontal pass, 4×4 lane
/// transpose, two-dimensional vertical tile -- and every one of those was verified on Rgba8. Rgba16 runs the
/// same band and strip code with a different read/write width, so it needs its own reference check rather
/// than an assumption that "the shape is format independent".
/// <para>The reference here is a deliberately naive per-pixel separable filter, written in the test, using
/// the same premultiply / ascending-tap / unpremultiply order the library documents. It is not a second
/// library, it is the scalar twin that the vector and tiled paths have to reproduce bit for bit.</para>
/// </summary>
public class FilterRgba16Tests
{
    private const int Width = 40;
    private const int Height = 27;
    private const int MaxCode16 = 65535;

    [Theory]
    [InlineData(3f)]
    [InlineData(0.8f)]
    public void Gaussian_MatchesNaiveReference_AndIgnoresWorkerCount(float sigma)
    {
        var source = MakeImage();
        var radius = (int)MathF.Ceiling(sigma * 3f);
        var kernel = GaussianKernel(sigma, radius);
        var expected = Reference(source, Width, Height, kernel, radius);

        foreach (var degree in new[] { 1, 2, 16 })
        {
            try
            {
                RowParallel.DegreeOverride = degree;
                using var image = SimdImaging.Image.Create(Width, Height, WorkingFormat.Rgba16);
                source.AsSpan().CopyTo(image.WritablePixels);

                using var blurred = image.GaussianBlur(sigma);
                Assert.Equal(expected, blurred.WritablePixels.ToArray());
            }
            finally
            {
                RowParallel.DegreeOverride = 0;
            }
        }
    }

    [Fact]
    public void BoxBlur_MatchesNaiveReference()
    {
        const int Radius = 3;
        var source = MakeImage();
        var kernel = new float[Radius * 2 + 1];
        Array.Fill(kernel, 1f / kernel.Length);
        var expected = Reference(source, Width, Height, kernel, Radius);

        using var image = SimdImaging.Image.Create(Width, Height, WorkingFormat.Rgba16);
        source.AsSpan().CopyTo(image.WritablePixels);

        using var blurred = image.BoxBlur(Radius);
        Assert.Equal(expected, blurred.WritablePixels.ToArray());
    }

    /// <summary>
    /// Narrow image, wider kernel: sigma 5 gives radius 15 on an 11-px wide image, which is the case where a
    /// prefix loop bounded by the radius instead of the width runs past the plane. That overrun used to throw
    /// or silently corrupt depending on pool state, and a solid image could not show it — so this pattern is
    /// deliberately non-constant.
    /// </summary>
    [Fact]
    public void Gaussian_NarrowImageWiderKernel_MatchesNaiveReference()
    {
        const int narrowWidth = 11;
        const int narrowHeight = 9;
        const float sigma = 5f;
        var radius = (int)MathF.Ceiling(sigma * 3f);
        var kernel = GaussianKernel(sigma, radius);

        var bytes = new byte[narrowWidth * narrowHeight * 8];
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (var y = 0; y < narrowHeight; y++)
        for (var x = 0; x < narrowWidth; x++)
        {
            var i = (y * narrowWidth + x) * 4;
            samples[i] = (ushort)(x * 6000 + 1000);
            samples[i + 1] = (ushort)(y * 7000 + 2000);
            samples[i + 2] = (ushort)((x * y) * 400 + 30000);
            samples[i + 3] = 65535;
        }

        var expected = Reference(bytes, narrowWidth, narrowHeight, kernel, radius);

        using var image = SimdImaging.Image.Create(narrowWidth, narrowHeight, WorkingFormat.Rgba16);
        bytes.AsSpan().CopyTo(image.WritablePixels);

        using var blurred = image.GaussianBlur(sigma);
        Assert.Equal(expected, blurred.WritablePixels.ToArray());
    }

    /// <summary>
    /// Sizes chosen to straddle the radius: at sigma 5 the radius is 15, so widths 3/5/8/11 all have a kernel
    /// wider than the image, which is exactly the case the prefix loop got wrong. The pattern is never
    /// constant, because a solid image writes the same value back and hides whole classes of index error.
    /// </summary>
    public static IEnumerable<object[]> SizeCases()
    {
        foreach (var width in new[] { 3, 5, 8, 11, 16, 40 })
        foreach (var height in new[] { 3, 7, 9, 27 })
        foreach (var sigma in new[] { 0.8f, 3f, 5f })
            yield return new object[] { width, height, sigma };
    }

    [Theory]
    [MemberData(nameof(SizeCases))]
    public void Gaussian_SizeSweep_MatchesNaiveReference(int width, int height, float sigma)
    {
        var radius = (int)MathF.Ceiling(sigma * 3f);
        var kernel = GaussianKernel(sigma, radius);
        var bytes = MakeImage(width, height);
        var expected = Reference(bytes, width, height, kernel, radius);

        using var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba16);
        bytes.AsSpan().CopyTo(image.WritablePixels);

        using var blurred = image.GaussianBlur(sigma);
        Assert.Equal(expected, blurred.WritablePixels.ToArray());
    }

    [Theory]
    [MemberData(nameof(SizeCases))]
    public void BoxBlur_SizeSweep_MatchesNaiveReference(int width, int height, float sigma)
    {
        _ = sigma;
        const int Radius = 3;
        var kernel = new float[Radius * 2 + 1];
        Array.Fill(kernel, 1f / kernel.Length);
        var bytes = MakeImage(width, height);
        var expected = Reference(bytes, width, height, kernel, Radius);

        using var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba16);
        bytes.AsSpan().CopyTo(image.WritablePixels);

        using var blurred = image.BoxBlur(Radius);
        Assert.Equal(expected, blurred.WritablePixels.ToArray());
    }

    private static byte[] MakeImage() => MakeImage(Width, Height);

    private static byte[] MakeImage(int width, int height)
    {
        var bytes = new byte[width * height * 8];
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            samples[i] = (ushort)((x * 1301 + y * 7919) & 0xFFFF);
            samples[i + 1] = (ushort)((x * x * 37 + y * 211) & 0xFFFF);
            samples[i + 2] = (ushort)(((x ^ y) * 4001 + 30000) & 0xFFFF);
            samples[i + 3] = (ushort)(40000 + (x + y) % 20000);
        }

        return bytes;
    }

    private static float[] GaussianKernel(float sigma, int radius)
    {
        var kernel = new float[radius * 2 + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var weight = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            kernel[i + radius] = weight;
            sum += weight;
        }

        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;
        return kernel;
    }

    /// <summary>Premultiplied horizontal pass, then vertical, then unpremultiply — one pixel at a time.</summary>
    private static byte[] Reference(byte[] source, int width, int height, float[] kernel, int radius)
    {
        const float Scale = 1f / MaxCode16;
        var input = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(source.AsSpan());
        // Premultiply first: the library's ReadRowPremultiplied multiplies colour by alpha before any tap is
        // touched, so a transparent neighbourhood cannot bleed colour into the result.
        var premultiplied = new float[width * height * 4];
        for (var i = 0; i < premultiplied.Length; i += 4)
        {
            var alpha = input[i + 3] * Scale;
            premultiplied[i] = (input[i] * Scale) * alpha;
            premultiplied[i + 1] = (input[i + 1] * Scale) * alpha;
            premultiplied[i + 2] = (input[i + 2] * Scale) * alpha;
            premultiplied[i + 3] = alpha;
        }

        var filtered = new float[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var accumulator = new float[4];
            for (var k = -radius; k <= radius; k++)
            {
                var sx = Math.Clamp(x + k, 0, width - 1);
                var s = (y * width + sx) * 4;
                var weight = kernel[k + radius];
                for (var c = 0; c < 4; c++)
                    accumulator[c] += premultiplied[s + c] * weight;
            }

            var d = (y * width + x) * 4;
            for (var c = 0; c < 4; c++)
                filtered[d + c] = accumulator[c];
        }

        var vertical = new float[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var accumulator = new float[4];
            for (var k = -radius; k <= radius; k++)
            {
                var sy = Math.Clamp(y + k, 0, height - 1);
                var s = (sy * width + x) * 4;
                var weight = kernel[k + radius];
                for (var c = 0; c < 4; c++)
                    accumulator[c] += filtered[s + c] * weight;
            }

            var d = (y * width + x) * 4;
            for (var c = 0; c < 4; c++)
                vertical[d + c] = accumulator[c];
        }

        var result = new byte[width * height * 8];
        var output = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(result.AsSpan());
        for (var i = 0; i < vertical.Length; i += 4)
        {
            var alpha = vertical[i + 3];
            var inverse = alpha > 0f ? 1f / alpha : 0f;
            output[i] = ToUInt16(vertical[i] * inverse);
            output[i + 1] = ToUInt16(vertical[i + 1] * inverse);
            output[i + 2] = ToUInt16(vertical[i + 2] * inverse);
            output[i + 3] = ToUInt16(alpha);
        }

        return result;
    }

    private static ushort ToUInt16(float value)
    {
        var scaled = value * MaxCode16 + 0.5f;
        if (scaled <= 0f) return 0;
        if (scaled >= MaxCode16) return MaxCode16;
        return (ushort)scaled;
    }
}
