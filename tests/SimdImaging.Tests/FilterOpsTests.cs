namespace SimdImaging.Tests;

public class FilterOpsTests
{
    private static Image Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = r;
            span[i + 1] = g;
            span[i + 2] = b;
            span[i + 3] = a;
        }

        return image;
    }

    private static byte[] PixelAt(Image image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return image.WritablePixels.Slice(i, 4).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void BoxBlur_KeepsAConstantImageConstant(int radius)
    {
        using var source = Solid(9, 7, 77, 88, 99);
        using var blurred = source.BoxBlur(radius);

        var span = blurred.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            ((int)span[i]).ShouldBeInRange(76, 78);
            ((int)span[i + 1]).ShouldBeInRange(87, 89);
            ((int)span[i + 2]).ShouldBeInRange(98, 100);
            span[i + 3].ShouldBe((byte)255);
        }
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(2f)]
    [InlineData(5f)]
    public void GaussianBlur_KeepsAConstantImageConstant(float sigma)
    {
        using var source = Solid(11, 9, 40, 120, 200);
        using var blurred = source.GaussianBlur(sigma);

        var span = blurred.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            ((int)span[i]).ShouldBeInRange(39, 41);
            ((int)span[i + 1]).ShouldBeInRange(119, 121);
            ((int)span[i + 2]).ShouldBeInRange(199, 201);
        }
    }

    [Fact]
    public void BoxBlur_SpreadsAnImpulseEvenly()
    {
        using var source = Solid(5, 5, 0, 0, 0);
        var centre = (2 * 5 + 2) * 4;
        source.WritablePixels[centre] = 255;

        using var blurred = source.BoxBlur(1);

        // 255 / 9 = 28.3
        PixelAt(blurred, 2, 2)[0].ShouldBe((byte)28);
        PixelAt(blurred, 1, 1)[0].ShouldBe((byte)28);
        PixelAt(blurred, 0, 0)[0].ShouldBe((byte)0);
    }

    [Fact]
    public void BoxBlur_ZeroRadiusIsACopy()
    {
        using var source = Solid(4, 4, 12, 34, 56);
        using var blurred = source.BoxBlur(0);
        blurred.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Blur_DoesNotBleedTransparentColour()
    {
        using var source = Solid(9, 9, 0, 0, 0, a: 0);
        var centre = (4 * 9 + 4) * 4;
        source.WritablePixels[centre] = 255;
        source.WritablePixels[centre + 3] = 255;

        using var blurred = source.BoxBlur(2);

        var corner = PixelAt(blurred, 0, 0);
        corner[0].ShouldBe((byte)0);
        corner[3].ShouldBe((byte)0);
    }

    [Fact]
    public void GaussianBlur_RejectsInvalidSigma()
    {
        using var source = Solid(2, 2, 1, 2, 3);
        Should.Throw<ArgumentOutOfRangeException>(() => source.GaussianBlur(0f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.GaussianBlur(float.NaN));
    }

    [Fact]
    public void BoxBlur_RejectsNegativeRadius()
    {
        using var source = Solid(2, 2, 1, 2, 3);
        Should.Throw<ArgumentOutOfRangeException>(() => source.BoxBlur(-1));
    }

    [Fact]
    public void Median3x3_RemovesAnImpulse()
    {
        using var source = Solid(5, 5, 10, 20, 30);
        var centre = (2 * 5 + 2) * 4;
        source.WritablePixels[centre] = 250;

        using var median = source.Median3x3();

        PixelAt(median, 2, 2).ShouldBe(new byte[] { 10, 20, 30, 255 });
        PixelAt(median, 0, 0).ShouldBe(new byte[] { 10, 20, 30, 255 });
    }

    [Fact]
    public void Median3x3_KeepsAConstantImageConstant()
    {
        using var source = Solid(6, 4, 55, 66, 77, a: 88);
        using var median = source.Median3x3();

        var span = median.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i].ShouldBe((byte)55);
            span[i + 1].ShouldBe((byte)66);
            span[i + 2].ShouldBe((byte)77);
            span[i + 3].ShouldBe((byte)88);
        }
    }

    [Fact]
    public void Sobel_FlatImageIsBlack()
    {
        using var source = Solid(7, 5, 120, 120, 120);
        using var edges = source.Sobel();

        var span = edges.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i].ShouldBe((byte)0);
            span[i + 3].ShouldBe((byte)255);
        }
    }

    [Fact]
    public void Sobel_RespondsAtAVerticalEdgeAndNotAwayFromIt()
    {
        using var source = Solid(7, 3, 0, 0, 0);
        for (var y = 0; y < 3; y++)
        {
            for (var x = 3; x < 7; x++)
            {
                var o = (y * 7 + x) * 4;
                source.WritablePixels[o] = 255;
                source.WritablePixels[o + 1] = 255;
                source.WritablePixels[o + 2] = 255;
            }
        }

        using var edges = source.Sobel();

        PixelAt(edges, 0, 1)[0].ShouldBe((byte)0);
        PixelAt(edges, 2, 1)[0].ShouldBe((byte)255);
        PixelAt(edges, 3, 1)[0].ShouldBe((byte)255);
        PixelAt(edges, 6, 1)[0].ShouldBe((byte)0);
    }

    [Fact]
    public void Bilateral_KeepsAConstantImageConstant()
    {
        using var source = Solid(9, 7, 60, 120, 180);
        using var filtered = source.Bilateral(2, sigmaRange: 0.1f);

        var span = filtered.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            ((int)span[i]).ShouldBeInRange(59, 61);
            ((int)span[i + 1]).ShouldBeInRange(119, 121);
            ((int)span[i + 2]).ShouldBeInRange(179, 181);
        }
    }

    [Fact]
    public void Bilateral_PreservesAStepEdge()
    {
        using var source = Solid(9, 5, 0, 0, 0);
        for (var y = 0; y < 5; y++)
        {
            for (var x = 5; x < 9; x++)
            {
                var o = (y * 9 + x) * 4;
                source.WritablePixels[o] = 255;
                source.WritablePixels[o + 1] = 255;
                source.WritablePixels[o + 2] = 255;
            }
        }

        using var filtered = source.Bilateral(2, sigmaRange: 0.05f);

        PixelAt(filtered, 0, 2)[0].ShouldBe((byte)0);
        PixelAt(filtered, 8, 2)[0].ShouldBe((byte)255);
        // A Gaussian blur would smear the boundary into two mid-grey pixels; the range term must not.
        PixelAt(filtered, 4, 2)[0].ShouldBeLessThan((byte)8);
        PixelAt(filtered, 5, 2)[0].ShouldBeGreaterThan((byte)247);
    }

    [Fact]
    public void Bilateral_RejectsInvalidParameters()
    {
        using var source = Solid(3, 3, 10, 10, 10);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Bilateral(0));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Bilateral(2, sigmaRange: 0f));
    }
}
