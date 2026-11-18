using System.Runtime.InteropServices;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class HdrTests
{
    private static Image Float(int width, int height, float r, float g, float b)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba32F);
        var samples = MemoryMarshal.Cast<byte, float>(image.WritablePixels);
        for (var i = 0; i < samples.Length; i += 4)
        {
            samples[i] = r;
            samples[i + 1] = g;
            samples[i + 2] = b;
            samples[i + 3] = 1f;
        }

        return image;
    }

    private static float First(Image image) => MemoryMarshal.Cast<byte, float>(image.WritablePixels)[0];

    [Fact]
    public void ToneMap_AcesCompressesOneToPointEight()
    {
        using var source = Float(2, 2, 1f, 1f, 1f);
        using var mapped = source.Pipe().ToneMap(ToneMapCurve.Aces).ToImage();

        First(mapped).ShouldBe(0.8038f, 0.002f);
    }

    [Fact]
    public void ToneMap_ReinhardHalvesOne()
    {
        using var source = Float(2, 2, 1f, 1f, 1f);
        using var mapped = source.Pipe().ToneMap(ToneMapCurve.Reinhard).ToImage();

        First(mapped).ShouldBe(0.5f, 0.001f);
    }

    [Fact]
    public void ToneMap_NoneClampsAboveOne()
    {
        using var source = Float(2, 2, 4f, 0.25f, 0f);
        using var mapped = source.Pipe().ToneMap(ToneMapCurve.None).ToImage();

        var samples = MemoryMarshal.Cast<byte, float>(mapped.WritablePixels);
        samples[0].ShouldBe(1f);
        samples[1].ShouldBe(0.25f, 1e-6f);
        samples[2].ShouldBe(0f);
    }

    [Fact]
    public void ToneMap_ExposureIsInStops()
    {
        using var source = Float(2, 2, 0.25f, 0.25f, 0.25f);
        using var mapped = source.Pipe().ToneMap(ToneMapCurve.None, exposure: 1f).ToImage();

        First(mapped).ShouldBe(0.5f, 1e-5f);
    }

    [Fact]
    public void ToneMap_AutoExposureLiftsADarkImage()
    {
        using var source = Float(4, 4, 0.01f, 0.01f, 0.01f);
        using var mapped = source.Pipe().ToneMap(ToneMapCurve.None, autoExposure: true, targetLuminance: 0.18f).ToImage();

        First(mapped).ShouldBe(0.18f, 0.01f);
    }

    [Fact]
    public void ToneMap_Bt2390MapsTheScenePeakToTheDisplayPeak()
    {
        // Linear 1.0 is 10 000 cd/m² in PQ units; a 100 cd/m² display is linear 0.01.
        using var source = Float(2, 2, 1f, 1f, 1f);
        using var mapped = source.Pipe().ToneMap(ToneMapCurve.Bt2390, whitePoint: 0.01f).ToImage();

        var value = First(mapped);
        value.ShouldBe(0.01f, 0.002f);
    }

    [Fact]
    public void ToneMap_Bt2390WithThePqCeilingDoesNotDivideByZero()
    {
        using var source = Float(2, 2, 10f, 10f, 10f);
        using var mapped = source.Pipe().ToneMap(ToneMapCurve.Bt2390, whitePoint: 1f).ToImage();

        First(mapped).ShouldBe(1f);
    }

    [Fact]
    public void ToneMap_RejectsIllegalParameters()
    {
        using var source = Float(2, 2, 1f, 1f, 1f);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Pipe().ToneMap(whitePoint: 0f).ToImage());
        Should.Throw<ArgumentOutOfRangeException>(() => source.Pipe().ToneMap(targetLuminance: 0f).ToImage());
        Should.Throw<ArgumentOutOfRangeException>(() => source.Pipe().ToneMap(exposure: float.PositiveInfinity).ToImage());
    }

    [Fact]
    public void GainMap_MultipliesLinearLight()
    {
        using var baseImage = Image.Create(2, 2, WorkingFormat.Rgba8);
        baseImage.Fill(new RgbaColor(128, 128, 128, 255));

        using var white = Image.Create(2, 2, WorkingFormat.Rgba8);
        white.Fill(RgbaColor.White);

        using var result = baseImage.Pipe().ApplyGainMap(white, headroom: 4f).ToImage();

        result.Format.ShouldBe(WorkingFormat.Rgba32F);
        // sRGB 128 is 0.2159 linear; a full gain doubles the brightness twice.
        First(result).ShouldBe(0.2159f * 4f, 0.01f);
    }

    [Fact]
    public void GainMap_ZeroGainLeavesTheLinearBase()
    {
        using var baseImage = Image.Create(2, 2, WorkingFormat.Rgba8);
        baseImage.Fill(new RgbaColor(128, 128, 128, 255));

        using var black = Image.Create(2, 2, WorkingFormat.Rgba8);
        black.Fill(RgbaColor.Black);

        using var result = baseImage.Pipe().ApplyGainMap(black).ToImage();

        First(result).ShouldBe(0.2159f, 0.01f);
    }

    [Fact]
    public void GainMap_RejectsMismatchedSizes()
    {
        using var baseImage = Float(4, 4, 0.5f, 0.5f, 0.5f);
        using var map = Float(2, 2, 1f, 1f, 1f);

        Should.Throw<ArgumentException>(() => baseImage.Pipe().ApplyGainMap(map).ToImage());
        Should.Throw<ArgumentOutOfRangeException>(() => baseImage.Pipe().ApplyGainMap(baseImage, headroom: 1f).ToImage());
        Should.Throw<ArgumentOutOfRangeException>(() => baseImage.Pipe().ApplyGainMap(baseImage, weight: 1.5f).ToImage());
    }

    [Fact]
    public void Ssim_IsOneForIdenticalImages()
    {
        using var first = Image.Create(16, 16, WorkingFormat.Rgba8);
        using var second = Image.Create(16, 16, WorkingFormat.Rgba8);
        first.Fill(new RgbaColor(120, 60, 30, 255));
        second.Fill(new RgbaColor(120, 60, 30, 255));

        first.Ssim(second).ShouldBe(1.0, 1e-5);
    }

    [Fact]
    public void Ssim_DropsForADifferentImage()
    {
        using var first = Image.Create(16, 16, WorkingFormat.Rgba8);
        using var second = Image.Create(16, 16, WorkingFormat.Rgba8);
        first.Fill(RgbaColor.Black);
        second.Fill(RgbaColor.Black);
        second.FillRect(new Sdcb.SimdCodecs.Abstractions.PixelRect(0, 0, 16, 8), RgbaColor.White);

        var score = first.Ssim(second);

        score.ShouldBeLessThan(0.9);
        score.ShouldBeGreaterThan(-1.0);
    }

    [Fact]
    public void Ssim_RejectsMismatchedSizes()
    {
        using var first = Image.Create(8, 8, WorkingFormat.Rgba8);
        using var second = Image.Create(8, 9, WorkingFormat.Rgba8);

        Should.Throw<ArgumentException>(() => first.Ssim(second));
    }

    // ---------------------------------------------------------------------------------------------
    // hdrgm reconstruction. The offsets are the format's own 1/64, so they cancel at headroom 1 and
    // shift the result everywhere else — which is how the tests tell a correct model from ApplyGainMap.
    // ---------------------------------------------------------------------------------------------

    private static GainMapParameters Hdrgm(double min = -1, double max = 1, double gamma = 1) =>
        new(min, max, gamma, 1.0 / 64.0, 1.0 / 64.0, 0, 1, false);

    private static float Pixel0(WorkingBuffer buffer) =>
        MemoryMarshal.Cast<byte, float>(buffer.View.Span)[0];

    [Fact]
    public void ReconstructGainMap_AtHeadroomOneReturnsTheLinearBase()
    {
        using var baseImage = Float(4, 4, 0.5f, 0.5f, 0.5f);
        using var gain = Float(4, 4, 1f, 1f, 1f);

        using var result = HdrOps.ReconstructGainMap(baseImage[0].Pixels.View, gain[0].Pixels.View, Hdrgm(), 1f);

        // Capacity is zero at headroom 1, and the two offsets cancel: the answer is the linearised base.
        Pixel0(result).ShouldBe(ColorMath.SrgbToLinear(0.5f), 1e-6f);
    }

    [Fact]
    public void ReconstructGainMap_AtFullCapacityAppliesTheAuthoredGain()
    {
        using var baseImage = Float(4, 4, 0.5f, 0.5f, 0.5f);
        using var gain = Float(4, 4, 1f, 1f, 1f);

        // Headroom 4 is log2 = 2, above HdrCapacityMax = 1, so the full authored gain applies: 2^1.
        using var result = HdrOps.ReconstructGainMap(baseImage[0].Pixels.View, gain[0].Pixels.View, Hdrgm(), 4f);

        var expected = (ColorMath.SrgbToLinear(0.5f) + 1f / 64f) * 2f - 1f / 64f;
        Pixel0(result).ShouldBe(expected, 1e-6f);
    }

    [Fact]
    public void ReconstructGainMap_TheMinimumGainAppliesToAZeroMap()
    {
        using var baseImage = Float(4, 4, 0.5f, 0.5f, 0.5f);
        using var gain = Float(4, 4, 0f, 0f, 0f);

        using var result = HdrOps.ReconstructGainMap(baseImage[0].Pixels.View, gain[0].Pixels.View, Hdrgm(), 4f);

        var expected = (ColorMath.SrgbToLinear(0.5f) + 1f / 64f) * 0.5f - 1f / 64f;
        Pixel0(result).ShouldBe(expected, 1e-6f);
    }

    [Fact]
    public void ReconstructGainMap_UpsamplesALowResolutionMap()
    {
        using var baseImage = Float(8, 6, 0.5f, 0.5f, 0.5f);
        using var gain = Float(2, 2, 1f, 1f, 1f);

        using var result = HdrOps.ReconstructGainMap(baseImage[0].Pixels.View, gain[0].Pixels.View, Hdrgm(), 4f);

        var pixels = MemoryMarshal.Cast<byte, float>(result.View.Span);
        var expected = (ColorMath.SrgbToLinear(0.5f) + 1f / 64f) * 2f - 1f / 64f;

        for (var i = 0; i < pixels.Length; i += 4)
            pixels[i].ShouldBe(expected, 1e-5f);
    }

    [Fact]
    public void ReconstructGainMap_LeavesValuesAboveOneUnclamped()
    {
        using var baseImage = Float(2, 2, 1f, 1f, 1f);
        using var gain = Float(2, 2, 1f, 1f, 1f);

        // Two stops of authored gain on white: the whole point of the format.
        using var result = HdrOps.ReconstructGainMap(baseImage[0].Pixels.View, gain[0].Pixels.View,
            Hdrgm(min: 0, max: 2), 8f);

        Pixel0(result).ShouldBeGreaterThan(3.5f);
    }

    [Fact]
    public void ReconstructGainMap_RejectsIllegalArguments()
    {
        using var baseImage = Float(2, 2, 0.5f, 0.5f, 0.5f);
        using var gain = Float(2, 2, 0.5f, 0.5f, 0.5f);
        var view = baseImage[0].Pixels.View;
        var map = gain[0].Pixels.View;

        Should.Throw<ArgumentOutOfRangeException>(() => HdrOps.ReconstructGainMap(view, map, Hdrgm(), 0f));
        Should.Throw<ArgumentOutOfRangeException>(() => HdrOps.ReconstructGainMap(view, map, Hdrgm(), 4f, weight: 1.5f));
        Should.Throw<ArgumentException>(() => HdrOps.ReconstructGainMap(view, map, Hdrgm(max: -2), 4f));
        Should.Throw<ArgumentOutOfRangeException>(() => HdrOps.ReconstructGainMap(view, map, Hdrgm(gamma: 0), 4f));
    }

    [Fact]
    public void ApplyGainMap_KeepsItsPublishedFormula()
    {
        // Regression on the old simplified model: headroom 4 and a full gain map quadruple the linear
        // base, with no offsets and no capacity interpolation. ReconstructGainMap must not have moved it.
        using var baseImage = Float(2, 2, 0.5f, 0.5f, 0.5f);
        using var gain = Float(2, 2, 1f, 1f, 1f);

        using var result = baseImage.Pipe().ApplyGainMap(gain, headroom: 4f).ToImage();

        First(result).ShouldBe(ColorMath.SrgbToLinear(0.5f) * 4f, 1e-6f);
    }
}
