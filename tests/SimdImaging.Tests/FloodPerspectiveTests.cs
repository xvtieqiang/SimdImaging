using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Tests;

public class FloodPerspectiveTests
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
    public void FloodFill_StopsAtABarrier()
    {
        using var image = Solid(9, 5, 0);
        for (var y = 0; y < 5; y++)
        {
            var o = (y * 9 + 4) * 4;
            image.WritablePixels[o] = 255;
            image.WritablePixels[o + 1] = 255;
            image.WritablePixels[o + 2] = 255;
        }

        image.FloodFill(1, 1, new RgbaColor(255, 0, 0, 255));

        At(image, 0, 0).ShouldBe((byte)255);   // red channel
        At(image, 3, 4).ShouldBe((byte)255);
        At(image, 4, 2).ShouldBe((byte)255);   // barrier is white in every channel
        image.WritablePixels[(2 * 9 + 4) * 4 + 1].ShouldBe((byte)255);
        At(image, 5, 2).ShouldBe((byte)0);     // other side untouched
        At(image, 8, 0).ShouldBe((byte)0);
    }

    [Fact]
    public void FloodFill_WithToleranceCrossesSimilarColours()
    {
        using var image = Solid(5, 1, 100);
        image.WritablePixels[4 * 4] = 101;   // x = 4, one code value away in red

        image.FloodFill(0, 0, new RgbaColor(255, 0, 0, 255), tolerance: 0.01f);

        At(image, 4, 0).ShouldBe((byte)255);
    }

    [Fact]
    public void FloodFill_RejectsASeedOutsideTheImage()
    {
        using var image = Solid(3, 3, 0);
        Should.Throw<ArgumentOutOfRangeException>(() => image.FloodFill(3, 0, RgbaColor.White));
        Should.Throw<ArgumentOutOfRangeException>(() => image.FloodFill(0, -1, RgbaColor.White));
    }

    [Fact]
    public void Perspective_IdentityIsACopy()
    {
        using var source = Solid(5, 4, 77);
        float[] identity = [1, 0, 0, 0, 1, 0, 0, 0];

        using var result = source.Perspective(identity, 5, 4);

        result.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Perspective_TranslationMovesTheImage()
    {
        using var source = Image.Create(4, 1, WorkingFormat.Rgba8);
        source.WritablePixels[0] = 255;      // single white pixel at the left
        source.WritablePixels[3] = 255;

        float[] shiftRight = [1, 0, 2, 0, 1, 0, 0, 0];
        using var result = source.Perspective(shiftRight, 4, 1);

        At(result, 2, 0).ShouldBe((byte)255);
        At(result, 0, 0).ShouldBe((byte)0);
    }

    [Fact]
    public void Perspective_RejectsBadInput()
    {
        using var source = Solid(3, 3, 10);
        Should.Throw<ArgumentException>(() => source.Perspective(new float[7], 3, 3));
        Should.Throw<ArgumentException>(() => source.Perspective(new float[8], 3, 3));   // all zeros = singular
    }

    [Fact]
    public void WebSafe_Has216ColoursInStepsOf51()
    {
        var palette = SimdImaging.Processing.PaletteQuantizer.WebSafe;

        palette.Count.ShouldBe(216);
        foreach (var colour in palette.Colors)
        {
            (colour.R % 51).ShouldBe(0);
            (colour.G % 51).ShouldBe(0);
            (colour.B % 51).ShouldBe(0);
        }
    }

    [Fact]
    public void Dither_BreaksUpAFlatMidGreyThatPlainMappingCollapses()
    {
        using var source = Solid(8, 1, 128);
        var blackAndWhite = new ColorPalette(new[]
        {
            new RgbaColor(0, 0, 0, 255),
            new RgbaColor(255, 255, 255, 255),
        });

        using var plain = source.MapToPalette(blackAndWhite);
        using var dithered = source.DitherToPalette(blackAndWhite);

        var plainValues = new HashSet<byte>();
        for (var x = 0; x < 8; x++)
            plainValues.Add(At(plain, x, 0));
        plainValues.Count.ShouldBe(1);

        var ditheredValues = new HashSet<byte>();
        for (var x = 0; x < 8; x++)
            ditheredValues.Add(At(dithered, x, 0));
        ditheredValues.Count.ShouldBe(2);
    }
}
