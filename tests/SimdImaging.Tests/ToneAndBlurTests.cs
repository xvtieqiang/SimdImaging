namespace SimdImaging.Tests;

public class ToneAndBlurTests
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

    private static byte At(Image image, int x, int y) => image.WritablePixels[(y * image.Width + x) * 4];

    [Fact]
    public void ColorBalance_ShiftsShadowsMoreThanHighlights()
    {
        using var dark = Solid(1, 1, 20);
        using var bright = Solid(1, 1, 230);
        var shifts = new SimdImaging.Processing.ColorBalanceShifts(0.2f, 0f, 0f);
        var none = new SimdImaging.Processing.ColorBalanceShifts(0f, 0f, 0f);

        using var darkResult = dark.ColorBalance(shifts, none, none);
        using var brightResult = bright.ColorBalance(shifts, none, none);

        var darkDelta = darkResult.WritablePixels[0] - dark.WritablePixels[0];
        var brightDelta = brightResult.WritablePixels[0] - bright.WritablePixels[0];
        darkDelta.ShouldBeGreaterThan(brightDelta);
        brightDelta.ShouldBeLessThan((byte)10);
    }

    [Fact]
    public void ChannelSwap_ReordersChannels()
    {
        var image = Image.Create(1, 1, WorkingFormat.Rgba8);
        using (image)
        {
            image.WritablePixels[0] = 10;
            image.WritablePixels[1] = 20;
            image.WritablePixels[2] = 30;
            image.WritablePixels[3] = 40;

            using var swapped = image.ChannelSwap(2, 1, 0);
            swapped.WritablePixels[..4].ToArray().ShouldBe(new byte[] { 30, 20, 10, 40 });
        }
    }

    [Fact]
    public void ChannelSwap_RejectsAnUnknownChannel()
    {
        using var image = Solid(1, 1, 100);
        Should.Throw<ArgumentOutOfRangeException>(() => image.ChannelSwap(3, 1, 0));
    }

    [Fact]
    public void BlackWhite_ProducesOnlyPureBlackOrWhite()
    {
        using var image = Solid(3, 1, 100);
        image.WritablePixels[4] = 200;

        using var result = image.BlackWhite(0.5f);

        result.WritablePixels[0].ShouldBe((byte)0);
        result.WritablePixels[4].ShouldBe((byte)255);
    }

    [Fact]
    public void MotionBlur_SpreadsHorizontallyOnly()
    {
        using var image = Solid(9, 9, 0);
        var centre = (4 * 9 + 4) * 4;
        image.WritablePixels[centre] = 255;
        image.WritablePixels[centre + 1] = 255;
        image.WritablePixels[centre + 2] = 255;
        image.WritablePixels[centre + 3] = 255;

        using var blurred = image.MotionBlur(5, 0f);

        At(blurred, 4, 4).ShouldBeGreaterThan((byte)0);
        At(blurred, 3, 4).ShouldBeGreaterThan((byte)0);
        At(blurred, 4, 3).ShouldBe((byte)0);
        At(blurred, 4, 5).ShouldBe((byte)0);
    }

    [Fact]
    public void BokehBlur_UsesADiscNotABox()
    {
        using var image = Solid(9, 9, 0);
        var centre = (4 * 9 + 4) * 4;
        image.WritablePixels[centre] = 255;
        image.WritablePixels[centre + 1] = 255;
        image.WritablePixels[centre + 2] = 255;
        image.WritablePixels[centre + 3] = 255;

        using var blurred = image.BokehBlur(1);

        // radius 1 disc = centre + four orthogonal neighbours; the diagonal corners are outside it.
        At(blurred, 4, 4).ShouldBeGreaterThan((byte)0);
        At(blurred, 3, 4).ShouldBeGreaterThan((byte)0);
        At(blurred, 5, 4).ShouldBeGreaterThan((byte)0);
        At(blurred, 3, 3).ShouldBe((byte)0);
        At(blurred, 5, 5).ShouldBe((byte)0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    public void MotionBlur_KeepsAConstantImageConstant(int angle)
    {
        using var image = Solid(7, 7, 66);
        using var blurred = image.MotionBlur(5, angle);

        var span = blurred.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            ((int)span[i]).ShouldBeInRange(65, 67);
            span[i + 3].ShouldBe((byte)255);
        }
    }

    [Fact]
    public void BokehBlur_KeepsAConstantImageConstant()
    {
        using var image = Solid(7, 7, 66);
        using var blurred = image.BokehBlur(2);

        var span = blurred.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
            ((int)span[i]).ShouldBeInRange(65, 67);
    }
}
