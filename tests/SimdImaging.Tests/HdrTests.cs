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
        using var mapped = source.ToneMap(ToneMapCurve.Aces);

        First(mapped).ShouldBe(0.8038f, 0.002f);
    }

    [Fact]
    public void ToneMap_ReinhardHalvesOne()
    {
        using var source = Float(2, 2, 1f, 1f, 1f);
        using var mapped = source.ToneMap(ToneMapCurve.Reinhard);

        First(mapped).ShouldBe(0.5f, 0.001f);
    }

    [Fact]
    public void ToneMap_NoneClampsAboveOne()
    {
        using var source = Float(2, 2, 4f, 0.25f, 0f);
        using var mapped = source.ToneMap(ToneMapCurve.None);

        var samples = MemoryMarshal.Cast<byte, float>(mapped.WritablePixels);
        samples[0].ShouldBe(1f);
        samples[1].ShouldBe(0.25f, 1e-6f);
        samples[2].ShouldBe(0f);
    }

    [Fact]
    public void ToneMap_ExposureIsInStops()
    {
        using var source = Float(2, 2, 0.25f, 0.25f, 0.25f);
        using var mapped = source.ToneMap(ToneMapCurve.None, exposure: 1f);

        First(mapped).ShouldBe(0.5f, 1e-5f);
    }

    [Fact]
    public void ToneMap_AutoExposureLiftsADarkImage()
    {
        using var source = Float(4, 4, 0.01f, 0.01f, 0.01f);
        using var mapped = source.ToneMap(ToneMapCurve.None, autoExposure: true, targetLuminance: 0.18f);

        First(mapped).ShouldBe(0.18f, 0.01f);
    }

    [Fact]
    public void ToneMap_Bt2390MapsTheScenePeakToTheDisplayPeak()
    {
        // Linear 1.0 is 10 000 cd/m² in PQ units; a 100 cd/m² display is linear 0.01.
        using var source = Float(2, 2, 1f, 1f, 1f);
        using var mapped = source.ToneMap(ToneMapCurve.Bt2390, whitePoint: 0.01f);

        var value = First(mapped);
        value.ShouldBe(0.01f, 0.002f);
    }

    [Fact]
    public void ToneMap_Bt2390WithThePqCeilingDoesNotDivideByZero()
    {
        using var source = Float(2, 2, 10f, 10f, 10f);
        using var mapped = source.ToneMap(ToneMapCurve.Bt2390, whitePoint: 1f);

        First(mapped).ShouldBe(1f);
    }

    [Fact]
    public void ToneMap_RejectsIllegalParameters()
    {
        using var source = Float(2, 2, 1f, 1f, 1f);
        Should.Throw<ArgumentOutOfRangeException>(() => source.ToneMap(whitePoint: 0f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.ToneMap(targetLuminance: 0f));
        Should.Throw<ArgumentOutOfRangeException>(() => source.ToneMap(exposure: float.PositiveInfinity));
    }

    [Fact]
    public void GainMap_MultipliesLinearLight()
    {
        using var baseImage = Image.Create(2, 2, WorkingFormat.Rgba8);
        baseImage.Fill(new RgbaColor(128, 128, 128, 255));

        using var white = Image.Create(2, 2, WorkingFormat.Rgba8);
        white.Fill(RgbaColor.White);

        using var result = baseImage.ApplyGainMap(white, headroom: 4f);

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

        using var result = baseImage.ApplyGainMap(black);

        First(result).ShouldBe(0.2159f, 0.01f);
    }

    [Fact]
    public void GainMap_RejectsMismatchedSizes()
    {
        using var baseImage = Float(4, 4, 0.5f, 0.5f, 0.5f);
        using var map = Float(2, 2, 1f, 1f, 1f);

        Should.Throw<ArgumentException>(() => baseImage.ApplyGainMap(map));
        Should.Throw<ArgumentOutOfRangeException>(() => baseImage.ApplyGainMap(baseImage, headroom: 1f));
        Should.Throw<ArgumentOutOfRangeException>(() => baseImage.ApplyGainMap(baseImage, weight: 1.5f));
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
}
