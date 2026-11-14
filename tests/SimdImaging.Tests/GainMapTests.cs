using System.Runtime.InteropServices;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Ultra HDR round trips against the codec's own gain-map package: compute a gain map, assemble an
/// Ultra HDR JPEG, read it back, reconstruct. These are also the tests that pin what
/// <c>Image.Load</c> already does with such a file before any of this library's gain-map API is used.
/// </summary>
public class GainMapTests
{
    private const int Width = 64;
    private const int Height = 48;
    private const int Downscale = 2;

    /// <summary>
    /// A scene with real headroom: a horizontal ramp whose top-left square is four times SDR white.
    /// The SDR rendition is a plain clamp, so the gain map's fitted bounds have something to describe.
    /// </summary>
    private static (float[] Hdr, byte[] Sdr) Scene()
    {
        var hdr = new float[Width * Height * 4];
        var sdr = new byte[Width * Height * 4];

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = (y * Width + x) * 4;
                var linear = 0.05f + 0.9f * x / (Width - 1);
                if (x < 8 && y < 8)
                    linear = 4.0f;

                hdr[offset] = linear;
                hdr[offset + 1] = linear * 0.75f;
                hdr[offset + 2] = linear * 0.5f;
                hdr[offset + 3] = 1f;

                sdr[offset] = Encode(Math.Min(linear, 1f));
                sdr[offset + 1] = Encode(Math.Min(linear * 0.75f, 1f));
                sdr[offset + 2] = Encode(Math.Min(linear * 0.5f, 1f));
                sdr[offset + 3] = 255;
            }
        }

        return (hdr, sdr);
    }

    private static byte Encode(float linear)
    {
        var srgb = ColorMath.LinearToSrgb(linear);
        return (byte)Math.Clamp((int)MathF.Round(srgb * 255f), 0, 255);
    }

    private static byte[] AssembleUltraHdr(out GainMapComputeResult computed, out byte[] gainJpeg)
    {
        var (hdr, sdr) = Scene();
        computed = JpegGainMap.Compute(hdr, sdr, Width, Height, Downscale);
        computed.Width.ShouldBe(Width / Downscale);
        computed.Height.ShouldBe(Height / Downscale);

        using var baseImage = Image.Create(Width, Height, WorkingFormat.Rgba8);
        sdr.CopyTo(baseImage.WritablePixels);
        var baseJpeg = Image.Encode(baseImage, ImageFormat.Jpeg, new SaveOptions { JpegQuality = 95 });

        gainJpeg = EncodeGray(computed.GainMapGray8, computed.Width, computed.Height);
        return JpegGainMap.Assemble(baseJpeg, gainJpeg, computed.Metadata);
    }

    private static byte[] EncodeGray(byte[] pixels, int width, int height)
    {
        using var stream = new MemoryStream();
        ImageCodecs.Encode(pixels, width, height, PixelFormat.Gray8, ImageFormat.Jpeg, stream, width,
            options: new JpegEncodeOptions { Quality = 95 }, metadata: ImageMetadata.Empty);
        return stream.ToArray();
    }

    [Fact]
    public void Assemble_ThenTryRead_KeepsBothRenditionsAndTheMetadata()
    {
        var ultraHdr = AssembleUltraHdr(out var computed, out _);

        JpegGainMap.TryRead(ultraHdr, out var image).ShouldBeTrue();

        image.Base.Width.ShouldBe(Width);
        image.Base.Height.ShouldBe(Height);
        image.GainMap.Width.ShouldBe(Width / Downscale);
        image.GainMap.Height.ShouldBe(Height / Downscale);

        image.Metadata.ShouldNotBeNull();
        image.Metadata!.GainMapMax.ShouldBe(computed.Metadata.GainMapMax, 1e-6);
        image.Metadata.HdrCapacityMax.ShouldBe(computed.Metadata.HdrCapacityMax, 1e-6);
    }

    [Fact]
    public void ImageLoad_OnAnUltraHdrJpeg_ReturnsFloatPixelsBecauseTheFacadeServesTheHdrRendition()
    {
        // Recorded behaviour, not an assumption: the codec facade hands back a gain-map decoded image
        // whose SampleFormat is Float32, so Image.Load lands on Rgba32F without this library asking.
        var ultraHdr = AssembleUltraHdr(out _, out _);

        using var loaded = Image.Load(ultraHdr);

        loaded.Format.ShouldBe(WorkingFormat.Rgba32F);

        var pixels = MemoryMarshal.Cast<byte, float>(loaded.WritablePixels);
        var peak = 0f;
        for (var i = 0; i < pixels.Length; i += 4)
            peak = Math.Max(peak, pixels[i]);

        peak.ShouldBeGreaterThan(1.5f, $"the authored highlight is 4.0; the peak came back as {peak}");
    }

    [Fact]
    public void ReconstructHdr_RecoversTheAuthoredHighlightAndCollapsesToTheBaseAtHeadroomOne()
    {
        var ultraHdr = AssembleUltraHdr(out _, out _);
        JpegGainMap.TryRead(ultraHdr, out var image).ShouldBeTrue();

        using var full = image.ReconstructHdr(4.0);
        using var flat = image.ReconstructHdr(1.0);

        var fullPixels = full.ToFloats();
        var flatPixels = flat.ToFloats();

        var peak = 0f;
        for (var i = 0; i < fullPixels.Length; i += 3)
            peak = Math.Max(peak, fullPixels[i]);

        // The authored highlight is 4.0; the fitted gain map and JPEG round trip are allowed to lose a
        // little, but the reconstruction has to actually reach above SDR white.
        peak.ShouldBeGreaterThan(2.5f, $"reconstructed peak {peak}");
        peak.ShouldBeLessThan(6.0f, $"reconstructed peak {peak}");

        var flatPeak = 0f;
        for (var i = 0; i < flatPixels.Length; i += 3)
            flatPeak = Math.Max(flatPeak, flatPixels[i]);

        flatPeak.ShouldBeLessThanOrEqualTo(1.01f, $"headroom 1 must not boost; peak {flatPeak}");
    }

    [Fact]
    public void FromHdrThenEncodeThenLoad_RecoversTheAuthoredHighlight()
    {
        var (hdr, _) = Scene();
        using var hdrImage = Image.Create(Width, Height, WorkingFormat.Rgba32F);
        var samples = MemoryMarshal.Cast<byte, float>(hdrImage.WritablePixels);
        hdr.CopyTo(samples);

        using var pair = GainMappedImage.FromHdr(hdrImage, downscale: Downscale);
        pair.Base.Format.ShouldBe(WorkingFormat.Rgba8);
        pair.GainMap.Width.ShouldBe(Width / Downscale);
        pair.GainMap.Height.ShouldBe(Height / Downscale);

        var encoded = pair.Encode(new SaveOptions { JpegQuality = 95 });
        JpegGainMap.TryRead(encoded, out var codecView).ShouldBeTrue();

        using var reloaded = GainMappedImage.Load(encoded);
        reloaded.Base.Width.ShouldBe(Width);
        reloaded.GainMap.Width.ShouldBe(Width / Downscale);

        using var reconstructed = reloaded.ToHdr(4f);
        var peak = 0f;
        var floats = MemoryMarshal.Cast<byte, float>(reconstructed.WritablePixels);
        for (var i = 0; i < floats.Length; i += 4)
            peak = Math.Max(peak, floats[i]);

        peak.ShouldBeGreaterThan(2.5f, $"reconstructed peak {peak}");
    }

    [Fact]
    public void Encode_WritesAGainMapWhoseChannelsAgreeSoItsLumaIsWellDefined()
    {
        var (hdr, _) = Scene();
        using var hdrImage = Image.Create(Width, Height, WorkingFormat.Rgba32F);
        hdr.CopyTo(MemoryMarshal.Cast<byte, float>(hdrImage.WritablePixels));

        using var pair = GainMappedImage.FromHdr(hdrImage, downscale: Downscale);
        var encoded = pair.Encode();

        JpegGainMap.TryRead(encoded, out var codecView).ShouldBeTrue();

        // The encoder writes a computed (grey) map as one channel; the decoder still hands back three,
        // and they have to agree or the reconstruction's luma would depend on a colour that is not there.
        var pixels = codecView.GainMap.ToRgba8();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i].ShouldBe(pixels[i + 1]);
            pixels[i].ShouldBe(pixels[i + 2]);
        }
    }

    [Fact]
    public void Load_RejectsBytesThatAreNotAnUltraHdrJpeg()
    {
        using var plain = Image.Create(8, 8, WorkingFormat.Rgba8);
        var jpeg = Image.Encode(plain, ImageFormat.Jpeg);

        Should.Throw<InvalidDataException>(() => GainMappedImage.Load(jpeg));
    }

    [Fact]
    public void Load_RejectsOptionsThatWouldHaveToApplyToBothRenditionsDifferently()
    {
        var ultraHdr = AssembleUltraHdr(out _, out _);

        Should.Throw<NotSupportedException>(() => GainMappedImage.Load(ultraHdr,
            new LoadOptions { Region = new Sdcb.SimdCodecs.Abstractions.PixelRect(0, 0, 8, 8) }));
        Should.Throw<NotSupportedException>(() => GainMappedImage.Load(ultraHdr,
            new LoadOptions { ScaleDenominator = 2 }));
    }

    [Fact]
    public void FromHdr_RejectsANonFloatRendition()
    {
        using var sdr = Image.Create(8, 8, WorkingFormat.Rgba8);

        Should.Throw<ArgumentException>(() => GainMappedImage.FromHdr(sdr));
    }
}
