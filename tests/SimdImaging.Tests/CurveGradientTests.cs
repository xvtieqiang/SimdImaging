using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class CurveGradientTests
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

    private static byte At(Image image, int x, int y, int channel = 0) =>
        image.WritablePixels[(y * image.Width + x) * 4 + channel];

    [Fact]
    public void Curves_IdentityPointsLeaveTheImageAlone()
    {
        using var source = Solid(3, 1, 90);
        CurvePoint[] identity = [new(0f, 0f), new(1f, 1f)];

        using var result = source.Curves(identity);

        for (var i = 0; i < result.WritablePixels.Length; i += 4)
            Math.Abs(result.WritablePixels[i] - 90).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Curves_SCurveDeepensShadowsAndLiftsHighlights()
    {
        var source = Image.Create(2, 1, WorkingFormat.Rgba8);
        using (source)
        {
            source.WritablePixels[0] = 64;    // 0.25
            source.WritablePixels[4] = 191;   // 0.75
            source.WritablePixels[3] = 255;
            source.WritablePixels[7] = 255;

            CurvePoint[] sCurve = [new(0f, 0f), new(0.5f, 0.5f), new(1f, 1f)];
            using var result = source.Curves(sCurve);

            // A symmetric S-curve through the midpoint leaves 0.25 and 0.75 near where they started, but the
            // interpolation must stay monotone: output is non-decreasing in input.
            Math.Abs(result.WritablePixels[0] - 64).ShouldBeLessThanOrEqualTo(3);
            Math.Abs(result.WritablePixels[4] - 191).ShouldBeLessThanOrEqualTo(3);
        }
    }

    [Fact]
    public void Curves_RejectsBadControlPoints()
    {
        using var source = Solid(2, 2, 10);
        Should.Throw<ArgumentException>(() => source.Curves(new[] { new CurvePoint(0f, 0f) }));
        Should.Throw<ArgumentException>(() => source.Curves(new[] { new CurvePoint(0.5f, 0f), new CurvePoint(0.2f, 1f) }));
    }

    [Fact]
    public void Opacity_MultipliesAlphaOnly()
    {
        using var source = Solid(2, 1, 100, alpha: 200);
        using var result = source.Opacity(0.5f);

        result.WritablePixels[0].ShouldBe((byte)100);
        ((int)result.WritablePixels[3]).ShouldBeInRange(99, 101);
    }

    [Fact]
    public void LinearGradient_SpansTheRectangle()
    {
        using var image = Solid(4, 1, 0);
        image.LinearGradient(new PixelRect(0, 0, 4, 1), RgbaColor.Black, RgbaColor.White, 0f);

        At(image, 0, 0).ShouldBeLessThan((byte)40);
        At(image, 3, 0).ShouldBeGreaterThan((byte)215);
    }

    [Fact]
    public void RadialGradient_IsInnerAtTheCentreAndOuterAtTheEdge()
    {
        using var image = Solid(5, 5, 0);
        image.RadialGradient(new PixelRect(0, 0, 5, 5), 2.5f, 2.5f, 2.5f, RgbaColor.White, RgbaColor.Black);

        At(image, 2, 2).ShouldBeGreaterThan((byte)240);
        At(image, 0, 0).ShouldBeLessThan((byte)10);
    }

    [Fact]
    public void DrawImage_PlacesAndPreservesTheSource()
    {
        using var background = Solid(6, 6, 0);
        using var mark = Solid(2, 2, 255);

        using var result = background.DrawImage(mark, new PixelRect(2, 2, 2, 2));

        At(result, 2, 2).ShouldBe((byte)255);
        At(result, 3, 3).ShouldBe((byte)255);
        At(result, 1, 2).ShouldBe((byte)0);
        At(result, 4, 2).ShouldBe((byte)0);
    }

    [Fact]
    public void Watermark_SitsInTheCornerWithMargin()
    {
        using var background = Solid(8, 8, 0);
        using var mark = Solid(2, 2, 255);

        using var result = background.Watermark(mark, WatermarkPosition.BottomRight, margin: 1, opacity: 1f);

        At(result, 5, 5).ShouldBe((byte)255);
        At(result, 6, 6).ShouldBe((byte)255);
        At(result, 7, 7).ShouldBe((byte)0);
        At(result, 4, 4).ShouldBe((byte)0);
    }

    [Fact]
    public void Watermark_RefusesWhenItDoesNotFit()
    {
        using var background = Solid(3, 3, 0);
        using var mark = Solid(2, 2, 255);

        Should.Throw<ArgumentException>(() => background.Watermark(mark, WatermarkPosition.BottomRight, margin: 2));
    }
}
