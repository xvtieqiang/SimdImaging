using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class ColorBatchTests
{
    private static Image Solid(byte r, byte g, byte b, byte a = 255)
    {
        var image = Image.Create(1, 1, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        span[0] = r;
        span[1] = g;
        span[2] = b;
        span[3] = a;
        return image;
    }

    private static byte[] Pixels(Image image) => image.WritablePixels.ToArray();

    [Fact]
    public void ColorMatrix_IdentityIsANoOp()
    {
        using var source = Solid(37, 128, 210);
        float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

        using var result = source.Pipe().ColorMatrix(identity).ToImage();
        Pixels(result).ShouldBe(Pixels(source));
    }

    [Fact]
    public void ColorMatrix_SwapsChannels()
    {
        using var source = Solid(255, 0, 0);
        float[] swapGreenAndRed = [0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

        using var result = source.Pipe().ColorMatrix(swapGreenAndRed).ToImage();
        Pixels(result).ShouldBe(new byte[] { 0, 255, 0, 255 });
    }

    [Fact]
    public void ColorMatrix_RejectsWrongLength()
    {
        using var source = Solid(1, 2, 3);
        Should.Throw<ArgumentException>(() => source.Pipe().ColorMatrix(new float[9]).ToImage());
    }

    [Fact]
    public void HueRotate_ZeroIsIdentity()
    {
        using var source = Solid(120, 60, 200);
        using var result = source.Pipe().HueRotate(0f).ToImage();

        for (var i = 0; i < 3; i++)
            Math.Abs(result.WritablePixels[i] - source.WritablePixels[i]).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void HueRotate_360IsIdentity()
    {
        using var source = Solid(120, 60, 200);
        using var result = source.Pipe().HueRotate(360f).ToImage();

        for (var i = 0; i < 3; i++)
            Math.Abs(result.WritablePixels[i] - source.WritablePixels[i]).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Sepia_ZeroIsIdentityAndFullWarmsWhite()
    {
        using var source = Solid(255, 255, 255);

        using var none = source.Pipe().Sepia(0f).ToImage();
        Pixels(none).ShouldBe(Pixels(source));

        using var full = source.Pipe().Sepia(1f).ToImage();
        var p = Pixels(full);
        p[0].ShouldBe((byte)255);
        p[1].ShouldBe((byte)255);
        ((int)p[2]).ShouldBeInRange(236, 241);   // 0.937 × 255
    }

    [Fact]
    public void Levels_IdentityLeavesTheImageAlone()
    {
        using var source = Solid(20, 130, 240);
        using var result = source.Pipe().Levels(0f, 1f, 1f).ToImage();

        var before = Pixels(source);
        var after = Pixels(result);
        for (var i = 0; i < 3; i++)
            Math.Abs(after[i] - before[i]).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Levels_StretchesADarkRange()
    {
        using var source = Solid(100, 100, 100);
        using var result = source.Pipe().Levels(100f / 255f, 150f / 255f).ToImage();

        result.WritablePixels[0].ShouldBe((byte)0);
    }

    [Fact]
    public void Levels_RejectsAnEmptyInputRange()
    {
        using var source = Solid(1, 2, 3);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Pipe().Levels(0.5f, 0.5f).ToImage());
        Should.Throw<ArgumentOutOfRangeException>(() => source.Pipe().Levels(0f, 1f, 0f).ToImage());
    }

    [Fact]
    public void AlphaThreshold_SnapsAlphaAndKeepsColour()
    {
        using var low = Solid(10, 20, 30, a: 100);
        using var high = Solid(10, 20, 30, a: 200);

        Pixels(low.Pipe().AlphaThreshold(0.5f).ToImage()).ShouldBe(new byte[] { 10, 20, 30, 0 });
        Pixels(high.Pipe().AlphaThreshold(0.5f).ToImage()).ShouldBe(new byte[] { 10, 20, 30, 255 });
    }

    [Fact]
    public void Normalize_StretchesMinMaxPerChannel()
    {
        var source = Image.Create(2, 1, WorkingFormat.Rgba8);
        using (source)
        {
            var span = source.WritablePixels;
            span[0] = 100;
            span[4] = 150;
            span[1] = 100;
            span[5] = 150;
            span[2] = 100;
            span[6] = 150;
            span[3] = 255;
            span[7] = 255;

            using var result = source.Pipe().Normalize().ToImage();
            result.WritablePixels[0].ShouldBe((byte)0);
            result.WritablePixels[4].ShouldBe((byte)255);
            result.WritablePixels[3].ShouldBe((byte)255);
        }
    }

    [Fact]
    public void Convolve3x3_IdentityKernelIsANoOp()
    {
        using var source = Image.Create(4, 4, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)(i * 7);

        float[] identity = [0, 0, 0, 0, 1, 0, 0, 0, 0];
        using var result = source.Pipe().Convolve3x3(identity).ToImage();

        result.WritablePixels.ToArray().ShouldBe(Pixels(source));
    }

    [Fact]
    public void Convolve3x3_RejectsWrongLength()
    {
        using var source = Solid(1, 2, 3);
        Should.Throw<ArgumentException>(() => source.Pipe().Convolve3x3(new float[8]).ToImage());
    }

    [Fact]
    public void Emboss_FlatAreasBecomeMidGrey()
    {
        using var source = Solid(100, 100, 100);
        using var result = source.Pipe().Emboss().ToImage();

        ((int)result.WritablePixels[0]).ShouldBeInRange(126, 129);
        result.WritablePixels[3].ShouldBe((byte)255);
    }
}
