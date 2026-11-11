using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class GeometryTests
{
    private static Image Pattern(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                span[i] = (byte)(x * 17 + 3);
                span[i + 1] = (byte)(y * 29 + 5);
                span[i + 2] = (byte)(x + y);
                span[i + 3] = 255;
            }
        }

        return image;
    }

    private static byte[] PixelAt(Image image, int x, int y)
    {
        var span = image.WritablePixels;
        var i = (y * image.Width + x) * 4;
        return span.Slice(i, 4).ToArray();
    }

    [Fact]
    public void Crop_ReturnsTheRequestedRegion()
    {
        using var source = Pattern(8, 6);
        using var cropped = source.Crop(new PixelRect(2, 1, 3, 4));

        cropped.Width.ShouldBe(3);
        cropped.Height.ShouldBe(4);
        PixelAt(cropped, 0, 0).ShouldBe(PixelAt(source, 2, 1));
        PixelAt(cropped, 2, 3).ShouldBe(PixelAt(source, 4, 4));
    }

    [Fact]
    public void FlipHorizontal_TwiceIsIdentity()
    {
        using var source = Pattern(7, 5);
        using var twice = source.FlipHorizontal().FlipHorizontal();
        twice.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Rotate90_FourTimesIsIdentity()
    {
        using var source = Pattern(7, 5);
        using var rotated = source.Rotate90().Rotate90().Rotate90().Rotate90();
        rotated.Width.ShouldBe(7);
        rotated.Height.ShouldBe(5);
        rotated.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Theory]
    [InlineData(37, 23)]     // width not a multiple of 8: the scalar tail
    [InlineData(64, 16)]     // exactly on the vector boundary
    public void Rotate180_MatchesTheScalarReference(int width, int height)
    {
        using var source = TestImages.Gradient(width, height, WorkingFormat.Rgba8);
        using var rotated = source.Rotate180();

        var from = source.WritablePixels;
        var to = rotated.WritablePixels;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceOffset = ((height - 1 - y) * width + (width - 1 - x)) * 4;
            var targetOffset = (y * width + x) * 4;
            for (var c = 0; c < 4; c++)
                to[targetOffset + c].ShouldBe(from[sourceOffset + c]);
        }

        using var twice = rotated.Rotate180();
        twice.WritablePixels.ToArray().ShouldBe(from.ToArray());
    }

    [Fact]
    public void Rotate180_WorksOnSixteenBit()
    {
        using var source = TestImages.Gradient(33, 17, WorkingFormat.Rgba16);
        using var twice = source.Rotate180().Rotate180();
        twice.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Rotate90_ClockwiseMovesTopLeftToTopRight()
    {
        using var source = Pattern(3, 2);
        using var rotated = source.Rotate90();

        rotated.Width.ShouldBe(2);
        rotated.Height.ShouldBe(3);
        PixelAt(rotated, 0, 0).ShouldBe(PixelAt(source, 0, 1));   // src bottom-left -> dst top-left
        PixelAt(rotated, 1, 0).ShouldBe(PixelAt(source, 0, 0));   // src top-left -> dst top-right
    }

    [Theory]
    [InlineData(Resampler.Nearest)]
    [InlineData(Resampler.Box)]
    [InlineData(Resampler.Bilinear)]
    [InlineData(Resampler.Bicubic)]
    [InlineData(Resampler.Lanczos3)]
    public void Resize_KeepsAConstantImageConstant(Resampler resampler)
    {
        using var source = Image.Create(9, 7, WorkingFormat.Rgba8);
        source.WritablePixels.Fill(0);
        for (var i = 0; i < source.WritablePixels.Length; i += 4)
        {
            source.WritablePixels[i] = 77;
            source.WritablePixels[i + 1] = 88;
            source.WritablePixels[i + 2] = 99;
            source.WritablePixels[i + 3] = 255;
        }

        using var resized = source.Resize(5, 3, resampler);

        var span = resized.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            ((int)span[i]).ShouldBeInRange(76, 78);
            ((int)span[i + 1]).ShouldBeInRange(87, 89);
            ((int)span[i + 2]).ShouldBeInRange(98, 100);
            span[i + 3].ShouldBe((byte)255);
        }
    }

    [Fact]
    public void Resize_DoesNotBleedTransparentColour()
    {
        using var source = Image.Create(8, 8, WorkingFormat.Rgba8);
        source.WritablePixels.Clear();
        for (var y = 3; y < 5; y++)
        {
            for (var x = 3; x < 5; x++)
            {
                var i = (y * 8 + x) * 4;
                source.WritablePixels[i] = 255;      // opaque red centre on a transparent canvas
                source.WritablePixels[i + 3] = 255;
            }
        }

        using var resized = source.Resize(4, 4, Resampler.Box);

        var corner = PixelAt(resized, 0, 0);
        corner[3].ShouldBe((byte)0);
        corner[0].ShouldBe((byte)0);
        corner[1].ShouldBe((byte)0);
        corner[2].ShouldBe((byte)0);
    }

    [Fact]
    public void Composite_SourceOver()
    {
        using var background = Image.Create(1, 1, WorkingFormat.Rgba8);
        background.WritablePixels[..4].Clear();
        background.WritablePixels[2] = 255;          // opaque blue
        background.WritablePixels[3] = 255;

        using var overlay = Image.Create(1, 1, WorkingFormat.Rgba8);
        overlay.WritablePixels[0] = 255;             // 50% red
        overlay.WritablePixels[3] = 128;

        using var composed = background.Composite(overlay, 0, 0);
        var pixel = PixelAt(composed, 0, 0);

        ((int)pixel[0]).ShouldBeInRange(127, 129);
        pixel[1].ShouldBe((byte)0);
        ((int)pixel[2]).ShouldBeInRange(126, 128);
        pixel[3].ShouldBe((byte)255);
    }

    [Fact]
    public void PremultiplyThenUnpremultiply_WithinOne()
    {
        using var source = Pattern(16, 16);
        var span = source.WritablePixels;
        for (var i = 3; i < span.Length; i += 4)
            span[i] = (byte)(128 + i * 7 % 128);

        using var roundTripped = source.Premultiply().Unpremultiply();

        var before = source.WritablePixels;
        var after = roundTripped.WritablePixels;
        for (var i = 0; i < before.Length; i++)
            Math.Abs(after[i] - before[i]).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Pad_FillsTheBorderAndKeepsTheCentre()
    {
        using var source = Pattern(2, 2);
        using var padded = source.Pad(1, RgbaColor.Black);

        padded.Width.ShouldBe(4);
        padded.Height.ShouldBe(4);
        PixelAt(padded, 0, 0).ShouldBe(new byte[] { 0, 0, 0, 255 });
        PixelAt(padded, 1, 1).ShouldBe(PixelAt(source, 0, 0));
        PixelAt(padded, 2, 2).ShouldBe(PixelAt(source, 1, 1));
    }

    [Fact]
    public void Composite_RefusesAnOverlayThatDoesNotFit()
    {
        using var background = Pattern(4, 4);
        using var overlay = Pattern(3, 3);

        Should.Throw<ArgumentException>(() => background.Composite(overlay, 2, 2));
    }
}
