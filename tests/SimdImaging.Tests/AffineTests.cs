using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

public class AffineTests
{
    private static Image Pattern(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                span[o] = (byte)(x * 13 + 7);
                span[o + 1] = (byte)(y * 29 + 5);
                span[o + 2] = (byte)(x + y);
                span[o + 3] = 255;
            }
        }

        return image;
    }

    private static byte[] At(Image image, int x, int y)
    {
        var o = (y * image.Width + x) * 4;
        return image.WritablePixels.Slice(o, 4).ToArray();
    }

    [Fact]
    public void Rotate_ZeroDegreesIsIdentity()
    {
        using var source = Pattern(7, 5);
        using var rotated = source.Rotate(0f);

        rotated.Width.ShouldBe(7);
        rotated.Height.ShouldBe(5);

        var a = source.WritablePixels;
        var b = rotated.WritablePixels;
        for (var i = 0; i < a.Length; i++)
            Math.Abs(a[i] - b[i]).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Rotate_NinetyDegreesSwapsTheCanvas()
    {
        using var source = Pattern(9, 5);
        using var rotated = source.Rotate(90f);

        rotated.Width.ShouldBe(5);
        rotated.Height.ShouldBe(9);

        // Rotating clockwise about the centre sends the source top-left corner to the destination top-right.
        At(rotated, 4, 0).ShouldBe(At(source, 0, 0));
    }

    [Fact]
    public void Rotate_FortyFiveDegreesFillsTheCornersWithTheBackground()
    {
        using var source = Pattern(11, 11);
        using var rotated = source.Rotate(45f, new RgbaColor(0, 0, 0, 0));

        rotated.Width.ShouldBeGreaterThan(11);
        At(rotated, 0, 0).ShouldBe(new byte[] { 0, 0, 0, 0 });
    }

    [Fact]
    public void Affine_IdentityIsACopy()
    {
        using var source = Pattern(6, 4);
        using var copied = source.Affine(AffineMatrix.Identity, 6, 4);

        copied.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Affine_ScaleDoublesTheCanvas()
    {
        using var source = Pattern(4, 3);
        using var scaled = source.Affine(AffineMatrix.Scale(2f, 2f), 8, 6);

        scaled.Width.ShouldBe(8);
        scaled.Height.ShouldBe(6);
        scaled.WritablePixels[0].ShouldBe(source.WritablePixels[0]);
    }

    [Fact]
    public void Affine_RejectsASingularMatrix()
    {
        using var source = Pattern(3, 3);
        var singular = new AffineMatrix(1f, 2f, 0f, 2f, 4f, 0f);

        Should.Throw<ArgumentException>(() => source.Affine(singular, 3, 3));
    }

    [Fact]
    public void Affine_MatricesComposeRightToLeft()
    {
        var rotateThenTranslate = AffineMatrix.Translation(10f, 0f).Multiply(AffineMatrix.Rotation(90f));
        var (x, y) = rotateThenTranslate.Apply(1f, 0f);

        // Rotating (1,0) by 90° in a y-down system gives (0,1); the translation then adds 10 to x.
        x.ShouldBe(10f, 1e-4f);
        y.ShouldBe(1f, 1e-4f);
    }

    [Fact]
    public void Clamp_LimitsTheColourRange()
    {
        using var source = Image.Create(1, 1, WorkingFormat.Rgba8);
        source.WritablePixels[0] = 10;
        source.WritablePixels[1] = 200;
        source.WritablePixels[2] = 128;
        source.WritablePixels[3] = 255;

        using var clamped = source.Clamp(0.25f, 0.75f);

        clamped.WritablePixels[0].ShouldBe((byte)64);
        clamped.WritablePixels[1].ShouldBe((byte)191);
        clamped.WritablePixels[2].ShouldBe((byte)128);
        clamped.WritablePixels[3].ShouldBe((byte)255);
    }

    [Fact]
    public void Clamp_RejectsAnInvertedRange()
    {
        using var source = Pattern(2, 2);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Clamp(0.8f, 0.2f));
    }

    [Fact]
    public void MinMax_ReportsPerChannelRanges()
    {
        var image = Image.Create(2, 1, WorkingFormat.Rgba8);
        using (image)
        {
            image.WritablePixels[0] = 100;
            image.WritablePixels[4] = 150;
            image.WritablePixels[1] = 20;
            image.WritablePixels[5] = 60;
            image.WritablePixels[2] = 200;
            image.WritablePixels[6] = 200;
            image.WritablePixels[3] = 255;
            image.WritablePixels[7] = 255;

            var (r, g, b) = image.MinMax();

            MathF.Round(r.Min * 255).ShouldBe(100f);
            MathF.Round(r.Max * 255).ShouldBe(150f);
            MathF.Round(g.Min * 255).ShouldBe(20f);
            MathF.Round(g.Max * 255).ShouldBe(60f);
            b.Span.ShouldBe(0f);
        }
    }
}
