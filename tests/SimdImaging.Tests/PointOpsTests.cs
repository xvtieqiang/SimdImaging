namespace SimdImaging.Tests;

public class PointOpsTests
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
    public void Invert_TwiceIsIdentity()
    {
        using var source = Solid(10, 120, 250);
        using var twice = source.Invert().Invert();
        Pixels(twice).ShouldBe(Pixels(source));
    }

    [Fact]
    public void Invert_LeavesAlphaAlone()
    {
        using var source = Solid(10, 120, 250, a: 77);
        using var inverted = source.Invert();
        Pixels(inverted)[3].ShouldBe((byte)77);
        Pixels(inverted)[0].ShouldBe((byte)245);
    }

    [Fact]
    public void Grayscale_UsesBt601Luma()
    {
        using var source = Solid(255, 0, 0);
        using var gray = source.Grayscale();

        // 0.299 * 255 = 76.245
        Pixels(gray).ShouldBe(new byte[] { 76, 76, 76, 255 });
    }

    [Fact]
    public void Grayscale_Bt709WeightsBlueDifferently()
    {
        using var source = Solid(0, 0, 255);
        using var gray = source.Grayscale(bt709: true);

        // 0.0722 * 255 = 18.4
        Pixels(gray).ShouldBe(new byte[] { 18, 18, 18, 255 });
    }

    [Fact]
    public void Brightness_AddsInSampleUnits()
    {
        using var source = Solid(100, 100, 100);
        using var brighter = source.Brightness(0.5f);
        Pixels(brighter)[0].ShouldBe((byte)228);   // 100 + 127.5, rounded
    }

    [Fact]
    public void Brightness_ClampsInsteadOfWrapping()
    {
        using var source = Solid(200, 10, 10);
        using var brighter = source.Brightness(0.5f);

        Pixels(brighter)[0].ShouldBe((byte)255);
        Pixels(brighter)[1].ShouldBe((byte)138);   // 10 + 127.5
    }

    [Fact]
    public void Contrast_OneIsIdentity()
    {
        using var source = Solid(37, 128, 210);
        using var same = source.Contrast(1f);
        Pixels(same).ShouldBe(Pixels(source));
    }

    [Fact]
    public void Saturation_ZeroEqualsGrayscale()
    {
        using var source = Solid(200, 40, 90);
        using var desaturated = source.Saturation(0f);
        using var gray = source.Grayscale();
        Pixels(desaturated).ShouldBe(Pixels(gray));
    }

    [Fact]
    public void Gamma_OneIsIdentity()
    {
        using var source = Solid(37, 128, 210);
        using var same = source.Gamma(1f);
        Pixels(same).ShouldBe(Pixels(source));
    }

    [Fact]
    public void Gamma_RejectsNonPositive()
    {
        using var source = Solid(1, 2, 3);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Gamma(0f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Gamma(float.NaN));
    }

    [Fact]
    public void PointOps_PreserveAlpha()
    {
        using var source = Solid(10, 20, 30, a: 41);

        using var a = source.Invert();
        using var b = source.Brightness(0.1f);
        using var c = source.Contrast(1.7f);
        using var d = source.Saturation(0.3f);
        using var e = source.Grayscale();
        using var f = source.Gamma(2.2f);

        foreach (var image in new[] { a, b, c, d, e, f })
            Pixels(image)[3].ShouldBe((byte)41);
    }

    [Fact]
    public void PointOps_WorkOnSixteenBit()
    {
        using var source = Image.Create(1, 1, WorkingFormat.Rgba16);
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(source.WritablePixels);
        span[0] = 65535;
        span[1] = 0;
        span[2] = 0;
        span[3] = 65535;

        using var gray = source.Grayscale();
        var result = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(gray.WritablePixels);

        // 0.299 * 65535 = 19595.0
        ((int)result[0]).ShouldBeInRange(19594, 19596);
        result[1].ShouldBe(result[0]);
        result[3].ShouldBe((ushort)65535);
    }

    [Fact]
    public void Posterize_QuantizesToEvenSteps()
    {
        using var source = Solid(0, 128, 255);
        using var posterized = source.Posterize(4);

        // 0 -> 0, 128/255 = 0.502 -> round(1.506)/3 = 0.667 -> 170, 255 -> 255
        PixelAt(posterized)[0].ShouldBe((byte)0);
        PixelAt(posterized)[1].ShouldBe((byte)170);
        PixelAt(posterized)[2].ShouldBe((byte)255);
    }

    [Fact]
    public void Posterize_RejectsFewerThanTwoLevels()
    {
        using var source = Solid(1, 2, 3);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Posterize(1));
    }

    [Fact]
    public void Threshold_SplitsByLumaAndKeepsAlpha()
    {
        using var source = Solid(100, 100, 100, a: 33);
        using var dark = source.Threshold(0.5f);

        PixelAt(dark).ShouldBe(new byte[] { 0, 0, 0, 33 });

        using var bright = Solid(200, 200, 200, a: 44).Threshold(0.5f);
        PixelAt(bright).ShouldBe(new byte[] { 255, 255, 255, 44 });
    }

    [Fact]
    public void Threshold_RejectsOutOfRange()
    {
        using var source = Solid(1, 2, 3);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Threshold(1.5f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Threshold(-0.1f));
    }

    private static byte[] PixelAt(Image image)
    {
        return image.WritablePixels[..4].ToArray();
    }
}
