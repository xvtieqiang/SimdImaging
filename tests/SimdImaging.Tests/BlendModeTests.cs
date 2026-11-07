using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class BlendModeTests
{
    private static Image Pixel(byte r, byte g, byte b, byte a = 255)
    {
        var image = Image.Create(1, 1, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        span[0] = r;
        span[1] = g;
        span[2] = b;
        span[3] = a;
        return image;
    }

    private static byte[] BlendOne(BlendMode mode, byte[] background, byte[] overlay)
    {
        using var baseImage = Pixel(background[0], background[1], background[2], background[3]);
        using var top = Pixel(overlay[0], overlay[1], overlay[2], overlay[3]);
        using var result = baseImage.Blend(top, 0, 0, mode);
        return result.WritablePixels[..4].ToArray();
    }

    [Fact]
    public void Multiply_DarkensTowardsBlack()
    {
        var result = BlendOne(BlendMode.Multiply, new byte[] { 128, 128, 128, 255 }, new byte[] { 128, 128, 128, 255 });
        ((int)result[0]).ShouldBeInRange(63, 65);
    }

    [Fact]
    public void Screen_LightensTowardsWhite()
    {
        var result = BlendOne(BlendMode.Screen, new byte[] { 128, 128, 128, 255 }, new byte[] { 128, 128, 128, 255 });
        ((int)result[0]).ShouldBeInRange(190, 192);
    }

    [Fact]
    public void DarkenAndLighten_PickPerChannel()
    {
        var backdrop = new byte[] { 200, 50, 10, 255 };
        var source = new byte[] { 100, 100, 100, 255 };

        BlendOne(BlendMode.Darken, backdrop, source)[0].ShouldBe((byte)100);
        BlendOne(BlendMode.Lighten, backdrop, source)[0].ShouldBe((byte)200);
        BlendOne(BlendMode.Darken, backdrop, source)[1].ShouldBe((byte)50);
        BlendOne(BlendMode.Lighten, backdrop, source)[1].ShouldBe((byte)100);
    }

    [Fact]
    public void Difference_IsAbsolute()
    {
        BlendOne(BlendMode.Difference, new byte[] { 200, 50, 0, 255 }, new byte[] { 100, 100, 0, 255 })[0]
            .ShouldBe((byte)100);
        BlendOne(BlendMode.Difference, new byte[] { 200, 50, 0, 255 }, new byte[] { 100, 100, 0, 255 })[1]
            .ShouldBe((byte)50);
    }

    [Fact]
    public void AddAndSubtract_Clamp()
    {
        BlendOne(BlendMode.Add, new byte[] { 200, 10, 0, 255 }, new byte[] { 100, 10, 0, 255 })[0]
            .ShouldBe((byte)255);
        BlendOne(BlendMode.Subtract, new byte[] { 100, 10, 0, 255 }, new byte[] { 200, 10, 0, 255 })[0]
            .ShouldBe((byte)0);
    }

    [Fact]
    public void Normal_MatchesSourceOverExactly()
    {
        using var backdrop = Image.Create(3, 3, WorkingFormat.Rgba8);
        backdrop.WritablePixels.Fill(40);
        using var overlay = Pixel(200, 30, 90, 128);

        using var blended = backdrop.Blend(overlay, 1, 1, BlendMode.Normal);
        using var composited = backdrop.Composite(overlay, 1, 1, PorterDuff.SourceOver);

        blended.WritablePixels.ToArray().ShouldBe(composited.WritablePixels.ToArray());
    }

    [Fact]
    public void Luminosity_OfIdenticalColoursIsIdentity()
    {
        var colour = new byte[] { 200, 100, 50, 255 };
        var result = BlendOne(BlendMode.Luminosity, colour, colour);

        ((int)result[0]).ShouldBeInRange(198, 202);
        ((int)result[1]).ShouldBeInRange(98, 102);
        ((int)result[2]).ShouldBeInRange(48, 52);
    }

    [Fact]
    public void Hue_FromAGreySourceProducesAGrey()
    {
        var result = BlendOne(BlendMode.Hue, new byte[] { 200, 100, 50, 255 }, new byte[] { 128, 128, 128, 255 });

        // Saturation comes from the source (grey, 0), so the result must be neutral.
        result[0].ShouldBe(result[1]);
        result[1].ShouldBe(result[2]);
    }

    [Fact]
    public void Overlay_IsHardLightWithOperandsSwapped()
    {
        var backdrop = new byte[] { 180, 60, 20, 255 };
        var source = new byte[] { 70, 200, 120, 255 };

        var overlay = BlendOne(BlendMode.Overlay, backdrop, source);
        var hardLight = BlendOne(BlendMode.HardLight, source, backdrop);

        overlay[0].ShouldBe(hardLight[0]);
        overlay[1].ShouldBe(hardLight[1]);
        overlay[2].ShouldBe(hardLight[2]);
    }
}
