using System.Runtime.InteropServices;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// M9 EWA precision tests. The oracle is an independent double-precision elliptical Gaussian
/// recomputed inside the test (it shares no code with the library's plan builder), rounded through
/// the same f32/ToByte discipline the frozen contract documents. Degenerate Jacobians must report
/// <see cref="EwaStatus.DegenerateIsotropic"/> and fall back to isotropic reconstruction observably;
/// the moiré scenario is a behavioural check (0.25× minification of a checkerboard must land near
/// the true average, where unfiltered bilinear produces aliasing extremes).
/// </summary>
public class EwaPrecisionTests
{
    [Fact]
    public void Elliptical_IdentityJacobian_PreservesASymmetricCentre()
    {
        // 3×1 grey ramp [0 | 128 | 255], destination centre exactly on the middle pixel: the
        // elliptical window is symmetric around it, so equal weights cancel and the centre byte
        // must survive exactly.
        using var source = Image.Create(3, 1, WorkingFormat.Rgba8);
        source.WritablePixels[0] = 0;
        source.WritablePixels[1] = 0;
        source.WritablePixels[2] = 0;
        source.WritablePixels[3] = 255;
        source.WritablePixels[4] = 128;
        source.WritablePixels[5] = 128;
        source.WritablePixels[6] = 128;
        source.WritablePixels[7] = 255;
        source.WritablePixels[8] = 255;
        source.WritablePixels[9] = 255;
        source.WritablePixels[10] = 255;
        source.WritablePixels[11] = 255;

        // A translation of -1 steers the destination centre (areal 0.5) onto the middle pixel
        // (areal 1.5), where the elliptical window is symmetric around it.
        using var result = source.AffineWithOptions(AffineMatrix.Translation(-1f, 0f), 1, 1,
            Resampler.Bilinear, new SampleOptions { AaMode = SampleAaMode.Elliptical });

        result.WritablePixels.Slice(0, 4).ToArray().ShouldBe(new byte[] { 128, 128, 128, 255 });
    }

    [Theory]
    [InlineData(0.87f)]
    [InlineData(1.0f)]
    [InlineData(1.13f)]
    public void Elliptical_MatchesTheIndependentGaussian(float centre)
    {
        // 2×1 [0 | 255] sampled at an arbitrary areal source centre. The destination centre (0.5)
        // is steered there with a translation whose magnitude is 0.5 - centre; the expected byte
        // comes from an independent double-precision Gaussian recomputed below (not from the
        // library), then the f32 and round-half-up truncation the frozen contract documents.
        using var source = Image.Create(2, 1, WorkingFormat.Rgba8);
        source.WritablePixels[0] = 0;
        source.WritablePixels[1] = 0;
        source.WritablePixels[2] = 0;
        source.WritablePixels[3] = 255;
        source.WritablePixels[4] = 255;
        source.WritablePixels[5] = 255;
        source.WritablePixels[6] = 255;
        source.WritablePixels[7] = 255;

        using var result = source.AffineWithOptions(AffineMatrix.Translation(0.5f - centre, 0f), 1, 1,
            Resampler.Bilinear,
            new SampleOptions { AaMode = SampleAaMode.Elliptical, Domain = SampleDomain.LinearLight });

        var expected = IndependentEllipticalByte(centre);
        result.WritablePixels[0].ShouldBe(expected,
            $"centre {centre}: the linear-domain elliptical average must match the independent Gaussian");
    }

    [Fact]
    public void Elliptical_Minification_SpreadsASinglePixelLine()
    {
        // A 1-pixel white row at y = 8 in a 16×16 black image, minified 4×. A footprint filter must
        // spread that line over the whole destination column: at 0.25× the correct footprint is
        // σ = 4 source pixels, so every one of the four rows keeps part of the energy. Legacy bilinear
        // samples 2×2 around source row 10 and misses the line entirely — that is the aliasing this
        // test exists to catch, and the inverted (pre-fix) kernel missed it too.
        using var source = Image.Create(16, 16, WorkingFormat.Rgba8);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var o = (y * 16 + x) * 4;
                var v = (byte)(y == 8 ? 255 : 0);
                source.WritablePixels[o] = v;
                source.WritablePixels[o + 1] = v;
                source.WritablePixels[o + 2] = v;
                source.WritablePixels[o + 3] = 255;
            }
        }

        var forward = AffineMatrix.Scale(0.25f, 0.25f);
        using var ewa = source.AffineWithOptions(forward, 4, 4, Resampler.Bilinear,
            new SampleOptions { AaMode = SampleAaMode.Elliptical });
        using var plain = source.Affine(forward, 4, 4);

        var ewaEnergy = 0;
        for (var y = 0; y < 4; y++)
        {
            var v = ewa.WritablePixels[y * 4 * 4];
            (v >= 1 && v <= 120).ShouldBeTrue(
                $"EWA row {y} = {v}: the line's energy must spread over the footprint, not be sampled");
            ewaEnergy += v;
        }

        // Energy check: four destination rows average sixteen source rows, so the column sum is about
        // 4 × 255/16 ≈ 64. A point sample would put ~255 in one row and 0 in the rest.
        (ewaEnergy >= 20 && ewaEnergy <= 140).ShouldBeTrue($"EWA column energy {ewaEnergy} is out of range");

        var plainMax = 0;
        for (var y = 0; y < 4; y++)
            plainMax = Math.Max(plainMax, plain.WritablePixels[y * 4 * 4]);
        plainMax.ShouldBeLessThanOrEqualTo(1, "legacy bilinear misses the line at this phase");
    }

    [Fact]
    public void Elliptical_BudgetEdge_KeepsTheWeightBufferInBounds()
    {
        // J = 32·I: |det| = 1024, σ = 32, so the 2σ box is clipped by the 64-tap budget's half-width
        // and a half-integer centre reaches the 65-tap upper bound. A buffer sized maxTaps² threw
        // IndexOutOfRangeException here; the contract is (maxTaps + 1)².
        var weights = new double[65 * 65];
        var plan = EwaKernel.Build(new SourceJacobian(32, 0, 0, 32), 32.5, 32.5,
            128, 128, 64, weights);

        plan.Status.ShouldBe(EwaStatus.Ok);
        (plan.CountX <= 65 && plan.CountY <= 65).ShouldBeTrue($"{plan.CountX}×{plan.CountY} exceeds the budget");

        var sum = 0.0;
        for (var i = 0; i < plan.CountX * plan.CountY; i++)
            sum += weights[i];
        sum.ShouldBe(1.0, 1e-12, "the double-normalised weights must sum to one");
    }

    [Theory]
    [InlineData(0.5, 8)]
    [InlineData(1.0, 8)]
    [InlineData(2.0, 16)]
    [InlineData(16.0, 32)]
    [InlineData(64.0, 64)]
    public void AdaptiveAa_TiersGrowWithTheMinificationFactor(double detJ, int expectedTaps)
    {
        AdaptiveAa.Evaluate(detJ).ShouldBe(expectedTaps);
        AdaptiveAa.Evaluate(-detJ).ShouldBe(expectedTaps, "a mirrored Jacobian has the same area");
    }

    [Fact]
    public void DegenerateJacobian_ReportsIsotropicStatusAndFallsBack()
    {
        using var source = Image.Create(4, 4, WorkingFormat.Rgba8);
        Fill(source);

        Span<float> sample = stackalloc float[4];
        Span<float> background = stackalloc float[4];
        background.Clear();

        // |det J| = 1e-9 is below the registered degenerate threshold.
        var collapsed = new SourceJacobian(1e-9, 0.0, 0.0, 1.0);
        SamplingCore.SampleWithOptions(source[0].Pixels.View, 1.5f, 1.5f, Resampler.Bilinear,
            new SampleOptions { AaMode = SampleAaMode.Elliptical }, background, collapsed, out var status, sample);

        status.ShouldBe(EwaStatus.DegenerateIsotropic);

        // The fallback result must equal the isotropic plan-injecting path on the same point.
        var planX = TapPlanBuilder.ForPoint(1.5, 4, Resampler.Bilinear);
        var planY = TapPlanBuilder.ForPoint(1.5, 4, Resampler.Bilinear);
        Span<float> isotropic = stackalloc float[4];
        SamplingCore.SampleWithOptions(source[0].Pixels.View, planX, planY, default, background, isotropic);

        sample.ToArray().ShouldBe(isotropic.ToArray(), "the degenerate fallback must be the isotropic path");
    }

    [Fact]
    public void Nearest_WithElliptical_IsRejectedLoudly()
    {
        using var source = Image.Create(2, 2, WorkingFormat.Rgba8);
        Fill(source);

        Span<float> sample = stackalloc float[4];
        Span<float> background = stackalloc float[4];
        var thrown = false;
        try
        {
            SamplingCore.SampleWithOptions(
                source[0].Pixels.View, 1.0f, 1.0f, Resampler.Nearest,
                new SampleOptions { AaMode = SampleAaMode.Elliptical }, background,
                new SourceJacobian(1, 0, 0, 1), out _, sample);
        }
        catch (NotSupportedException)
        {
            thrown = true;
        }

        thrown.ShouldBeTrue("Nearest has no kernel to adapt and must fail loudly");
    }

    [Fact]
    public void WarpWithOptions_WithElliptical_IsRejectedLoudly()
    {
        using var source = Image.Create(4, 4, WorkingFormat.Rgba8);
        Fill(source);

        Should.Throw<NotSupportedException>(() => source.WarpWithOptions(4, 4,
            (float x, float y, out float sx, out float sy) => { sx = x; sy = y; },
            Resampler.Bilinear, new SampleOptions { AaMode = SampleAaMode.Elliptical }));
    }

    [Fact]
    public void StrongPerspective_DoesNotExplodeAtTheFarBand()
    {
        // A homography whose inverse w crosses zero inside the canvas: the far band degenerates
        // (isotropic fallback) or clips, but no NaN, no throw, every pixel written.
        using var source = Image.Create(8, 5, WorkingFormat.Rgba32F);
        Fill(source);

        var homography = new float[] { 1f, 0f, 0f, 0f, 1f, 0f, 0.6f, 0f };
        using var warped = source.PerspectiveWithOptions(homography, 8, 5, Resampler.Bilinear,
            new SampleOptions { AaMode = SampleAaMode.Elliptical });

        warped.WritablePixels.Length.ShouldBe(8 * 5 * 16);
        var floats = MemoryMarshal.Cast<byte, float>(warped.WritablePixels);
        foreach (var f in floats)
            float.IsNaN(f).ShouldBeFalse("a degenerate footprint must never leak NaN into the output");
    }

    [Fact]
    public void Elliptical_WithLinearLight_RoundTripsTheDomainWithoutClamping()
    {
        using var source = Image.Create(1, 1, WorkingFormat.Rgba32F);
        var floats = MemoryMarshal.Cast<byte, float>(source.WritablePixels);
        floats[0] = 4f;   // HDR
        floats[1] = 0.25f;
        floats[2] = 0.5f;
        floats[3] = 1f;

        using var result = source.AffineWithOptions(AffineMatrix.Identity, 1, 1, Resampler.Bilinear,
            new SampleOptions { AaMode = SampleAaMode.Elliptical, Domain = SampleDomain.LinearLight });

        var outFloats = MemoryMarshal.Cast<byte, float>(result.WritablePixels);
        // The footprint is a single pixel (1×1 source, identity): the value must survive the
        // toLinear → fromLinear round trip exactly as SampleTransfer defines it (its round-trip
        // precision at HDR magnitudes is ±1 ULP by contract, not bit-exact).
        outFloats[0].ShouldBe(SampleTransfer.FromLinear(SampleTransfer.ToLinear(4f, SampleTransferCurve.Srgb), SampleTransferCurve.Srgb));
        outFloats[1].ShouldBe(SampleTransfer.FromLinear(SampleTransfer.ToLinear(0.25f, SampleTransferCurve.Srgb), SampleTransferCurve.Srgb));
        outFloats[2].ShouldBe(SampleTransfer.FromLinear(SampleTransfer.ToLinear(0.5f, SampleTransferCurve.Srgb), SampleTransferCurve.Srgb));
        outFloats[3].ShouldBe(1f);
    }

    // ------------------------------------------------- executed reference vectors

    [Theory]
    [InlineData("ewa.identity", 1f)]     // identity forward, 2×1 source
    [InlineData("ewa.minify", 0.5f)]     // Scale(0.5) forward, 2×2 source
    public void Vectors_ExecuteByteExactly(string caseId, float forwardScale)
    {
        var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(VectorPath(caseId + ".json")));
        var root = document.RootElement;
        var expectedBytes = Convert.FromHexString(root.GetProperty("expected").GetProperty("bytes").GetString()!);

        var width = root.GetProperty("input").GetProperty("width").GetInt32();
        var height = root.GetProperty("input").GetProperty("height").GetInt32();
        using var source = Image.Create(width, height, WorkingFormat.Rgba8);
        Convert.FromHexString(root.GetProperty("input").GetProperty("bytes").GetString()!)
            .CopyTo(source.WritablePixels);

        using var result = source.AffineWithOptions(AffineMatrix.Scale(forwardScale, forwardScale),
            1, 1, Resampler.Bilinear,
            new SampleOptions { AaMode = SampleAaMode.Elliptical });

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

    // ------------------------------------------------- independent oracle

    /// <summary>An independent double-precision elliptical Gaussian for the 2×1 black/white source,
    /// linear-light domain: decode both pixels, weight with exp(-0.5·q), encode the average, then
    /// apply the documented f32 + round-half-up byte discipline. Shares no code with the library.</summary>
    private static byte IndependentEllipticalByte(float centre)
    {
        const double pixel0Linear = 0.0;
        const double pixel1Linear = 1.0;

        // Weights: taps within one source pixel of the areal centre, q = dx² (identity Jacobian).
        var sum = 0.0;
        var acc = 0.0;
        for (var x = 0; x <= 1; x++)
        {
            var d = x + 0.5 - centre;
            if (Math.Abs(d) > 1.0) continue;
            var w = Math.Exp(-0.5 * d * d);
            acc += (x == 0 ? pixel0Linear : pixel1Linear) * w;
            sum += w;
        }

        var linearAverage = acc / sum;
        var encoded = SrgbEncode(linearAverage);
        var f32 = (float)encoded;
        var scaled = (float)(f32 * 255f) + 0.5f;
        return (byte)scaled;
    }

    private static double SrgbEncode(double linear) =>
        linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;

    private static void Fill(Image image)
    {
        var pixels = image.WritablePixels;
        if (image.Format == WorkingFormat.Rgba32F)
        {
            // Float buffers must receive real floats: writing byte patterns into the buffer would
            // mint arbitrary bit patterns, including NaNs the sampling path would faithfully carry.
            var floats = MemoryMarshal.Cast<byte, float>(pixels);
            for (var p = 0; p < floats.Length / 4; p++)
            {
                floats[p * 4] = (p % 7) / 3f;
                floats[p * 4 + 1] = (p % 5) / 4f;
                floats[p * 4 + 2] = (p % 11) / 8f;
                floats[p * 4 + 3] = 1f;
            }
            return;
        }

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var p = i / 4;
            pixels[i] = (byte)(p * 31 + 7);
            pixels[i + 1] = (byte)(p * 17 + 3);
            pixels[i + 2] = (byte)(p * 13 + 1);
            pixels[i + 3] = 255;
        }
    }
}
