namespace SimdImaging.Tests;

public class MorphologyTests
{
    private static Image Solid(int width, int height, byte value, byte alpha = 255)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = value;
            span[i + 1] = value;
            span[i + 2] = value;
            span[i + 3] = alpha;
        }

        return image;
    }

    private static void Set(Image image, int x, int y, byte value, byte alpha = 255)
    {
        var o = (y * image.Width + x) * 4;
        image.WritablePixels[o] = value;
        image.WritablePixels[o + 1] = value;
        image.WritablePixels[o + 2] = value;
        image.WritablePixels[o + 3] = alpha;
    }

    private static byte At(Image image, int x, int y) => image.WritablePixels[(y * image.Width + x) * 4];

    [Fact]
    public void Erode_ShrinksABrightSquareByOnePixel()
    {
        using var source = Solid(7, 7, 0);
        for (var y = 2; y < 5; y++)
            for (var x = 2; x < 5; x++)
                Set(source, x, y, 255);

        using var eroded = source.Erode3x3();

        At(eroded, 3, 3).ShouldBe((byte)255);
        At(eroded, 2, 2).ShouldBe((byte)0);
        At(eroded, 2, 3).ShouldBe((byte)0);
    }

    [Fact]
    public void Dilate_GrowsASinglePixelIntoThreeByThree()
    {
        using var source = Solid(7, 7, 0);
        Set(source, 3, 3, 255);

        using var dilated = source.Dilate3x3();

        for (var y = 2; y <= 4; y++)
            for (var x = 2; x <= 4; x++)
                At(dilated, x, y).ShouldBe((byte)255);

        At(dilated, 1, 3).ShouldBe((byte)0);
    }

    [Fact]
    public void Open_RemovesABrightSpeck()
    {
        using var source = Solid(9, 9, 0);
        Set(source, 4, 4, 255);

        using var opened = source.Open3x3();

        var span = opened.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
            span[i].ShouldBe((byte)0);
    }

    [Fact]
    public void Close_FillsADarkHole()
    {
        using var source = Solid(9, 9, 255);
        Set(source, 4, 4, 0);

        using var closed = source.Close3x3();

        At(closed, 4, 4).ShouldBe((byte)255);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Morphology_KeepsAConstantImageConstant(bool erode)
    {
        using var source = Solid(6, 5, 66);
        using var result = erode ? source.Erode3x3() : source.Dilate3x3();

        var span = result.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i].ShouldBe((byte)66);
            span[i + 3].ShouldBe((byte)255);
        }
    }

    [Fact]
    public void Erode_ShrinksAlphaCoverage()
    {
        using var source = Solid(7, 7, 128, alpha: 0);
        for (var y = 2; y < 5; y++)
            for (var x = 2; x < 5; x++)
                Set(source, x, y, 128, alpha: 255);

        using var eroded = source.Erode3x3();

        eroded.WritablePixels[(3 * 7 + 3) * 4 + 3].ShouldBe((byte)255);
        eroded.WritablePixels[(2 * 7 + 2) * 4 + 3].ShouldBe((byte)0);
    }
}
