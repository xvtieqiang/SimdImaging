using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class ColorSpaceTests
{
    private static void ShouldBeClose(float actual, float expected, float tolerance, string what) =>
        MathF.Abs(actual - expected).ShouldBeLessThanOrEqualTo(tolerance, what);

    [Fact]
    public void White_MapsToLabLightnessOneHundred()
    {
        var (l, a, b) = ColorMath.ToLab(1f, 1f, 1f);

        ShouldBeClose(l, 100f, 0.01f, "L");
        ShouldBeClose(a, 0f, 0.02f, "a");
        ShouldBeClose(b, 0f, 0.02f, "b");
    }

    [Fact]
    public void PureRed_MatchesThePublishedLabValue()
    {
        var (l, a, b) = ColorMath.ToLab(1f, 0f, 0f);

        // D65 / 2° observed: L 53.2408, a 80.0925, b 67.2032.
        ShouldBeClose(l, 53.2408f, 0.05f, "L");
        ShouldBeClose(a, 80.0925f, 0.05f, "a");
        ShouldBeClose(b, 67.2032f, 0.05f, "b");
    }

    [Theory]
    [InlineData(0.1f, 0.2f, 0.3f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0.5f, 0.5f, 0.5f)]
    [InlineData(0.02f, 0.9f, 0.4f)]
    public void XyzRoundTrips(float r, float g, float b)
    {
        var (x, y, z) = ColorMath.ToXyz(r, g, b);
        var (rr, gg, bb) = ColorMath.FromXyz(x, y, z);

        ShouldBeClose(rr, r, 1e-3f, "R");
        ShouldBeClose(gg, g, 1e-3f, "G");
        ShouldBeClose(bb, b, 1e-3f, "B");
    }

    [Theory]
    [InlineData(0.1f, 0.2f, 0.3f)]
    [InlineData(0.8f, 0.7f, 0.6f)]
    [InlineData(0.5f, 0.5f, 0.5f)]
    public void LabRoundTrips(float r, float g, float b)
    {
        var (l, a, other) = ColorMath.ToLab(r, g, b);
        var (rr, gg, bb) = ColorMath.FromLab(l, a, other);

        ShouldBeClose(rr, r, 2e-3f, "R");
        ShouldBeClose(gg, g, 2e-3f, "G");
        ShouldBeClose(bb, b, 2e-3f, "B");
    }

    [Fact]
    public void LchRoundTripsThroughLab()
    {
        var lab = ColorMath.ToLab(0.2f, 0.6f, 0.9f);
        var (l, c, h) = ColorMath.ToLch(lab.L, lab.A, lab.B);
        var back = ColorMath.FromLch(l, c, h);

        ShouldBeClose(back.L, lab.L, 1e-4f, "L");
        ShouldBeClose(back.A, lab.A, 1e-4f, "a");
        ShouldBeClose(back.B, lab.B, 1e-4f, "b");
    }

    [Fact]
    public void HslAndHsvAgreeOnThePrimaries()
    {
        var hsl = ColorMath.ToHsl(1f, 0f, 0f);
        ShouldBeClose(hsl.H, 0f, 1e-3f, "H");
        ShouldBeClose(hsl.S, 1f, 1e-4f, "S");
        ShouldBeClose(hsl.L, 0.5f, 1e-4f, "L");

        var hsv = ColorMath.ToHsv(1f, 0f, 0f);
        ShouldBeClose(hsv.H, 0f, 1e-3f, "H");
        ShouldBeClose(hsv.S, 1f, 1e-4f, "S");
        ShouldBeClose(hsv.V, 1f, 1e-4f, "V");

        var green = ColorMath.ToHsv(0f, 1f, 0f);
        ShouldBeClose(green.H, 120f, 1e-3f, "green hue");
    }

    [Fact]
    public void HslAndHsvRoundTrip()
    {
        foreach (var (r, g, b) in new[] { (0.2f, 0.4f, 0.6f), (0.9f, 0.1f, 0.5f), (0.3f, 0.3f, 0.3f) })
        {
            var hsl = ColorMath.ToHsl(r, g, b);
            var fromHsl = ColorMath.FromHsl(hsl.H, hsl.S, hsl.L);
            ShouldBeClose(fromHsl.R, r, 1e-3f, "HSL R");
            ShouldBeClose(fromHsl.G, g, 1e-3f, "HSL G");
            ShouldBeClose(fromHsl.B, b, 1e-3f, "HSL B");

            var hsv = ColorMath.ToHsv(r, g, b);
            var fromHsv = ColorMath.FromHsv(hsv.H, hsv.S, hsv.V);
            ShouldBeClose(fromHsv.R, r, 1e-3f, "HSV R");
            ShouldBeClose(fromHsv.G, g, 1e-3f, "HSV G");
            ShouldBeClose(fromHsv.B, b, 1e-3f, "HSV B");
        }
    }

    [Theory]
    [InlineData(YCbCrStandard.Bt601, false)]
    [InlineData(YCbCrStandard.Bt709, false)]
    [InlineData(YCbCrStandard.Bt2020, false)]
    [InlineData(YCbCrStandard.Bt601, true)]
    [InlineData(YCbCrStandard.Bt709, true)]
    public void YCbCrRoundTrips(YCbCrStandard standard, bool limited)
    {
        var (y, cb, cr) = ColorMath.ToYCbCr(0.3f, 0.6f, 0.2f, standard, limited);
        var (r, g, b) = ColorMath.FromYCbCr(y, cb, cr, standard, limited);

        ShouldBeClose(r, 0.3f, 1e-3f, "R");
        ShouldBeClose(g, 0.6f, 1e-3f, "G");
        ShouldBeClose(b, 0.2f, 1e-3f, "B");
    }

    [Fact]
    public void AdaptationMapsWhiteToWhite()
    {
        var (wx, wy, wz) = ColorMath.WhiteXyz(WhitePoint.D65);
        var (x, y, z) = ColorMath.Adapt(wx, wy, wz, WhitePoint.D65, WhitePoint.D50);

        var (dx, dy, dz) = ColorMath.WhiteXyz(WhitePoint.D50);
        ShouldBeClose(x, dx, 2e-3f, "X");
        ShouldBeClose(y, dy, 2e-3f, "Y");
        ShouldBeClose(z, dz, 2e-3f, "Z");
    }

    [Fact]
    public void Convert_ProducesAFloatImageAndKeepsAlpha()
    {
        using var source = Image.Create(4, 3, WorkingFormat.Rgba8);
        source.Fill(new RgbaColor(200, 100, 50, 128));

        using var lab = source.ConvertColorSpace(ColorSpace.Lab);

        lab.Format.ShouldBe(WorkingFormat.Rgba32F);
        lab.Width.ShouldBe(4);
        lab.Height.ShouldBe(3);

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(lab.WritablePixels);
        samples[3].ShouldBe(128f / 255f, 1e-6f);
        samples[0].ShouldBeGreaterThan(50f);   // L of a mid-bright colour
        samples[0].ShouldBeLessThan(90f);
    }

    [Fact]
    public void Convert_WhiteUnderD50StaysNeutral()
    {
        using var source = Image.Create(2, 2, WorkingFormat.Rgba8);
        source.Fill(RgbaColor.White);

        using var lab = source.ConvertColorSpace(ColorSpace.Lab, white: WhitePoint.D50);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(lab.WritablePixels);

        samples[0].ShouldBe(100f, 0.05f);
        MathF.Abs(samples[1]).ShouldBeLessThan(0.2f);
        MathF.Abs(samples[2]).ShouldBeLessThan(0.2f);
    }

    [Fact]
    public void Convert_YcbcrKeepsTheExpectedLuma()
    {
        using var source = Image.Create(2, 2, WorkingFormat.Rgba8);
        source.Fill(new RgbaColor(255, 0, 0, 255));

        using var ycbcr = source.ConvertColorSpace(ColorSpace.YCbCr, YCbCrStandard.Bt709);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(ycbcr.WritablePixels);

        samples[0].ShouldBe(0.2126f, 1e-3f);   // BT.709 luma of pure red
        samples[1].ShouldBe(0.385f, 1e-2f);    // chroma sits where the red axis puts it, not at neutral
        samples[2].ShouldBeGreaterThan(0.9f);
    }

    [Fact]
    public void ChromaResample_444IsACopy()
    {
        using var source = Image.Create(8, 6, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)(i * 7);

        using var result = source.ChromaResample(ChromaSubsampling.Yuv444);

        result.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void ChromaResample_BandSizeDoesNotChangeTheResult()
    {
        using var source = Image.Create(10, 21, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < 21; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                var o = (y * 10 + x) * 4;
                span[o] = (byte)(x * 25);
                span[o + 1] = (byte)(y * 12);
                span[o + 2] = (byte)((x * 7 + y * 3) % 256);
                span[o + 3] = 255;
            }
        }

        using var narrow = source.ChromaResample(ChromaSubsampling.Yuv420, YCbCrStandard.Bt709, bandRows: 3);
        using var wide = source.ChromaResample(ChromaSubsampling.Yuv420, YCbCrStandard.Bt709, bandRows: 1024);

        narrow.WritablePixels.ToArray().ShouldBe(wide.WritablePixels.ToArray());

        using var narrow422 = source.ChromaResample(ChromaSubsampling.Yuv422, YCbCrStandard.Bt601, bandRows: 2);
        using var wide422 = source.ChromaResample(ChromaSubsampling.Yuv422, YCbCrStandard.Bt601, bandRows: 1024);

        narrow422.WritablePixels.ToArray().ShouldBe(wide422.WritablePixels.ToArray());
    }

    [Fact]
    public void ChromaResample_420BleedsColourBetweenNeighbours()
    {
        using var source = Image.Create(8, 8, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var o = (y * 8 + x) * 4;
                var red = x % 2 == 0;
                span[o] = (byte)(red ? 255 : 0);
                span[o + 1] = 0;
                span[o + 2] = (byte)(red ? 0 : 255);
                span[o + 3] = 255;
            }
        }

        using var subsampled = source.ChromaResample(ChromaSubsampling.Yuv420, YCbCrStandard.Bt709);

        // Luma is preserved per pixel; the chroma cell averages red and blue, so both columns become the same
        // purple — R falls, B appears where there was none.
        var pixel = subsampled.WritablePixels;
        pixel[0].ShouldBeInRange((byte)120, (byte)190);
        pixel[2].ShouldBeGreaterThan((byte)100);
        pixel[3].ShouldBe((byte)255);
    }
}
