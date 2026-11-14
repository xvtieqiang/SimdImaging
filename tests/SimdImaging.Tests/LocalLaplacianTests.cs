using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// M11 Local Laplacian. The strongest check is the identity case: with <c>alpha = beta = 1</c> the remap is
/// the identity, so the analysis and synthesis stages must reconstruct the input. It is not expected to be
/// byte-exact — the collapse telescopes through float additions, so one ULP per level is inherent — but any
/// kernel mismatch, parity bug or wrong target size turns it into a visible error instead of ±1.
/// </summary>
public class LocalLaplacianTests
{
    [Theory]
    [InlineData(WorkingFormat.Rgba8)]
    [InlineData(WorkingFormat.Rgba16)]
    public void IdentityParameters_ReconstructTheInput(WorkingFormat format)
    {
        using var image = Ramp(32, 24, format);
        var expected = image.WritablePixels.ToArray();

        using var result = LocalLaplacianOps.LocalLaplacian(image[0].Pixels.View, levels: 3, samples: 8,
            alpha: 1f, beta: 1f);

        var actual = result.View.Span;
        var max = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            if (i % 4 == 3) continue; // alpha is copied, not filtered
            max = Math.Max(max, Math.Abs(actual[i] - expected[i]));
        }

        max.ShouldBeLessThanOrEqualTo(1, $"{format}: the identity remap must reconstruct the image");
    }

    [Fact]
    public void ConstantImage_StaysConstant()
    {
        using var image = Image.Create(24, 16, WorkingFormat.Rgba8);
        image.WritablePixels.Fill(120);
        var before = image.WritablePixels.ToArray();

        using var result = LocalLaplacianOps.LocalLaplacian(image[0].Pixels.View, levels: 3, samples: 8, alpha: 0.3f);

        result.View.Span.ToArray().ShouldBe(before);
    }

    [Fact]
    public void CodeValueTable_MatchesTheDirectRemap()
    {
        using var image = Ramp(17, 13, WorkingFormat.Rgba8);

        LocalLaplacianOps.ForceDirectRemap = true;
        byte[] direct;
        try
        {
            using var result = LocalLaplacianOps.LocalLaplacian(image[0].Pixels.View, levels: 3, samples: 8, alpha: 0.3f);
            direct = result.View.Span.ToArray();
        }
        finally
        {
            LocalLaplacianOps.ForceDirectRemap = false;
        }

        using var table = LocalLaplacianOps.LocalLaplacian(image[0].Pixels.View, levels: 3, samples: 8, alpha: 0.3f);
        table.View.Span.ToArray().ShouldBe(direct, "the code-value table must reproduce the direct remap exactly");
    }

    [Theory]
    [InlineData(1, 9)]
    [InlineData(9, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(17, 9)]
    public void SmallImages_RunWithClampedLevels(int width, int height)
    {
        using var image = Ramp(width, height, WorkingFormat.Rgba8);
        using var result = LocalLaplacianOps.LocalLaplacian(image[0].Pixels.View, levels: 5, samples: 4, alpha: 0.3f);

        result.Width.ShouldBe(width);
        result.Height.ShouldBe(height);
        result.View.Span.ToArray().Length.ShouldBe(width * height * 4);
    }

    [Fact]
    public void BoostsLocalContrastWithoutMovingTheMean()
    {
        // A flat mid-grey with a faint 1-pixel ripple: alpha < 1 must amplify the ripple, while the mean
        // of the flat region stays where it was (a global curve would move it).
        using var image = Image.Create(64, 64, WorkingFormat.Rgba8);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var at = (y * 64 + x) * 4;
                var value = (byte)(128 + ((x + y) % 2 == 0 ? 2 : -2));
                image.WritablePixels[at] = value;
                image.WritablePixels[at + 1] = value;
                image.WritablePixels[at + 2] = value;
                image.WritablePixels[at + 3] = 255;
            }
        }

        using var result = LocalLaplacianOps.LocalLaplacian(image[0].Pixels.View, levels: 4, samples: 32,
            alpha: 0.3f, beta: 1f, sigmaR: 0.15f);

        var before = Spread(image.WritablePixels, 64);
        var after = Spread(result.View.Span, 64);
        // Measured 1.5× with the defaults (32 samples over [0,1], sigmaR 0.15, alpha 0.3). The boost is set by
        // the sample spacing relative to sigmaR — the detail branch is evaluated at most half a sample away
        // from the pixel's own intensity — so the floor is 1.3× rather than a fixed absolute spread.
        after.ShouldBeGreaterThan(before * 1.3, $"ripple spread {before:F2} -> {after:F2}");

        var meanBefore = Mean(image.WritablePixels, 64);
        var meanAfter = Mean(result.View.Span, 64);
        Math.Abs(meanAfter - meanBefore).ShouldBeLessThan(1.5, $"mean {meanBefore:F2} -> {meanAfter:F2}");
    }

    [Fact]
    public void RejectsBadParameters()
    {
        using var image = Ramp(9, 9, WorkingFormat.Rgba8);
        var view = image[0].Pixels.View;

        Should.Throw<ArgumentOutOfRangeException>(() => LocalLaplacianOps.LocalLaplacian(view, levels: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => LocalLaplacianOps.LocalLaplacian(view, samples: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => LocalLaplacianOps.LocalLaplacian(view, sigmaR: 0f));
        Should.Throw<ArgumentOutOfRangeException>(() => LocalLaplacianOps.LocalLaplacian(view, sigmaR: float.NaN));
        Should.Throw<ArgumentOutOfRangeException>(() => LocalLaplacianOps.LocalLaplacian(view, alpha: -0.1f));
        Should.Throw<ArgumentOutOfRangeException>(() => LocalLaplacianOps.LocalLaplacian(view, beta: -1f));
    }

    // ------------------------------------------------------------------ helpers

    private static double Spread(ReadOnlySpan<byte> pixels, int width)
    {
        // Mean absolute difference between horizontally adjacent luma samples — the ripple's amplitude.
        double sum = 0;
        var count = 0;
        for (var y = 0; y < pixels.Length / (width * 4); y++)
            for (var x = 1; x < width; x++)
            {
                var a = (y * width + x) * 4;
                var b = a - 4;
                sum += Math.Abs(pixels[a] - pixels[b]);
                count++;
            }

        return sum / count;
    }

    private static double Mean(ReadOnlySpan<byte> pixels, int width)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < pixels.Length / (width * 4); y++)
            for (var x = 0; x < width; x++)
            {
                sum += pixels[(y * width + x) * 4];
                count++;
            }

        return sum / count;
    }

    private static Image Ramp(int width, int height, WorkingFormat format)
    {
        var image = Image.Create(width, height, format);
        if (format == WorkingFormat.Rgba32F)
        {
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.WritablePixels);
            for (var i = 0; i < floats.Length; i++)
                floats[i] = (i * 37 % 101) / 101f;
            return image;
        }

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var value = (byte)((x * 7 + y * 13) % 256);
                var at = (y * width + x) * 4;
                if (format == WorkingFormat.Rgba8)
                {
                    image.WritablePixels[at] = value;
                    image.WritablePixels[at + 1] = (byte)(255 - value);
                    image.WritablePixels[at + 2] = value;
                    image.WritablePixels[at + 3] = 255;
                }
                else
                {
                    var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.WritablePixels);
                    samples[at] = (ushort)(value * 257);
                    samples[at + 1] = (ushort)((255 - value) * 257);
                    samples[at + 2] = (ushort)(value * 257);
                    samples[at + 3] = 65535;
                }
            }

        return image;
    }
}
