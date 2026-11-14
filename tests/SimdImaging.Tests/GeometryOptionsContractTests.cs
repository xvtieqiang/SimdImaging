using System.Runtime.InteropServices;
using System.Text.Json;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// Contract tests for the M8 second-wave <c>…WithOptions</c> entry points
/// (<see cref="Mapping.TransformWithOptions"/>, <see cref="Mapping.PerspectiveWithOptions"/>,
/// <see cref="Resample.ResizeWithOptions"/> and the Image facades). Only the new entry points are
/// covered here; the frozen legacy assertions live in AffineTests / FloodPerspectiveTests /
/// GeometryTests and are deliberately untouched.
/// <para>Key semantics under test: <c>options == default</c> reproduces the historical chain contract
/// (byte-identical to the legacy Mapping/Resize paths on quantised formats), the centre-level
/// background rule is Mapping geometry independent of <see cref="EdgeMode"/>, and unsupported or
/// out-of-range situations fail loudly.</para>
/// </summary>
public class GeometryOptionsContractTests
{
    // ------------------------------------------------------- default ≡ legacy contract

    [Theory]
    [InlineData(WorkingFormat.Rgba8)]
    [InlineData(WorkingFormat.Rgba16)]
    public void TransformWithOptions_Default_ReproducesTheLegacyBytes(WorkingFormat format)
    {
        using var source = Gradient(8, 5, format);

        using var legacy = source.Affine(AffineMatrix.Translation(0.37f, 0.71f), 10, 6);
        using var options = source.AffineWithOptions(AffineMatrix.Translation(0.37f, 0.71f), 10, 6,
            Resampler.Bilinear, default);

        legacy.WritablePixels.ToArray().ShouldBe(options.WritablePixels.ToArray(),
            "the default contract must match the frozen legacy path on quantised formats");
    }

    [Fact]
    public void ResizeWithOptions_Default_IsByteIdenticalToResize()
    {
        using var source = Gradient(9, 7, WorkingFormat.Rgba8);

        using var legacy = source.Resize(4, 3, Resampler.Lanczos3);
        using var options = source.ResizeWithOptions(4, 3, Resampler.Lanczos3, default);

        legacy.WritablePixels.ToArray().ShouldBe(options.WritablePixels.ToArray());
    }

    // ------------------------------------------------------- centre-level background rule

    [Fact]
    public void RotateWithOptions_BackgroundsTheCornersRegardlessOfEdgeMode()
    {
        using var source = Gradient(11, 11, WorkingFormat.Rgba8);

        // Edge stays Clamp, yet a 45° rotation still fills the corners with the background colour:
        // the centre-level rule is Mapping geometry, not an edge mode.
        using var rotated = source.RotateWithOptions(45f, Resampler.Bilinear,
            new SampleOptions { Edge = EdgeMode.Clamp }, new RgbaColor(200, 40, 60, 255));

        rotated.Width.ShouldBeGreaterThan(11);
        rotated.WritablePixels.Slice(0, 4).ToArray().ShouldBe(new byte[] { 200, 40, 60, 255 });
    }

    [Fact]
    public void PerspectiveWithOptions_BehindTheCameraIsBackground()
    {
        using var source = Gradient(8, 5, WorkingFormat.Rgba8);

        // w' = 1 - 0.6·px (from the INVERSE homography) turns negative past px ≈ 1.67: the right half
        // is background even with Edge = Clamp, because a point behind the camera is not an edge case.
        var homography = new float[] { 1f, 0f, 0f, 0f, 1f, 0f, 0.6f, 0f };
        using var warped = source.PerspectiveWithOptions(homography, 8, 5, Resampler.Bilinear,
            new SampleOptions { Edge = EdgeMode.Clamp }, new RgbaColor(9, 8, 7, 255));

        var pixels = warped.WritablePixels;
        // x=0 maps to source 0.71 (inside); x=1 maps to source 15 (outside the box → background);
        // x≥2 has w' ≤ 0 (behind the camera → background).
        pixels.Slice(4 * 0, 4).ToArray().ShouldNotBe(new byte[] { 9, 8, 7, 255 });
        pixels.Slice(4 * 1, 4).ToArray().ShouldBe(new byte[] { 9, 8, 7, 255 });
        pixels.Slice(4 * 7, 4).ToArray().ShouldBe(new byte[] { 9, 8, 7, 255 });
    }

    // ------------------------------------------------------- edge modes on taps

    [Fact]
    public void ResizeWithOptions_BackgroundEdge_FillsOutOfBoundsTapsWithoutRenormalising()
    {
        // One opaque white pixel magnified 2×2. Every destination tap reaches past the single source
        // pixel: Clamp replicates it (pure white), Background blends in the constant field.
        using var source = Image.Create(1, 1, WorkingFormat.Rgba8);
        source.WritablePixels[0] = 255;
        source.WritablePixels[1] = 255;
        source.WritablePixels[2] = 255;
        source.WritablePixels[3] = 255;

        using var clamped = source.ResizeWithOptions(2, 2, Resampler.Bilinear,
            new SampleOptions { Edge = EdgeMode.Clamp });
        using var background = source.ResizeWithOptions(2, 2, Resampler.Bilinear,
            new SampleOptions { Edge = EdgeMode.Background }, new RgbaColor(0, 0, 0, 255));

        clamped.WritablePixels.ToArray().ShouldBe(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255 });
        // Per-tap background: any tap outside the single source pixel (including the whole y = -1
        // row) is replaced by the field, leaving only the (0,0) tap's weight 0.75² ≈ 0.5625 on white.
        background.WritablePixels[0].ShouldBeInRange((byte)135, (byte)150,
            "the corner must blend the white pixel with the black field");
        background.WritablePixels[3].ShouldBe((byte)255);
    }

    // ------------------------------------------------------- domain / alpha / out-of-gamut

    [Fact]
    public void TransformWithOptions_LinearLight_IsBrighterThanTheEncodedMidpoint()
    {
        using var image = Image.Create(2, 1, WorkingFormat.Rgba8);
        image.WritablePixels[0] = 0;
        image.WritablePixels[1] = 0;
        image.WritablePixels[2] = 0;
        image.WritablePixels[3] = 255;
        image.WritablePixels[4] = 255;
        image.WritablePixels[5] = 255;
        image.WritablePixels[6] = 255;
        image.WritablePixels[7] = 255;

        // Forward translation by -0.5: the destination centre (areal 0.5) maps back to source areal
        // 1.0 — the exact midpoint between the 0 and 255 pixels.
        var forward = AffineMatrix.Translation(-0.5f, 0f);
        using var encoded = image.AffineWithOptions(forward, 1, 1, Resampler.Bilinear, default);
        using var linear = image.AffineWithOptions(forward, 1, 1, Resampler.Bilinear,
            new SampleOptions { Domain = SampleDomain.LinearLight });

        (linear.WritablePixels[0] > encoded.WritablePixels[0]).ShouldBeTrue(
            "linear-light interpolation of 0 and 255 must be brighter than the encoded-domain midpoint");
    }

    [Fact]
    public void ResizeWithOptions_Premultiplied_DoesNotBleedTransparentColour()
    {
        // The all-zero SampleOptions IS the historical (premultiplied) contract — the zero-value
        // tension documented in known-gaps.md — so the literal straight/premultiplied comparison
        // rides on a non-zero Edge=Background option, which forces the unified contract where
        // Alpha is then interpreted literally (0 = Straight).
        using var image = Image.Create(2, 1, WorkingFormat.Rgba8);
        image.WritablePixels[0] = 200; // opaque red
        image.WritablePixels[1] = 0;
        image.WritablePixels[2] = 0;
        image.WritablePixels[3] = 255;
        image.WritablePixels[4] = 0;   // fully transparent, and NOT black — its colour must not bleed
        image.WritablePixels[5] = 250;
        image.WritablePixels[6] = 250;
        image.WritablePixels[7] = 0;
        var field = new RgbaColor(0, 250, 250, 0); // transparent green background

        using var straight = image.ResizeWithOptions(1, 1, Resampler.Bilinear,
            new SampleOptions { Edge = EdgeMode.Background }, field);
        using var premultiplied = image.ResizeWithOptions(1, 1, Resampler.Bilinear,
            new SampleOptions { Edge = EdgeMode.Background, Alpha = AlphaMode.Premultiplied }, field);

        straight.WritablePixels[1].ShouldBeGreaterThan(premultiplied.WritablePixels[1],
            "in straight accumulation the transparent field's green reaches the result; " +
            "premultiplied accumulation must keep the output green low");
    }

    [Fact]
    public void ResizeWithOptions_OutOfGamutThrow_RejectsHdrSamples()
    {
        using var image = Image.Create(2, 1, WorkingFormat.Rgba32F);
        var floats = MemoryMarshal.Cast<byte, float>(image.WritablePixels);
        floats[0] = 4f;      // HDR > 1
        floats[1] = 0.25f;
        floats[2] = 0.5f;
        floats[3] = 1f;
        floats[4] = 0.5f;
        floats[5] = 0.25f;
        floats[6] = 0.5f;
        floats[7] = 1f;

        using (var clamped = image.ResizeWithOptions(1, 1, Resampler.Bilinear,
            new SampleOptions { OutOfGamut = OutOfGamutPolicy.Clamp }))
        {
            MemoryMarshal.Cast<byte, float>(clamped.WritablePixels)[0].ShouldBe(1f);
        }

        Should.Throw<InvalidDataException>(() => image.ResizeWithOptions(1, 1, Resampler.Bilinear,
            new SampleOptions { OutOfGamut = OutOfGamutPolicy.Throw }));
    }

    [Fact]
    public void PerspectiveWithOptions_OutOfGamutThrow_PropagatesTheContract()
    {
        using var image = Image.Create(1, 1, WorkingFormat.Rgba32F);
        MemoryMarshal.Cast<byte, float>(image.WritablePixels)[0] = 4f;
        MemoryMarshal.Cast<byte, float>(image.WritablePixels)[1] = 0.25f;
        MemoryMarshal.Cast<byte, float>(image.WritablePixels)[2] = 0.5f;
        MemoryMarshal.Cast<byte, float>(image.WritablePixels)[3] = 1f;

        Should.Throw<InvalidDataException>(() => image.PerspectiveWithOptions(
            new float[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f }, 1, 1, Resampler.Bilinear,
            new SampleOptions { OutOfGamut = OutOfGamutPolicy.Throw }));
    }

    // ------------------------------------------------------- failures stay loud

    [Fact]
    public void WithOptions_RejectsASingularTransformAndABadHomography()
    {
        using var source = Gradient(3, 3, WorkingFormat.Rgba8);
        var singular = new AffineMatrix(1f, 2f, 0f, 2f, 4f, 0f);

        Should.Throw<ArgumentException>(() =>
            source.AffineWithOptions(singular, 3, 3, Resampler.Bilinear, default));
        Should.Throw<ArgumentException>(() =>
            source.PerspectiveWithOptions(new float[] { 1f, 0f }, 3, 3, Resampler.Bilinear, default));
    }

    // ------------------------------------------------------- executed reference vectors

    [Theory]
    [InlineData("warp.tap.bilinear-phase")]
    [InlineData("resize.background-edge")]
    [InlineData("sample-options-encoded-midpoint")]
    public void Vectors_ExecuteByteExactly(string caseId)
    {
        var document = JsonDocument.Parse(File.ReadAllText(VectorPath(caseId + ".json")));
        var root = document.RootElement;
        var input = root.GetProperty("input");
        var expectedBytes = Convert.FromHexString(root.GetProperty("expected").GetProperty("bytes").GetString()!);
        var format = Enum.Parse<WorkingFormat>(input.GetProperty("format").GetString()!);

        using var source = Image.Create(input.GetProperty("width").GetInt32(), input.GetProperty("height").GetInt32(), format);
        input.GetProperty("bytes").GetString()!.Length.ShouldBe(source.WritablePixels.Length * 2);
        Convert.FromHexString(input.GetProperty("bytes").GetString()!).CopyTo(source.WritablePixels);

        using var result = root.GetProperty("params").GetProperty("op").GetString() switch
        {
            // The midpoint vector maps the destination centre (areal 0.5) onto source areal 1.0,
            // the exact junction of the two pixels: (0 + 255) / 2 in the encoded domain.
            "WarpWithOptions" when caseId == "sample-options-encoded-midpoint" => source.WarpWithOptions(
                root.GetProperty("params").GetProperty("expectedWidth").GetInt32(),
                root.GetProperty("params").GetProperty("expectedHeight").GetInt32(),
                (float x, float y, out float sx, out float sy) =>
                {
                    sx = x + 0.5f;
                    sy = y;
                }, Resampler.Bilinear, default),
            "WarpWithOptions" => source.WarpWithOptions(
                root.GetProperty("params").GetProperty("expectedWidth").GetInt32(),
                root.GetProperty("params").GetProperty("expectedHeight").GetInt32(),
                (float x, float y, out float sx, out float sy) =>
                {
                    // sx = x + 0.37, sy = y — the 0.37-phase map the vector documents.
                    sx = x + 0.37f;
                    sy = y;
                }, Resampler.Bilinear, default),
            "ResizeWithOptions" => source.ResizeWithOptions(
                root.GetProperty("params").GetProperty("expectedWidth").GetInt32(),
                root.GetProperty("params").GetProperty("expectedHeight").GetInt32(),
                Resampler.Bilinear,
                new SampleOptions { Edge = EdgeMode.Background }, new RgbaColor(0, 0, 0, 255)),
            var op => throw new InvalidOperationException($"vector {caseId} uses unknown op {op}"),
        };

        result.WritablePixels.ToArray().ShouldBe(expectedBytes, caseId);
    }

    private static string VectorPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "vectors", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"tests/Fixtures/vectors/{fileName} not found");
    }

    // ------------------------------------------------------- helpers

    private static Image Gradient(int width, int height, WorkingFormat format)
    {
        var image = Image.Create(width, height, format);
        var pixels = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                switch (format)
                {
                    case WorkingFormat.Rgba8:
                        pixels[o] = (byte)(x * 29 + 11);
                        pixels[o + 1] = (byte)(y * 47 + 3);
                        pixels[o + 2] = (byte)(x * 7 + y * 13 + 1);
                        pixels[o + 3] = 255;
                        break;
                    case WorkingFormat.Rgba16:
                    {
                        var wide = MemoryMarshal.Cast<byte, ushort>(pixels);
                        wide[o] = (ushort)((x * 4093 + y * 97 + 13) & 0xFFFF);
                        wide[o + 1] = (ushort)((x * 997 + y * 631 + 7) & 0xFFFF);
                        wide[o + 2] = (ushort)((x * 631 + y * 4093 + 101) & 0xFFFF);
                        wide[o + 3] = 65535;
                        break;
                    }
                    default:
                    {
                        var floats = MemoryMarshal.Cast<byte, float>(pixels);
                        floats[o] = (x + y * width) / (float)(width * height);
                        floats[o + 1] = (y + 1) / (float)(height + 1);
                        floats[o + 2] = (x + 1) / (float)(width + 1);
                        floats[o + 3] = 1f;
                        break;
                    }
                }
            }
        }

        return image;
    }
}
