using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class ColorSmallTests
{
    private static Image Solid(int width, int height, RgbaColor colour)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        image.Fill(colour);
        return image;
    }

    private static byte Red(Image image, int x, int y) => image.WritablePixels[(y * image.Width + x) * 4];

    [Fact]
    public void Linearize_SrgbRoundTrips()
    {
        // In float storage: an 8-bit linear buffer crushes the sRGB toe (code 10 lands on linear code 1),
        // so an 8-bit round trip is lossy by construction, not by defect.
        using var source = Image.Create(256, 1, WorkingFormat.Rgba32F);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source.WritablePixels);
        for (var x = 0; x < 256; x++)
        {
            var o = x * 4;
            samples[o] = samples[o + 1] = samples[o + 2] = x / 255f;
            samples[o + 3] = 1f;
        }

        using var linear = source.Linearize(TransferCurve.Srgb);
        using var back = linear.Delinearize(TransferCurve.Srgb);
        var result = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(back.WritablePixels);

        for (var x = 0; x < 256; x++)
            result[x * 4].ShouldBe(x / 255f, 1e-3f, $"code {x}");
    }

    [Fact]
    public void Linearize_MapsMidGreyToLinearLight()
    {
        using var source = Solid(2, 2, new RgbaColor(128, 128, 128, 255));
        using var linear = source.Linearize(TransferCurve.Srgb);

        // sRGB 128 decodes to 0.2159, which is 55 in an 8-bit linear buffer.
        Red(linear, 0, 0).ShouldBeInRange((byte)54, (byte)56);
    }

    [Fact]
    public void Linearize_LeavesTheEndsAlone()
    {
        using var source = Image.Create(2, 1, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        span[0] = span[1] = span[2] = 0;
        span[3] = 255;
        span[4] = span[5] = span[6] = 255;
        span[7] = 255;

        using var linear = source.Linearize(TransferCurve.Pq);

        Red(linear, 0, 0).ShouldBe((byte)0);
        Red(linear, 1, 0).ShouldBe((byte)255);
    }

    [Fact]
    public void Linearize_IsMonotonicForEveryCurve()
    {
        using var source = Image.Create(64, 1, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var x = 0; x < 64; x++)
        {
            var o = x * 4;
            span[o] = span[o + 1] = span[o + 2] = (byte)(x * 4);
            span[o + 3] = 255;
        }

        foreach (var curve in Enum.GetValues<TransferCurve>())
        {
            using var linear = source.Linearize(curve);
            var previous = 0;
            for (var x = 0; x < 64; x++)
            {
                var value = Red(linear, x, 0);
                value.ShouldBeGreaterThanOrEqualTo((byte)previous, $"{curve} at {x}");
                previous = value;
            }
        }
    }

    [Fact]
    public void AdaptiveThreshold_FindsADarkObjectUnderUnevenLight()
    {
        using var source = Image.Create(40, 40, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var o = (y * 40 + x) * 4;
                var value = x < 20 ? (byte)60 : (byte)200;
                if (x is > 24 and < 34 && y is > 14 and < 26)
                    value = 30;
                span[o] = span[o + 1] = span[o + 2] = value;
                span[o + 3] = 255;
            }
        }

        using var binary = source.AdaptiveThreshold(8, 0.05f);

        // Both backgrounds are uniform, so both read as paper; only the dark object flips.
        Red(binary, 10, 20).ShouldBe((byte)255);
        Red(binary, 3, 3).ShouldBe((byte)255);
        Red(binary, 29, 20).ShouldBe((byte)0);
        Red(binary, 36, 36).ShouldBe((byte)255);
    }

    [Fact]
    public void AdaptiveThreshold_KeepsAlpha()
    {
        using var source = Solid(8, 8, new RgbaColor(10, 20, 30, 128));
        using var binary = source.AdaptiveThreshold(2);

        var span = binary.WritablePixels;
        for (var i = 3; i < span.Length; i += 4)
            span[i].ShouldBe((byte)128);
    }

    [Fact]
    public void AdaptiveThreshold_RejectsIllegalParameters()
    {
        using var source = Solid(4, 4, RgbaColor.White);
        Should.Throw<ArgumentOutOfRangeException>(() => source.AdaptiveThreshold(0));
        Should.Throw<ArgumentOutOfRangeException>(() => source.AdaptiveThreshold(2, 1f));
    }

    [Fact]
    public void ReplaceBackground_SwapsOnlyTheBackground()
    {
        using var source = Solid(10, 10, RgbaColor.White);
        source.FillRect(new PixelRect(4, 4, 2, 2), new RgbaColor(200, 40, 40, 255));

        using var replaced = source.ReplaceBackground(RgbaColor.White, new RgbaColor(0, 0, 255, 255), 0.05f, 0.02f);

        var corner = replaced.WritablePixels[0];
        corner.ShouldBe((byte)0);
        replaced.WritablePixels[2].ShouldBe((byte)255);
        Red(replaced, 4, 4).ShouldBe((byte)200);
    }

    [Fact]
    public void ReplaceBackground_FeathersBetweenToleranceAndEdge()
    {
        using var source = Image.Create(3, 1, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var x = 0; x < 3; x++)
        {
            var o = x * 4;
            span[o] = span[o + 1] = span[o + 2] = (byte)(255 - x * 25);
            span[o + 3] = 255;
        }

        using var replaced = source.ReplaceBackground(
            new RgbaColor(255, 255, 255, 255), new RgbaColor(0, 0, 0, 255), 0.05f, 0.2f);

        Red(replaced, 0, 0).ShouldBe((byte)0);
        var middle = Red(replaced, 1, 0);
        middle.ShouldBeGreaterThan((byte)0);
        middle.ShouldBeLessThan((byte)255);
    }

    [Fact]
    public void ColorBlindness_SeverityZeroIsIdentity()
    {
        using var source = Solid(4, 4, new RgbaColor(200, 60, 30, 255));
        using var simulated = source.ColorBlindness(ColorVisionDeficiency.Deuteranopia, 0f);

        simulated.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void ColorBlindness_DarkensRedForAProtanope()
    {
        using var source = Solid(2, 2, new RgbaColor(255, 0, 0, 255));
        using var simulated = source.ColorBlindness(ColorVisionDeficiency.Protanopia, 1f);

        Red(simulated, 0, 0).ShouldBeLessThan((byte)230);
        simulated.WritablePixels[1].ShouldBeGreaterThan((byte)0);
    }

    [Fact]
    public void ColorBlindness_AchromatopsiaProducesGrey()
    {
        using var source = Solid(2, 2, new RgbaColor(200, 60, 30, 255));
        using var simulated = source.ColorBlindness(ColorVisionDeficiency.Achromatopsia, 1f);

        var span = simulated.WritablePixels;
        span[0].ShouldBe(span[1]);
        span[1].ShouldBe(span[2]);
        span[3].ShouldBe((byte)255);
    }

    [Fact]
    public void ColorBlindness_DaltonisingDiffersFromSimulation()
    {
        using var source = Solid(4, 4, new RgbaColor(255, 0, 0, 255));
        using var simulated = source.ColorBlindness(ColorVisionDeficiency.Deuteranopia, 1f);
        using var corrected = source.ColorBlindness(ColorVisionDeficiency.Deuteranopia, 1f, correct: true);

        corrected.WritablePixels.ToArray().ShouldNotBe(simulated.WritablePixels.ToArray());
        corrected.WritablePixels[0].ShouldBe((byte)255);
    }

    [Fact]
    public void ColorBlindness_RejectsIllegalSeverity()
    {
        using var source = Solid(2, 2, RgbaColor.White);
        Should.Throw<ArgumentOutOfRangeException>(() => source.ColorBlindness(severity: -0.1f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.ColorBlindness(severity: 1.5f));
    }

    [Fact]
    public void DominantColors_OrdersByCoverage()
    {
        using var source = Image.Create(8, 8, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var o = (y * 8 + x) * 4;
                var red = x < 6;
                span[o] = (byte)(red ? 220 : 20);
                span[o + 1] = (byte)(red ? 30 : 40);
                span[o + 2] = (byte)(red ? 30 : 200);
                span[o + 3] = 255;
            }
        }

        var colours = source.DominantColors(2);

        colours.Length.ShouldBe(2);
        colours[0].R.ShouldBeGreaterThan((byte)200);
        colours[0].B.ShouldBeLessThan((byte)60);
        colours[1].B.ShouldBeGreaterThan((byte)150);
    }

    [Fact]
    public void DominantColors_RejectsIllegalCount()
    {
        using var source = Solid(4, 4, RgbaColor.White);
        Should.Throw<ArgumentOutOfRangeException>(() => source.DominantColors(0));
        Should.Throw<ArgumentOutOfRangeException>(() => source.DominantColors(257));
    }
}
