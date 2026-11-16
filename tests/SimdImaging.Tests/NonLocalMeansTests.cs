using System.Runtime.InteropServices;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// M11 non-local means. The oracle is a double-precision naive implementation written from the operator's
/// documented contract (candidate order, rounding point, integer distance for the integer formats), not
/// from its code: if the two agree byte for byte across sizes, formats and radii, the ring buffer, the
/// clamping and the frozen accumulation are all doing what the contract says.
/// </summary>
public class NonLocalMeansTests
{
    [Theory]
    [InlineData(WorkingFormat.Rgba8)]
    [InlineData(WorkingFormat.Rgba16)]
    [InlineData(WorkingFormat.Rgba32F)]
    public void ZeroRadius_IsIdentity(WorkingFormat format)
    {
        using var image = Noise(9, 7, format, seed: 11);
        var before = image.WritablePixels.ToArray();

        using var result = NonLocalMeansOps.NonLocalMeans(image[0].Pixels.View, 0, 0, 0.1f);

        result.View.Span.ToArray().ShouldBe(before);
    }

    [Theory]
    [InlineData(1, 1, 1, 0)]
    [InlineData(1, 9, 1, 0)]
    [InlineData(9, 1, 1, 0)]
    [InlineData(3, 3, 1, 1)]
    [InlineData(8, 7, 2, 1)]
    [InlineData(40, 27, 2, 1)]
    public void MatchesTheNaiveReference_Rgba8(int width, int height, int searchRadius, int patchRadius)
    {
        using var image = Noise(width, height, WorkingFormat.Rgba8, seed: 4711);
        AssertMatchesReference(image, searchRadius, patchRadius, 0.12f, 0f);
    }

    [Theory]
    [InlineData(1, 9, 1, 0)]
    [InlineData(3, 3, 1, 1)]
    [InlineData(17, 5, 2, 1)]
    public void MatchesTheNaiveReference_Rgba16(int width, int height, int searchRadius, int patchRadius)
    {
        using var image = Noise(width, height, WorkingFormat.Rgba16, seed: 90210);
        AssertMatchesReference(image, searchRadius, patchRadius, 0.08f, 0.01f);
    }

    [Fact]
    public void TinyImage_ClampsEveryNeighbourhood()
    {
        // 3×3 with a 5-pixel search and a 2-pixel patch: every candidate and every patch tap is a clamped
        // duplicate. This is the case that catches a row ring seeded at the band start instead of
        // start - (search + patch): on large images that bug is invisible.
        using var image = Noise(3, 3, WorkingFormat.Rgba8, seed: 7);
        AssertMatchesReference(image, 5, 2, 0.2f, 0f);
    }

    [Fact]
    public void DegreeDoesNotChangeBytes()
    {
        using var image = Noise(23, 19, WorkingFormat.Rgba8, seed: 1234);
        var previous = RowParallel.DegreeOverride;
        try
        {
            byte[]? expected = null;
            foreach (var degree in new[] { 1, 2, 16 })
            {
                RowParallel.DegreeOverride = degree;
                using var result = NonLocalMeansOps.NonLocalMeans(image[0].Pixels.View, 2, 1, 0.15f);
                var bytes = result.View.Span.ToArray();
                if (expected is null)
                    expected = bytes;
                else
                    bytes.ShouldBe(expected, $"degree {degree} changed the output");
            }
        }
        finally
        {
            RowParallel.DegreeOverride = previous;
        }
    }

    [Fact]
    public void ConstantImage_IsUnchanged()
    {
        using var image = Image.Create(11, 9, WorkingFormat.Rgba8);
        image.WritablePixels.Fill(200);
        var before = image.WritablePixels.ToArray();

        using var result = NonLocalMeansOps.NonLocalMeans(image[0].Pixels.View, 4, 2, 0.05f, 0.02f);

        result.View.Span.ToArray().ShouldBe(before);
    }

    [Fact]
    public void DenoisesMoreThanItBlursEdges()
    {
        // A clean two-tone image with a hard edge, a deterministic noise field on top: the denoised result
        // must sit closer to the clean image than the noisy input does (PSNR up by >= 3 dB), and the edge
        // must stay sharp. Without this test every byte-exact assertion above could pass on a no-op.
        using var clean = TwoTone(48, 32);
        using var noisy = Image.Create(48, 32, WorkingFormat.Rgba8);
        var random = new Random(2026);
        for (var i = 0; i < noisy.WritablePixels.Length; i += 4)
        {
            var delta = random.Next(-24, 25);
            for (var c = 0; c < 3; c++)
                noisy.WritablePixels[i + c] = (byte)Math.Clamp(clean.WritablePixels[i + c] + delta, 0, 255);
            noisy.WritablePixels[i + 3] = 255;
        }

        using var denoised = NonLocalMeansOps.NonLocalMeans(noisy[0].Pixels.View, 3, 2, 0.25f, 0.04f);

        var before = Psnr(noisy.WritablePixels, clean.WritablePixels);
        var after = Psnr(denoised.View.Span, clean.WritablePixels);
        after.ShouldBeGreaterThan(before + 3.0, $"PSNR {before:F2} -> {after:F2} dB");

        // The edge must stay an edge: patches straddling it are similar to each other, so the transition
        // keeps most of its contrast. Eight pixels away the search window no longer reaches the bright
        // side, so the dark plateau must stay dark.
        var left = (16 * 48 + 23) * 4;
        var right = left + 4;
        // The clean edge is 24 -> 231 (contrast 207); the probe measured 144 (70%) after denoising. A blur
        // with the same radius would lose far more, so 60% is the behavioural floor, not a tuned value.
        (denoised.View.Span[right] - denoised.View.Span[left]).ShouldBeGreaterThan(124);
        denoised.View.Span[(16 * 48 + 14) * 4].ShouldBeLessThan((byte)40);
    }

    [Fact]
    public void RejectsBadParameters()
    {
        using var image = Noise(5, 5, WorkingFormat.Rgba8, seed: 1);
        var view = image[0].Pixels.View;

        Should.Throw<ArgumentOutOfRangeException>(() => NonLocalMeansOps.NonLocalMeans(view, -1));
        Should.Throw<ArgumentOutOfRangeException>(() => NonLocalMeansOps.NonLocalMeans(view, 1, -1));
        Should.Throw<ArgumentOutOfRangeException>(() => NonLocalMeansOps.NonLocalMeans(view, 1, 1, 0f));
        Should.Throw<ArgumentOutOfRangeException>(() => NonLocalMeansOps.NonLocalMeans(view, 1, 1, float.NaN));
        Should.Throw<ArgumentOutOfRangeException>(() => NonLocalMeansOps.NonLocalMeans(view, 1, 1, 0.1f, -0.1f));
        Should.Throw<ArgumentOutOfRangeException>(() => NonLocalMeansOps.NonLocalMeans(view, 1, 1, 0.1f, float.PositiveInfinity));
    }

    [Fact]
    public void FloatNaN_StaysInsideTheSearchWindow()
    {
        // Rgba32F has no quantisation to stop a NaN: it poisons every patch that contains it. The contract
        // says it propagates through the search window and nothing further — a distant pixel must stay finite.
        using var image = Noise(24, 24, WorkingFormat.Rgba32F, seed: 5);
        var floats = MemoryMarshal.Cast<byte, float>(image.WritablePixels);
        floats[(12 * 24 + 12) * 4] = float.NaN;

        using var result = NonLocalMeansOps.NonLocalMeans(image[0].Pixels.View, 2, 1, 0.1f);
        var output = MemoryMarshal.Cast<byte, float>(result.View.Span);

        float.IsNaN(output[(0 * 24 + 0) * 4]).ShouldBeFalse("a corner pixel is outside the corrupted pixel's search window");
        float.IsNaN(output[(12 * 24 + 12) * 4]).ShouldBeTrue("the corrupted pixel itself cannot be recovered");
    }

    [Fact]
    public void VectorAndScalarDistancesAgree()
    {
        // Integer sums are associative, so the vector twin cannot drift: this is an equality test, not a
        // tolerance one. It also covers the clamped-column fallback at the left and right edges.
        foreach (var (width, height) in new[] { (1, 1), (5, 3), (17, 9), (64, 23) })
        {
            using var image = Noise(width, height, WorkingFormat.Rgba8, seed: 777);

            NonLocalMeansOps.ForceScalarSsd = true;
            byte[] scalar;
            try
            {
                using var result = NonLocalMeansOps.NonLocalMeans(image[0].Pixels.View, 3, 2, 0.15f);
                scalar = result.View.Span.ToArray();
            }
            finally
            {
                NonLocalMeansOps.ForceScalarSsd = false;
            }

            using var vector = NonLocalMeansOps.NonLocalMeans(image[0].Pixels.View, 3, 2, 0.15f);
            vector.View.Span.ToArray().ShouldBe(scalar, $"{width}×{height}: vector and scalar distances disagree");
        }
    }

    [Theory]
    // Sizes are large enough to clear the clamp-free interior (width, height > 2*(S+P)+1), so these
    // actually exercise the rolling column-sum path, not just the per-tap border fallback. Several radii,
    // h and a non-zero sigma; Rgba16 is covered too. Rgba32F keeps the per-tap path and must be unchanged.
    [InlineData(WorkingFormat.Rgba8, 40, 30, 3, 1, 0.1f, 0f)]
    [InlineData(WorkingFormat.Rgba8, 48, 40, 5, 2, 0.2f, 0.05f)]
    [InlineData(WorkingFormat.Rgba8, 64, 48, 2, 1, 0.15f, 0.02f)]
    [InlineData(WorkingFormat.Rgba16, 40, 30, 3, 1, 0.08f, 0.01f)]
    [InlineData(WorkingFormat.Rgba16, 48, 40, 5, 2, 0.15f, 0f)]
    [InlineData(WorkingFormat.Rgba32F, 40, 30, 3, 1, 0.1f, 0f)]
    public void RollingSsdMatchesPerTap_ByteForByte(WorkingFormat format, int width, int height,
        int searchRadius, int patchRadius, float h, float sigma)
    {
        using var image = Noise(width, height, format, seed: 20261116);
        var view = image[0].Pixels.View;

        byte[] perTap;
        NonLocalMeansOps.ForcePerTapSsd = true;
        try
        {
            using var result = NonLocalMeansOps.NonLocalMeans(view, searchRadius, patchRadius, h, sigma);
            perTap = result.View.Span.ToArray();
        }
        finally
        {
            NonLocalMeansOps.ForcePerTapSsd = false;
        }

        using var rolling = NonLocalMeansOps.NonLocalMeans(view, searchRadius, patchRadius, h, sigma);
        rolling.View.Span.ToArray().ShouldBe(perTap,
            $"{format} {width}x{height} S={searchRadius} P={patchRadius} h={h} sigma={sigma}: rolling SSD changed the bytes");
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertMatchesReference(Image image, int searchRadius, int patchRadius, float h,
        float sigma)
    {
        var expected = Naive(image.WritablePixels.ToArray(), image.Width, image.Height, image.Format,
            searchRadius, patchRadius, h, sigma);

        using var result = NonLocalMeansOps.NonLocalMeans(image[0].Pixels.View, searchRadius, patchRadius, h, sigma);

        result.View.Span.ToArray().ShouldBe(expected);
    }

    /// <summary>Double-precision reference written from the contract, not from the implementation.</summary>
    private static byte[] Naive(byte[] input, int width, int height, WorkingFormat format,
        int searchRadius, int patchRadius, float hValue, float sigmaValue)
    {
        var samples = width * height * 4;
        var colours = new double[samples];
        var code8 = new byte[samples];
        var code16 = new ushort[samples];
        var floats = new float[samples];
        if (format == WorkingFormat.Rgba8)
        {
            input.CopyTo(code8, 0);
            for (var i = 0; i < samples; i++)
                colours[i] = code8[i] * (1f / Pixels.MaxCode8);
        }
        else if (format == WorkingFormat.Rgba16)
        {
            for (var i = 0; i < samples; i++)
            {
                code16[i] = MemoryMarshal.Cast<byte, ushort>(input)[i];
                colours[i] = code16[i] * (1f / Pixels.MaxCode16);
            }
        }
        else
        {
            floats = MemoryMarshal.Cast<byte, float>(input).ToArray();
            for (var i = 0; i < samples; i++)
                colours[i] = floats[i];
        }

        var patchPixels = (2 * patchRadius + 1) * (2 * patchRadius + 1);
        var maxCode = format switch
        {
            WorkingFormat.Rgba8 => Pixels.MaxCode8,
            WorkingFormat.Rgba16 => Pixels.MaxCode16,
            _ => 1,
        };
        var normalizer = 1.0 / (patchPixels * 4 * (double)maxCode * maxCode);
        var hSquared = (double)hValue * hValue;
        var twoSigmaSquared = 2.0 * sigmaValue * sigmaValue;
        var bytesPerSample = format.BytesPerPixel() / 4;
        var output = new byte[samples * bytesPerSample];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double sum0 = 0, sum1 = 0, sum2 = 0, sum3 = 0, sumWeight = 0;
                for (var dy = -searchRadius; dy <= searchRadius; dy++)
                {
                    var sy = Math.Clamp(y + dy, 0, height - 1);
                    for (var dx = -searchRadius; dx <= searchRadius; dx++)
                    {
                        var sx = Math.Clamp(x + dx, 0, width - 1);

                        double distance = 0;
                        for (var py = -patchRadius; py <= patchRadius; py++)
                        {
                            var ay = Math.Clamp(y + py, 0, height - 1);
                            var by = Math.Clamp(sy + py, 0, height - 1);
                            for (var px = -patchRadius; px <= patchRadius; px++)
                            {
                                var ax = Math.Clamp(x + px, 0, width - 1);
                                var bx = Math.Clamp(sx + px, 0, width - 1);
                                var ia = (ay * width + ax) * 4;
                                var ib = (by * width + bx) * 4;

                                if (format == WorkingFormat.Rgba8)
                                {
                                    int d0 = code8[ia] - code8[ib];
                                    int d1 = code8[ia + 1] - code8[ib + 1];
                                    int d2 = code8[ia + 2] - code8[ib + 2];
                                    int d3 = code8[ia + 3] - code8[ib + 3];
                                    distance += d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
                                }
                                else if (format == WorkingFormat.Rgba16)
                                {
                                    long d0 = code16[ia] - code16[ib];
                                    long d1 = code16[ia + 1] - code16[ib + 1];
                                    long d2 = code16[ia + 2] - code16[ib + 2];
                                    long d3 = code16[ia + 3] - code16[ib + 3];
                                    distance += d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
                                }
                                else
                                {
                                    for (var c = 0; c < 4; c++)
                                    {
                                        double d = (double)floats[ia + c] - floats[ib + c];
                                        distance += d * d;
                                    }
                                }
                            }
                        }

                        var weight = Math.Exp(-Math.Max(distance * normalizer - twoSigmaSquared, 0.0) / hSquared);
                        var centre = (sy * width + sx) * 4;
                        sum0 += colours[centre] * weight;
                        sum1 += colours[centre + 1] * weight;
                        sum2 += colours[centre + 2] * weight;
                        sum3 += colours[centre + 3] * weight;
                        sumWeight += weight;
                    }
                }

                var at = (y * width + x) * 4;
                Quantize((float)(sum0 / sumWeight), format, output, at);
                Quantize((float)(sum1 / sumWeight), format, output, at + 1);
                Quantize((float)(sum2 / sumWeight), format, output, at + 2);
                Quantize((float)(sum3 / sumWeight), format, output, at + 3);
            }
        }

        return output;
    }

    private static void Quantize(float value, WorkingFormat format, Span<byte> output, int sampleIndex)
    {
        switch (format)
        {
            case WorkingFormat.Rgba8:
                output[sampleIndex] = Pixels.ToByte(value);
                break;

            case WorkingFormat.Rgba16:
            {
                var scaled = value * Pixels.MaxCode16 + 0.5f;
                var clamped = scaled <= 0f ? 0 : scaled >= Pixels.MaxCode16 ? Pixels.MaxCode16 : (int)scaled;
                MemoryMarshal.Cast<byte, ushort>(output)[sampleIndex] = (ushort)clamped;
                break;
            }

            default:
                MemoryMarshal.Cast<byte, float>(output)[sampleIndex] = value;
                break;
        }
    }

    private static double Psnr(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        double sum = 0;
        var count = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            if (i % 4 == 3) continue; // alpha is constant in this fixture
            var d = actual[i] - expected[i];
            sum += d * d;
            count++;
        }

        var mse = sum / count;
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static Image TwoTone(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                var value = (byte)(x < width / 2 ? 24 : 231);
                image.WritablePixels[at] = value;
                image.WritablePixels[at + 1] = value;
                image.WritablePixels[at + 2] = value;
                image.WritablePixels[at + 3] = 255;
            }
        }

        return image;
    }

    private static Image Noise(int width, int height, WorkingFormat format, int seed)
    {
        var image = Image.Create(width, height, format);
        if (format == WorkingFormat.Rgba32F)
        {
            var floats = MemoryMarshal.Cast<byte, float>(image.WritablePixels);
            var random = new Random(seed);
            for (var i = 0; i < floats.Length; i++)
                floats[i] = (float)random.NextDouble();
        }
        else
        {
            new Random(seed).NextBytes(image.WritablePixels);
        }

        return image;
    }
}
