namespace SimdImaging.Tests;

public class ColorToneTests
{
    private static Image Solid(byte r, byte g, byte b, byte a = 255)
    {
        var image = Image.Create(1, 1, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        span[0] = r;
        span[1] = g;
        span[2] = b;
        span[3] = a;
        return image;
    }

    private static byte[] Pixels(Image image) => image.WritablePixels.ToArray();

    [Fact]
    public void Vibrance_ZeroIsIdentity()
    {
        using var source = Solid(120, 90, 60);
        using var result = source.Vibrance(0f);
        Pixels(result).ShouldBe(Pixels(source));
    }

    [Fact]
    public void Vibrance_MovesMutedColoursMoreThanSaturatedOnes()
    {
        using var muted = Solid(130, 120, 110);
        using var saturated = Solid(255, 0, 0);

        using var mutedResult = muted.Vibrance(0.5f);
        using var saturatedResult = saturated.Vibrance(0.5f);

        var mutedDelta = Math.Abs(mutedResult.WritablePixels[0] - muted.WritablePixels[0]);
        var saturatedDelta = Math.Abs(saturatedResult.WritablePixels[1] - saturated.WritablePixels[1]);

        mutedDelta.ShouldBeGreaterThan(saturatedDelta);
    }

    [Fact]
    public void Temperature_WarmsAndCoolsPredictably()
    {
        using var source = Solid(128, 128, 128);

        using var warm = source.Temperature(1f);
        warm.WritablePixels[0].ShouldBeGreaterThan((byte)128);
        warm.WritablePixels[2].ShouldBeLessThan((byte)128);

        using var cool = source.Temperature(-1f);
        cool.WritablePixels[0].ShouldBeLessThan((byte)128);
        cool.WritablePixels[2].ShouldBeGreaterThan((byte)128);
    }

    [Fact]
    public void Tint_BlendsTowardsTheColourAndLeavesAlpha()
    {
        using var source = Solid(100, 100, 100, a: 60);

        using var none = source.Tint(new RgbaColor(255, 0, 0, 255), 0f);
        Pixels(none).ShouldBe(Pixels(source));

        using var half = source.Tint(new RgbaColor(255, 0, 0, 255), 0.5f);
        var p = Pixels(half);
        ((int)p[0]).ShouldBeInRange(176, 179);
        ((int)p[1]).ShouldBeInRange(49, 51);
        p[3].ShouldBe((byte)60);
    }

    [Fact]
    public void Sharpen_ZeroIsIdentity()
    {
        using var source = Image.Create(5, 5, WorkingFormat.Rgba8);
        source.WritablePixels.Fill(80);

        using var result = source.Sharpen(0f);
        var after = Pixels(result);
        for (var i = 0; i < after.Length; i++)
            Math.Abs(after[i] - source.WritablePixels[i]).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Sharpen_OvershootsAtAnEdge()
    {
        using var source = Image.Create(9, 9, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < 9; y++)
        {
            for (var x = 0; x < 9; x++)
            {
                var o = (y * 9 + x) * 4;
                var value = (byte)(x < 4 ? 100 : 150);
                span[o] = value;
                span[o + 1] = value;
                span[o + 2] = value;
                span[o + 3] = 255;
            }
        }

        using var sharpened = source.Sharpen(1f, 1f);

        var dark = sharpened.WritablePixels[(4 * 9 + 3) * 4];
        var bright = sharpened.WritablePixels[(4 * 9 + 5) * 4];
        dark.ShouldBeLessThan((byte)100);
        bright.ShouldBeGreaterThan((byte)150);
    }

    [Fact]
    public void Sharpen_PreservesAlpha()
    {
        using var source = Image.Create(5, 5, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = (byte)(i * 3);
            span[i + 3] = 77;
        }

        using var result = source.Sharpen(0.8f);
        var after = result.WritablePixels;
        for (var i = 3; i < after.Length; i += 4)
            after[i].ShouldBe((byte)77);
    }
}
