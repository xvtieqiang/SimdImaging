using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

public class CanvasTests
{
    private static Image Filled(int width, int height, RgbaColor colour)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        image.Fill(colour);
        return image;
    }

    private static byte AlphaAt(Image image, int x, int y) => image.WritablePixels[(y * image.Width + x) * 4 + 3];

    private static byte Red(Image image, int x, int y) => image.WritablePixels[(y * image.Width + x) * 4];

    [Fact]
    public void Trim_RemovesAUniformBorder()
    {
        using var source = Filled(10, 8, RgbaColor.Black);
        source.FillRect(new PixelRect(2, 1, 6, 4), RgbaColor.White);

        using var trimmed = source.Trim();

        trimmed.Width.ShouldBe(6);
        trimmed.Height.ShouldBe(4);
        Red(trimmed, 0, 0).ShouldBe((byte)255);
        Red(trimmed, 5, 3).ShouldBe((byte)255);
    }

    [Fact]
    public void Trim_UniformImageBecomesOnePixel()
    {
        using var source = Filled(9, 7, new RgbaColor(12, 34, 56, 255));
        using var trimmed = source.Trim();

        trimmed.Width.ShouldBe(1);
        trimmed.Height.ShouldBe(1);
    }

    [Fact]
    public void Trim_ToleranceTreatsNearMatchesAsBackground()
    {
        using var source = Filled(8, 8, new RgbaColor(100, 100, 100, 255));
        source.FillRect(new PixelRect(2, 2, 4, 4), new RgbaColor(103, 103, 103, 255));

        using var exact = source.Trim();
        exact.Width.ShouldBe(4);

        // 3/255 ≈ 0.012, inside a 0.02 tolerance.
        using var loose = source.Trim(0.02f);
        loose.Width.ShouldBe(1);
    }

    [Fact]
    public void BoxPad_KeepsTheAspectRatioAndPads()
    {
        using var source = Filled(4, 2, RgbaColor.White);
        using var resized = source.Resize(8, 8, ResizeMode.BoxPad, Anchor.Centre, Resampler.Nearest, RgbaColor.Black);

        resized.Width.ShouldBe(8);
        resized.Height.ShouldBe(8);
        Red(resized, 4, 1).ShouldBe((byte)0);
        Red(resized, 4, 2).ShouldBe((byte)255);
        Red(resized, 4, 5).ShouldBe((byte)255);
        Red(resized, 4, 6).ShouldBe((byte)0);
    }

    [Fact]
    public void CropPad_KeepsTheAspectRatioAndCrops()
    {
        using var source = Filled(4, 2, RgbaColor.White);
        source.FillRect(new PixelRect(0, 0, 1, 2), new RgbaColor(255, 0, 0, 255));

        using var resized = source.Resize(8, 8, ResizeMode.CropPad, Anchor.Centre, Resampler.Nearest, RgbaColor.Black);

        resized.Width.ShouldBe(8);
        resized.Height.ShouldBe(8);
        // Covering 8×8 from 4×2 scales by 4 (16×8) and crops the outer quarters, which is where the red
        // column lived.
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
                Red(resized, x, y).ShouldBe((byte)255, $"pixel ({x},{y}) should be white");
        }
    }

    [Fact]
    public void MaxAndMin_ReturnTheScaledSize()
    {
        using var source = Filled(4, 2, RgbaColor.White);

        using var max = source.Resize(8, 8, ResizeMode.Max);
        max.Width.ShouldBe(8);
        max.Height.ShouldBe(4);

        using var min = source.Resize(8, 8, ResizeMode.Min);
        min.Width.ShouldBe(16);
        min.Height.ShouldBe(8);
    }

    [Fact]
    public void Stretch_IgnoresTheAspectRatio()
    {
        using var source = Filled(4, 2, RgbaColor.White);
        using var resized = source.Resize(5, 9, ResizeMode.Stretch);

        resized.Width.ShouldBe(5);
        resized.Height.ShouldBe(9);
    }

    [Fact]
    public void ResizeCanvas_PadsWithoutScaling()
    {
        using var source = Filled(4, 4, RgbaColor.White);
        using var padded = source.ResizeCanvas(8, 6, Anchor.Centre, RgbaColor.Black);

        padded.Width.ShouldBe(8);
        padded.Height.ShouldBe(6);
        Red(padded, 0, 0).ShouldBe((byte)0);
        Red(padded, 2, 1).ShouldBe((byte)255);
        Red(padded, 5, 4).ShouldBe((byte)255);
        Red(padded, 7, 5).ShouldBe((byte)0);
    }

    [Fact]
    public void ResizeCanvas_CropsWithoutScaling()
    {
        using var source = Filled(4, 4, RgbaColor.White);
        source.FillRect(new PixelRect(0, 0, 1, 4), new RgbaColor(255, 0, 0, 255));

        using var cropped = source.ResizeCanvas(2, 2, Anchor.Centre);

        cropped.Width.ShouldBe(2);
        cropped.Height.ShouldBe(2);
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
                Red(cropped, x, y).ShouldBe((byte)255);
        }
    }

    [Fact]
    public void Tile_RepeatsAndMirrors()
    {
        using var source = Image.Create(2, 1, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        span[0] = span[1] = span[2] = 255;
        span[3] = 255;
        span[4] = span[5] = span[6] = 0;
        span[7] = 255;

        using var tiled = source.Tile(4, 2);
        Red(tiled, 0, 0).ShouldBe((byte)255);
        Red(tiled, 1, 0).ShouldBe((byte)0);
        Red(tiled, 2, 0).ShouldBe((byte)255);
        Red(tiled, 3, 0).ShouldBe((byte)0);
        Red(tiled, 3, 1).ShouldBe((byte)0);

        using var mirrored = source.Tile(4, 1, mirror: true);
        Red(mirrored, 0, 0).ShouldBe((byte)255);
        Red(mirrored, 1, 0).ShouldBe((byte)0);
        Red(mirrored, 2, 0).ShouldBe((byte)0);
        Red(mirrored, 3, 0).ShouldBe((byte)255);
    }

    [Fact]
    public void Tile_ReachesTheRequestedSizeWithOddSource()
    {
        using var source = Filled(3, 3, RgbaColor.White);
        using var tiled = source.Tile(7, 5);

        tiled.Width.ShouldBe(7);
        tiled.Height.ShouldBe(5);
        Red(tiled, 6, 4).ShouldBe((byte)255);
    }

    [Fact]
    public void RoundCorners_ClipsTheCornersAntialiased()
    {
        using var source = Filled(20, 20, RgbaColor.White);
        using var rounded = source.RoundCorners(6f);

        AlphaAt(rounded, 10, 10).ShouldBe((byte)255);
        AlphaAt(rounded, 0, 0).ShouldBe((byte)0);
        AlphaAt(rounded, 19, 19).ShouldBe((byte)0);

        var partial = 0;
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 20; x++)
            {
                var alpha = AlphaAt(rounded, x, y);
                if (alpha is > 0 and < 255)
                    partial++;
            }
        }

        partial.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void RoundCorners_AtHalfTheShortSideRoundsTheEndsCompletely()
    {
        using var source = Filled(20, 10, RgbaColor.White);
        using var rounded = source.RoundCorners(50f);

        // The radius clamps to half the short side, so the two ends become semicircles and the middle stays
        // straight: a stadium, which for a square image is a circle.
        AlphaAt(rounded, 10, 5).ShouldBe((byte)255);
        AlphaAt(rounded, 0, 0).ShouldBe((byte)0);
        AlphaAt(rounded, 19, 9).ShouldBe((byte)0);
        AlphaAt(rounded, 0, 5).ShouldBeGreaterThan((byte)0);
    }

    [Fact]
    public void Canvas_RejectsIllegalParameters()
    {
        using var source = Filled(4, 4, RgbaColor.White);

        Should.Throw<ArgumentOutOfRangeException>(() => source.Resize(0, 4, ResizeMode.BoxPad));
        Should.Throw<ArgumentOutOfRangeException>(() => source.ResizeCanvas(4, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Tile(0, 4));
        Should.Throw<ArgumentOutOfRangeException>(() => source.RoundCorners(0f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Trim(-0.1f));
    }
}
