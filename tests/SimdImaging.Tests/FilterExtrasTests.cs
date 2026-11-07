namespace SimdImaging.Tests;

public class FilterExtrasTests
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

    [Fact]
    public void Pixelate_AveragesWithinABlock()
    {
        using var source = Image.Create(2, 1, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        span[0] = span[1] = span[2] = 0;
        span[3] = 255;
        span[4] = span[5] = span[6] = 200;
        span[7] = 255;

        using var pixelated = source.Pixelate(2);

        Red(pixelated, 0, 0).ShouldBe((byte)100);
        Red(pixelated, 1, 0).ShouldBe((byte)100);
    }

    [Fact]
    public void Pixelate_BlockOfOneIsACopy()
    {
        using var source = Solid(7, 5, 33);
        using var pixelated = source.Pixelate(1);

        pixelated.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Pixelate_LeavesAUniformImageAlone()
    {
        using var source = Solid(9, 7, 77);
        using var pixelated = source.Pixelate(4);

        for (var y = 0; y < 7; y++)
        {
            for (var x = 0; x < 9; x++)
                Red(pixelated, x, y).ShouldBe((byte)77);
        }
    }

    [Fact]
    public void Pixelate_RejectsNonPositiveBlocks()
    {
        using var source = Solid(4, 4, 10);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Pixelate(0));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Pixelate(-2));
    }

    [Fact]
    public void Vignette_LeavesTheCentreAndDarkensTheCorners()
    {
        using var source = Solid(21, 21, 255);
        using var vignetted = source.Vignette(0.5f, 1f);

        Red(vignetted, 10, 10).ShouldBe((byte)255);
        Red(vignetted, 0, 0).ShouldBeLessThan((byte)64);
        Red(vignetted, 20, 0).ShouldBeLessThan((byte)64);
    }

    [Fact]
    public void Vignette_ZeroStrengthIsACopy()
    {
        using var source = Solid(8, 6, 90);
        using var vignetted = source.Vignette(0.5f, 0f);

        vignetted.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Glow_SpreadsAHaloAroundHighlights()
    {
        using var source = Image.Create(21, 21, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 8; y <= 12; y++)
        {
            for (var x = 8; x <= 12; x++)
            {
                var o = (y * 21 + x) * 4;
                span[o] = span[o + 1] = span[o + 2] = 255;
                span[o + 3] = 255;
            }
        }

        using var glowed = source.Glow(0.5f, 2f, 1f);

        Red(glowed, 10, 10).ShouldBe((byte)255);
        // Four pixels above the bright square: only the blurred halo can light this up.
        Red(glowed, 10, 4).ShouldBeGreaterThan((byte)0);
        Red(glowed, 0, 0).ShouldBe((byte)0);
    }

    [Fact]
    public void Glow_WithThresholdOneChangesNothing()
    {
        // Nothing is strictly brighter than 1, so the highlight mask is empty and the image must not move.
        using var source = Solid(9, 9, 200);
        using var glowed = source.Glow(1f, 2f, 1f);

        glowed.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Glow_RejectsIllegalParameters()
    {
        using var source = Solid(5, 5, 10);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Glow(1.5f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Glow(0.5f, 0f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Glow(0.5f, 2f, -1f));
    }
}
