using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class FilterAdvancedTests
{
    private static Image Solid(int width, int height, byte value)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = span[i + 1] = span[i + 2] = value;
            span[i + 3] = 255;
        }

        return image;
    }

    private static byte Red(Image image, int x, int y) => image.WritablePixels[(y * image.Width + x) * 4];

    private static double Variance(Image image)
    {
        var span = image.WritablePixels;
        double sum = 0, squares = 0;
        var count = image.Width * image.Height;
        for (var i = 0; i < span.Length; i += 4)
        {
            double value = span[i];
            sum += value;
            squares += value * value;
        }

        var mean = sum / count;
        return squares / count - mean * mean;
    }

    [Fact]
    public void OilPaint_LeavesAFlatImageAlone()
    {
        using var source = Solid(9, 7, 120);
        using var painted = source.OilPaint(2, 20);

        for (var y = 0; y < 7; y++)
        {
            for (var x = 0; x < 9; x++)
                Red(painted, x, y).ShouldBe((byte)120);
        }
    }

    [Fact]
    public void OilPaint_PicksTheDominantTone()
    {
        using var source = Image.Create(10, 1, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var x = 0; x < 10; x++)
        {
            var o = x * 4;
            span[o] = span[o + 1] = span[o + 2] = (byte)(x < 5 ? 0 : 255);
            span[o + 3] = 255;
        }

        using var painted = source.OilPaint(1, 20);

        Red(painted, 1, 0).ShouldBe((byte)0);
        Red(painted, 4, 0).ShouldBe((byte)0);     // window holds two blacks and one white
        Red(painted, 5, 0).ShouldBe((byte)255);   // window holds one black and two whites
        Red(painted, 8, 0).ShouldBe((byte)255);
    }

    [Fact]
    public void OilPaint_RejectsIllegalParameters()
    {
        using var source = Solid(4, 4, 10);
        Should.Throw<ArgumentOutOfRangeException>(() => source.OilPaint(0));
        Should.Throw<ArgumentOutOfRangeException>(() => source.OilPaint(1, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => source.OilPaint(1, 257));
    }

    [Fact]
    public void Clahe_LeavesAFlatImageAlone()
    {
        using var source = Solid(16, 16, 128);
        using var equalised = source.Clahe(4, 4);

        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
                Red(equalised, x, y).ShouldBe((byte)128);
        }
    }

    [Fact]
    public void Clahe_ExpandsALowContrastImage()
    {
        // 64×64 with 4×4 tiles leaves 256 pixels per tile holding four levels of 64 pixels each; a clip limit
        // of 200 is above that, so the test measures the equalisation rather than the clipping.
        using var source = Image.Create(64, 64, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var o = (y * 64 + x) * 4;
                // One distinct value per column, sixteen per tile: at 16 rows each that is below the 20-count
                // limit, so the tile is equalised rather than clipped.
                span[o] = span[o + 1] = span[o + 2] = (byte)(120 + x / 4);
                span[o + 3] = 255;
            }
        }

        using var equalised = source.Clahe(4, 4, 200f);

        var minimum = (byte)255;
        var maximum = (byte)0;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var value = Red(equalised, x, y);
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
        }

        minimum.ShouldBeLessThan((byte)60);
        maximum.ShouldBeGreaterThan((byte)200);
    }

    [Fact]
    public void Clahe_RejectsMoreTilesThanPixels()
    {
        using var source = Solid(4, 4, 10);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Clahe(8, 4));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Clahe(4, 8));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Clahe(2, 2, 0f));
    }

    [Fact]
    public void Noise_IsReproducible()
    {
        using var source = Solid(16, 16, 128);
        using var first = source.Noise(0.1f, NoiseKind.Gaussian, seed: 7);
        using var second = source.Noise(0.1f, NoiseKind.Gaussian, seed: 7);
        using var other = source.Noise(0.1f, NoiseKind.Gaussian, seed: 8);

        second.WritablePixels.ToArray().ShouldBe(first.WritablePixels.ToArray());
        other.WritablePixels.ToArray().ShouldNotBe(first.WritablePixels.ToArray());
    }

    [Fact]
    public void Noise_ZeroAmountIsACopy()
    {
        using var source = Solid(9, 5, 77);
        using var noisy = source.Noise(0f);

        noisy.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Noise_KeepsAlphaAndStaysInRange()
    {
        using var source = Solid(32, 32, 128);
        using var noisy = source.Noise(0.3f, NoiseKind.Uniform, seed: 3);

        var span = noisy.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
            span[i + 3].ShouldBe((byte)255);
    }

    [Fact]
    public void Noise_RejectsIllegalAmount()
    {
        using var source = Solid(4, 4, 10);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Noise(-0.1f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Noise(1.5f));
    }

    [Fact]
    public void GuidedFilter_LeavesAFlatImageAlone()
    {
        using var source = Solid(12, 12, 90);
        using var filtered = source.GuidedFilter(3, 1e-4f);

        for (var y = 0; y < 12; y++)
        {
            for (var x = 0; x < 12; x++)
                Red(filtered, x, y).ShouldBe((byte)90);
        }
    }

    [Fact]
    public void GuidedFilter_SmoothsSmallVariation()
    {
        using var source = Image.Create(24, 24, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < 24; y++)
        {
            for (var x = 0; x < 24; x++)
            {
                var o = (y * 24 + x) * 4;
                var value = (byte)(120 + (x * 7 + y * 13) % 11 - 5);
                span[o] = span[o + 1] = span[o + 2] = value;
                span[o + 3] = 255;
            }
        }

        using var filtered = source.GuidedFilter(3, 0.01f);

        Variance(filtered).ShouldBeLessThan(Variance(source) * 0.5);
    }

    [Fact]
    public void GaussianBlur_MatchesADirectTwoDimensionalConvolution()
    {
        const int Width = 9;
        const int Height = 300;      // three bands at 128 rows, so the seams are inside the comparison
        const float Sigma = 1f;
        const int Radius = 3;

        using var source = Image.Create(Width, Height, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var o = (y * Width + x) * 4;
                span[o] = (byte)((40 + x * 17 + y * 5) % 256);
                span[o + 1] = (byte)((200 - y * 21 % 256 + 256) % 256);
                span[o + 2] = (byte)((x * 9 + y * 3) % 256);
                span[o + 3] = 255;
            }
        }

        using var blurred = source.GaussianBlur(Sigma);

        // The reference is a plain 2-D convolution with the same kernel, clamped at the edges; the separable
        // implementation must agree with it to within float summation order, seams included.
        var kernel = new float[Radius * 2 + 1];
        var total = 0f;
        for (var i = -Radius; i <= Radius; i++)
        {
            var weight = MathF.Exp(-(i * i) / (2f * Sigma * Sigma));
            kernel[i + Radius] = weight;
            total += weight;
        }

        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= total;

        var result = blurred.WritablePixels;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0f;
                    for (var ky = -Radius; ky <= Radius; ky++)
                    {
                        for (var kx = -Radius; kx <= Radius; kx++)
                        {
                            var sx = Math.Clamp(x + kx, 0, Width - 1);
                            var sy = Math.Clamp(y + ky, 0, Height - 1);
                            sum += kernel[ky + Radius] * kernel[kx + Radius] * span[(sy * Width + sx) * 4 + c];
                        }
                    }

                    var expected = Math.Clamp((int)(sum + 0.5f), 0, 255);
                    Math.Abs(result[(y * Width + x) * 4 + c] - expected)
                        .ShouldBeLessThanOrEqualTo(1, $"pixel ({x},{y}) channel {c}");
                }
            }
        }
    }

    [Fact]
    public void GuidedFilter_MatchesANaiveTwoDimensionalReference()
    {
        const int Width = 9;
        const int Height = 7;
        const int Radius = 2;
        const float Epsilon = 0.01f;

        using var source = Image.Create(Width, Height, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var o = (y * Width + x) * 4;
                var value = (byte)((60 + x * 19 + y * 7) % 256);
                span[o] = span[o + 1] = span[o + 2] = value;
                span[o + 3] = 255;
            }
        }

        // A copy: a Span cannot be captured by the reference's local functions.
        var pixels = source.WritablePixels.ToArray();
        using var filtered = source.GuidedFilter(Radius, Epsilon);
        var result = filtered.WritablePixels;

        // The reference is the definition: two rounds of a plain (2r+1)² box average with replicated edges,
        // no prefix sums and no bands. The real implementation must agree to within float summation order.
        double Box(int x, int y, Func<int, int, double> value)
        {
            var sum = 0.0;
            for (var ky = -Radius; ky <= Radius; ky++)
            {
                var sy = Math.Clamp(y + ky, 0, Height - 1);
                for (var kx = -Radius; kx <= Radius; kx++)
                {
                    var sx = Math.Clamp(x + kx, 0, Width - 1);
                    sum += value(sx, sy);
                }
            }

            return sum / ((Radius * 2 + 1) * (Radius * 2 + 1));
        }

        double Input(int x, int y) => pixels[(y * Width + x) * 4] / 255.0;

        var a = new double[Width * Height];
        var b = new double[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var mean = Box(x, y, Input);
                var meanSquare = Box(x, y, (sx, sy) => Input(sx, sy) * Input(sx, sy));
                var variance = Math.Max(0.0, meanSquare - mean * mean);
                var coefficient = variance / (variance + Epsilon);
                a[y * Width + x] = coefficient;
                b[y * Width + x] = mean * (1 - coefficient);
            }
        }

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var meanA = Box(x, y, (sx, sy) => a[sy * Width + sx]);
                var meanB = Box(x, y, (sx, sy) => b[sy * Width + sx]);
                var expected = Math.Clamp(meanA * Input(x, y) + meanB, 0.0, 1.0);

                var actual = result[(y * Width + x) * 4];
                Math.Abs(actual - expected * 255.0).ShouldBeLessThanOrEqualTo(1.0, $"pixel ({x},{y})");
            }
        }
    }

    [Fact]
    public void GuidedFilter_BandSizeDoesNotChangeTheResult()
    {
        using var source = Image.Create(23, 19, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < 19; y++)
        {
            for (var x = 0; x < 23; x++)
            {
                var o = (y * 23 + x) * 4;
                var value = (byte)(90 + (x * 7 + y * 13) % 41);
                span[o] = span[o + 1] = span[o + 2] = value;
                span[o + 3] = 255;
            }
        }

        using var narrow = source.GuidedFilter(3, 0.01f, bandRows: 5);
        using var wide = source.GuidedFilter(3, 0.01f, bandRows: 1024);

        narrow.WritablePixels.ToArray().ShouldBe(wide.WritablePixels.ToArray());
    }

    [Fact]
    public void GuidedFilter_RejectsIllegalParameters()
    {
        using var source = Solid(5, 5, 10);
        Should.Throw<ArgumentOutOfRangeException>(() => source.GuidedFilter(0));
        Should.Throw<ArgumentOutOfRangeException>(() => source.GuidedFilter(1, 0f));
    }
}
