using System.Runtime.InteropServices;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// Contract tests for the explicit <see cref="SampleOptions"/> reconstruction path. These cover
/// only the NEW WithOptions entry point; the historical Warp / SampleRaw assertions live in
/// WarpPrecisionTests and are deliberately not touched.
/// </summary>
public class SampleOptionsContractTests
{
    private const int Width = 5;
    private const int Height = 4;

    private static void Identity(float x, float y, out float sourceX, out float sourceY)
    {
        sourceX = x;
        sourceY = y;
    }

    [Theory]
    [InlineData(WorkingFormat.Rgba8)]
    [InlineData(WorkingFormat.Rgba16)]
    [InlineData(WorkingFormat.Rgba32F)]
    public void WarpWithOptions_Identity_IsByteExactInEveryFormat(WorkingFormat format)
    {
        using var image = Image.Create(Width, Height, format);
        var expected = Fill(image, format);

        using var warped = image.Pipe().WarpWithOptions(Width, Height, Identity, Resampler.Bilinear, default).ToImage();

        warped.WritablePixels.ToArray().ShouldBe(expected);
    }

    [Theory]
    [InlineData(Resampler.Nearest)]
    [InlineData(Resampler.Box)]
    [InlineData(Resampler.Bilinear)]
    [InlineData(Resampler.Bicubic)]
    [InlineData(Resampler.Lanczos3)]
    public void WarpWithOptions_AcceptsAllFiveKernelsWithoutFallback(Resampler resampler)
    {
        using var image = Image.Create(Width, Height, WorkingFormat.Rgba8);
        Fill(image, WorkingFormat.Rgba8);

        using var warped = image.Pipe().WarpWithOptions(Width, Height, Identity, resampler,
            new SampleOptions { Edge = EdgeMode.Background }).ToImage();

        warped.WritablePixels.Length.ShouldBe(Width * Height * 4);
        // Identity must not depend on which reconstruction kernel was selected.
        warped.WritablePixels.ToArray().ShouldBe(image.WritablePixels.ToArray());
    }

    [Fact]
    public void WarpWithOptions_LinearLight_DoesNotInterpolateInTheGammaEncodedDomain()
    {
        using var image = Image.Create(2, 1, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        pixels[0] = 0;
        pixels[1] = 0;
        pixels[2] = 0;
        pixels[3] = 255;
        pixels[4] = 255;
        pixels[5] = 255;
        pixels[6] = 255;
        pixels[7] = 255;
        PixelWarp midpoint = (float _, float y, out float sx, out float sy) => { sx = 1f; sy = y; };

        using var encoded = image.Pipe().WarpWithOptions(1, 1, midpoint, Resampler.Bilinear, default).ToImage();
        using var linear = image.Pipe().WarpWithOptions(1, 1, midpoint, Resampler.Bilinear,
            new SampleOptions { Domain = SampleDomain.LinearLight }).ToImage();

        encoded.WritablePixels[0].ShouldBeInRange((byte)126, (byte)129);
        (linear.WritablePixels[0] > encoded.WritablePixels[0]).ShouldBeTrue(
            "linear-light interpolation of 0 and 1 must be brighter than the encoded-domain midpoint");
    }

    [Fact]
    public void WarpWithOptions_Premultiplied_DoesNotBleedTransparentColour()
    {
        using var image = Image.Create(2, 1, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        pixels[0] = 200; // opaque red
        pixels[3] = 255;
        pixels[4] = 0;   // fully transparent, black
        pixels[7] = 0;
        PixelWarp midpoint = (float _, float y, out float sx, out float sy) => { sx = 1f; sy = y; };

        using var straight = image.Pipe().WarpWithOptions(1, 1, midpoint, Resampler.Bilinear, default).ToImage();
        using var premultiplied = image.Pipe().WarpWithOptions(1, 1, midpoint, Resampler.Bilinear,
            new SampleOptions { Alpha = AlphaMode.Premultiplied }).ToImage();

        straight.WritablePixels[0].ShouldBeInRange((byte)99, (byte)101);
        (premultiplied.WritablePixels[0] > straight.WritablePixels[0]).ShouldBeTrue(
            "the transparent neighbour's black must not bleed into the interpolated colour");
    }

    [Fact]
    public void WarpWithOptions_OutOfGamut_ClampsOrThrowsNeverSilentlyPreserves()
    {
        using var image = Image.Create(1, 1, WorkingFormat.Rgba32F);
        var floats = MemoryMarshal.Cast<byte, float>(image.WritablePixels);
        floats[0] = 4f;      // HDR > 1
        floats[1] = 0.25f;
        floats[2] = 0.5f;
        floats[3] = 1f;

        using (var clamped = image.Pipe().WarpWithOptions(1, 1, Identity, Resampler.Bilinear,
            new SampleOptions { OutOfGamut = OutOfGamutPolicy.Clamp }).ToImage())
        {
            MemoryMarshal.Cast<byte, float>(clamped.WritablePixels)[0].ShouldBe(1f);
        }

        Should.Throw<InvalidDataException>(() => image.Pipe().WarpWithOptions(1, 1, Identity,
            Resampler.Bilinear, new SampleOptions { OutOfGamut = OutOfGamutPolicy.Throw }).ToImage());
    }

    [Fact]
    public void SampleTransfer_RoundTripsSixCurvesAndPreservesOutOfRangeValues()
    {
        foreach (var curve in Enum.GetValues<SampleTransferCurve>())
        {
            foreach (var value in new[] { 0f, 0.25f, 0.5f, 0.8f, 1f })
            {
                var back = SampleTransfer.FromLinear(SampleTransfer.ToLinear(value, curve), curve);
                back.ShouldBe(value, 2e-3f);
            }
        }

        // Values outside the curve's defined domain are preserved verbatim, never clamped or NaN.
        SampleTransfer.ToLinear(-0.25f, SampleTransferCurve.Srgb).ShouldBe(-0.25f);
        (SampleTransfer.ToLinear(4f, SampleTransferCurve.Srgb) > 1f).ShouldBeTrue();
        float.IsNaN(SampleTransfer.ToLinear(float.NaN, SampleTransferCurve.Pq)).ShouldBeTrue();
    }

    private static byte[] Fill(Image image, WorkingFormat format)
    {
        var pixels = image.WritablePixels;
        switch (format)
        {
            case WorkingFormat.Rgba8:
                new Random(991).NextBytes(pixels);
                break;
            case WorkingFormat.Rgba16:
            {
                var wide = MemoryMarshal.Cast<byte, ushort>(pixels);
                for (var i = 0; i < wide.Length; i++)
                    wide[i] = (ushort)((i * 1031 + 13) & 0xFFFF);
                break;
            }
            default:
            {
                var floats = MemoryMarshal.Cast<byte, float>(pixels);
                for (var i = 0; i < floats.Length; i++)
                    floats[i] = i % 3 == 0 ? 4f : i % 3 == 1 ? -0.25f : 0.25f;
                break;
            }
        }

        return pixels.ToArray();
    }
}
