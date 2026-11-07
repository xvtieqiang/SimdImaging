using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

public class GeometryExtraTests
{
    private static Image Indexed(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                span[o] = (byte)(y * 16 + x);
                span[o + 1] = (byte)(y * 16 + x);
                span[o + 2] = (byte)(y * 16 + x);
                span[o + 3] = 255;
            }
        }

        return image;
    }

    private static byte Red(Image image, int x, int y) => image.WritablePixels[(y * image.Width + x) * 4];

    [Fact]
    public void Transpose_SwapsTheAxes()
    {
        using var source = Indexed(3, 2);
        using var transposed = source.Transpose();

        transposed.Width.ShouldBe(2);
        transposed.Height.ShouldBe(3);
        Red(transposed, 0, 0).ShouldBe(Red(source, 0, 0));
        Red(transposed, 1, 0).ShouldBe(Red(source, 0, 1));
        Red(transposed, 0, 2).ShouldBe(Red(source, 2, 0));
    }

    [Fact]
    public void Transpose_AntiMirrorsAcrossTheOtherDiagonal()
    {
        using var source = Indexed(3, 2);
        using var transposed = source.Transpose(anti: true);

        transposed.Width.ShouldBe(2);
        transposed.Height.ShouldBe(3);
        // Destination (x, y) reads source (width-1-y, height-1-x).
        Red(transposed, 0, 0).ShouldBe(Red(source, 2, 1));
        Red(transposed, 1, 2).ShouldBe(Red(source, 0, 0));
    }

    [Fact]
    public void RotateFlip_MatchesTheComposedTransform()
    {
        using var source = Indexed(2, 1);

        using var quarter = source.RotateFlip(1);
        quarter.Width.ShouldBe(1);
        quarter.Height.ShouldBe(2);
        Red(quarter, 0, 0).ShouldBe(Red(source, 0, 0));
        Red(quarter, 0, 1).ShouldBe(Red(source, 1, 0));

        using var half = source.RotateFlip(2);
        Red(half, 0, 0).ShouldBe(Red(source, 1, 0));
        Red(half, 1, 0).ShouldBe(Red(source, 0, 0));

        using var flipped = source.RotateFlip(0, flipHorizontal: true);
        Red(flipped, 0, 0).ShouldBe(Red(source, 1, 0));
        Red(flipped, 1, 0).ShouldBe(Red(source, 0, 0));
    }

    [Fact]
    public void RotateFlip_HandlesNegativeAndLargeTurns()
    {
        using var source = Indexed(3, 2);

        using var minusOne = source.RotateFlip(-1);
        using var three = source.RotateFlip(3);
        minusOne.Width.ShouldBe(three.Width);
        minusOne.WritablePixels.ToArray().ShouldBe(three.WritablePixels.ToArray());

        using var four = source.RotateFlip(4);
        four.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void EntropyCrop_RemovesAFlatBorder()
    {
        using var source = Image.Create(20, 20, WorkingFormat.Rgba8);
        var span = source.WritablePixels;

        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 20; x++)
            {
                var o = (y * 20 + x) * 4;
                var inside = x is >= 4 and < 16 && y is >= 4 and < 16;
                var value = inside && (x + y) % 2 == 0 ? (byte)255 : (byte)0;
                span[o] = span[o + 1] = span[o + 2] = inside ? value : (byte)0;
                span[o + 3] = 255;
            }
        }

        using var cropped = source.EntropyCrop(0.25f);

        cropped.Width.ShouldBe(12);
        cropped.Height.ShouldBe(12);
        Red(cropped, 0, 0).ShouldBe((byte)255);   // (4,4) is an even-sum cell
    }

    [Fact]
    public void EntropyCrop_KeepsAUniformImageAtOnePixel()
    {
        using var source = Image.Create(8, 8, WorkingFormat.Rgba8);
        source.Fill(new RgbaColor(40, 40, 40, 255));

        using var cropped = source.EntropyCrop();

        cropped.Width.ShouldBe(1);
        cropped.Height.ShouldBe(1);
    }

    [Fact]
    public void EntropyCrop_RejectsIllegalThreshold()
    {
        using var source = Indexed(4, 4);
        Should.Throw<ArgumentOutOfRangeException>(() => source.EntropyCrop(-0.1f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.EntropyCrop(1.1f));
    }

    [Fact]
    public void SamplePixel_BilinearBlendsBetweenNeighbours()
    {
        using var source = Image.Create(2, 1, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        span[0] = span[1] = span[2] = 0;
        span[3] = 255;
        span[4] = span[5] = span[6] = 255;
        span[7] = 255;

        source.SamplePixel(0.5f, 0.5f).R.ShouldBe((byte)0);
        source.SamplePixel(1.5f, 0.5f).R.ShouldBe((byte)255);
        source.SamplePixel(1f, 0.5f).R.ShouldBeInRange((byte)127, (byte)129);
        source.SamplePixel(1f, 0.5f, Resampler.Nearest).R.ShouldBeOneOf((byte)0, (byte)255);
    }

    [Fact]
    public void SamplePixel_RejectsUnsupportedResamplers()
    {
        using var source = Indexed(4, 4);
        Should.Throw<NotSupportedException>(() => source.SamplePixel(1f, 1f, Resampler.Lanczos3));
    }

    [Fact]
    public void Warp_IdentityMappingReproducesTheSource()
    {
        using var source = Indexed(4, 4);
        using var warped = source.Warp(4, 4, (float x, float y, out float sx, out float sy) =>
        {
            sx = x;
            sy = y;
        });

        warped.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Warp_OutsideTheSourceIsBackground()
    {
        using var source = Indexed(4, 4);
        using var warped = source.Warp(8, 8, (float x, float y, out float sx, out float sy) =>
        {
            sx = x - 4f;
            sy = y - 4f;
        }, background: new RgbaColor(10, 20, 30, 255));

        var span = warped.WritablePixels;
        span[0].ShouldBe((byte)10);       // top-left lands outside
        Red(warped, 7, 7).ShouldBe(Red(source, 3, 3));
    }

    [Fact]
    public void SweepGradient_RunsAroundTheCentre()
    {
        using var image = Image.Create(21, 21, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.SweepGradient(new PixelRect(0, 0, 21, 21), 10.5f, 10.5f, RgbaColor.White, RgbaColor.Black);

        Red(image, 20, 10).ShouldBeGreaterThan((byte)250);   // angle 0 is the start colour
        Red(image, 10, 0).ShouldBeLessThan((byte)80);        // three quarters round is near the end colour
    }

    [Fact]
    public void SweepGradient_ClipsToTheRectangle()
    {
        using var image = Image.Create(10, 10, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.SweepGradient(new PixelRect(20, 20, 5, 5), 5f, 5f, RgbaColor.White, RgbaColor.White);

        Red(image, 0, 0).ShouldBe((byte)0);
    }
}
