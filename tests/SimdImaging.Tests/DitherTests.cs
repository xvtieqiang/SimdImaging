using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class DitherTests
{
    private static Image Gradient(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                span[o] = (byte)(x * 255 / (width - 1));
                span[o + 1] = (byte)(y * 255 / (height - 1));
                span[o + 2] = (byte)((x + y) * 255 / (width + height - 2));
                span[o + 3] = 255;
            }
        }

        return image;
    }

    [Fact]
    public void Dither_Bayer2TurnsMidGreyIntoACheckerboard()
    {
        using var source = Image.Create(4, 4, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = span[i + 1] = span[i + 2] = 128;
            span[i + 3] = 255;
        }

        using var dithered = source.Dither(1, DitherKernel.Bayer2);
        var result = dithered.WritablePixels;

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var value = result[(y * 4 + x) * 4];
                var expected = (x + y) % 2 == 1 ? (byte)255 : (byte)0;
                value.ShouldBe(expected, $"pixel ({x},{y})");
            }
        }
    }

    [Theory]
    [InlineData(DitherKernel.None)]
    [InlineData(DitherKernel.FloydSteinberg)]
    [InlineData(DitherKernel.Atkinson)]
    [InlineData(DitherKernel.Burkes)]
    [InlineData(DitherKernel.Sierra)]
    [InlineData(DitherKernel.Stucki)]
    [InlineData(DitherKernel.Bayer2)]
    [InlineData(DitherKernel.Bayer4)]
    [InlineData(DitherKernel.Bayer8)]
    public void Dither_StaysOnTheLevelGrid(DitherKernel kernel)
    {
        using var source = Gradient(23, 17);
        using var dithered = source.Dither(2, kernel);

        // Two bits is four levels; every channel must land on one of them.
        var allowed = new[] { 0, 85, 170, 255 };
        var span = dithered.WritablePixels;
        for (var y = 0; y < dithered.Height; y++)
        {
            for (var x = 0; x < dithered.Width; x++)
            {
                var o = (y * dithered.Width + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var value = span[o + c];
                    allowed.ShouldContain((int)value, $"channel {c} at ({x},{y})");
                }

                span[o + 3].ShouldBe((byte)255);
            }
        }
    }

    [Theory]
    [InlineData(DitherKernel.FloydSteinberg)]
    [InlineData(DitherKernel.Atkinson)]
    [InlineData(DitherKernel.Burkes)]
    [InlineData(DitherKernel.Sierra)]
    [InlineData(DitherKernel.Stucki)]
    [InlineData(DitherKernel.Bayer8)]
    public void Dither_IsDeterministic(DitherKernel kernel)
    {
        using var source = Gradient(31, 19);
        using var first = source.Dither(3, kernel);
        using var second = source.Dither(3, kernel);

        second.WritablePixels.ToArray().ShouldBe(first.WritablePixels.ToArray());
    }

    [Fact]
    public void Dither_KernelsProduceDifferentPatterns()
    {
        using var source = Gradient(32, 32);
        using var floyd = source.Dither(1, DitherKernel.FloydSteinberg);
        using var atkinson = source.Dither(1, DitherKernel.Atkinson);
        using var bayer = source.Dither(1, DitherKernel.Bayer8);

        floyd.WritablePixels.ToArray().ShouldNotBe(atkinson.WritablePixels.ToArray());
        floyd.WritablePixels.ToArray().ShouldNotBe(bayer.WritablePixels.ToArray());
    }

    [Fact]
    public void Dither_NoneRoundsInsteadOfSpreading()
    {
        using var source = Image.Create(2, 1, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        span[0] = span[1] = span[2] = 100;      // 100/255 * 1 + 0.5 = 0.892 -> level 0
        span[3] = 255;
        span[4] = span[5] = span[6] = 200;      // 200/255 + 0.5 = 1.284 -> level 1
        span[7] = 255;

        using var dithered = source.Dither(1, DitherKernel.None);
        dithered.WritablePixels[0].ShouldBe((byte)0);
        dithered.WritablePixels[4].ShouldBe((byte)255);
    }

    [Fact]
    public void Dither_EightBitsIsACopy()
    {
        using var source = Gradient(9, 7);
        using var dithered = source.Dither(8);

        dithered.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Dither_RejectsIllegalBitDepth()
    {
        using var source = Gradient(4, 4);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Dither(0));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Dither(9));
    }

    [Fact]
    public void PaletteDither_KeepsPaletteColours()
    {
        using var source = Gradient(16, 16);
        var palette = new ColorPalette(new[]
        {
            new RgbaColor(0, 0, 0, 255),
            new RgbaColor(255, 255, 255, 255),
        });

        using var dithered = source.DitherToPalette(palette, DitherKernel.Atkinson);

        var span = dithered.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            var value = span[i];
            value.ShouldBeOneOf((byte)0, (byte)255);
            span[i + 1].ShouldBe(value);
        }
    }

    [Fact]
    public void PaletteDither_RejectsOrderedKernels()
    {
        using var source = Gradient(8, 8);
        var palette = new ColorPalette(new[]
        {
            new RgbaColor(0, 0, 0, 255),
            new RgbaColor(255, 255, 255, 255),
        });

        Should.Throw<NotSupportedException>(() => source.DitherToPalette(palette, DitherKernel.Bayer4));
        Should.Throw<NotSupportedException>(() => source.ReduceColors(4, kernel: DitherKernel.Bayer2));
    }
}
