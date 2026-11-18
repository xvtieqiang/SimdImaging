using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// M12 Richardson–Lucy. The interesting assertions are not "it ran": they are that the update actually
/// reverses a known blur (measured in PSNR against the untouched original), that it never touches alpha, that a
/// float image above 1 stays above 1, and that the vector and scalar convolution paths agree byte for byte.
/// </summary>
public class RichardsonLucyTests
{
    [Theory]
    [InlineData(WorkingFormat.Rgba8)]
    [InlineData(WorkingFormat.Rgba16)]
    [InlineData(WorkingFormat.Rgba32F)]
    public void ZeroIterations_IsACopy(WorkingFormat format)
    {
        using var image = Bars(23, 17, format);
        var before = image.WritablePixels.ToArray();

        using var result = image.Pipe().RichardsonLucy(1.4f, 0).ToImage();

        result.WritablePixels.ToArray().ShouldBe(before);
    }

    [Fact]
    public void GaussianDeconvolution_RecoversMoreDetailThanTheBlurLeft()
    {
        using var sharp = Bars(96, 64, WorkingFormat.Rgba8);
        using var blurred = sharp.Pipe().GaussianBlur(1.6f).ToImage();
        using var restored = blurred.Pipe().RichardsonLucy(1.6f, 12).ToImage();

        var blurPsnr = AnalysisOps.Psnr(View(sharp), View(blurred));
        var restorePsnr = AnalysisOps.Psnr(View(sharp), View(restored));

        restorePsnr.ShouldBeGreaterThan(blurPsnr + 2.0,
            $"blur {blurPsnr:F2} dB -> restored {restorePsnr:F2} dB");
    }

    [Fact]
    public void MotionDeconvolution_RecoversAHorizontalTrail()
    {
        using var sharp = Bars(96, 64, WorkingFormat.Rgba8);
        using var blurred = sharp.Pipe().MotionBlur(9, 0f).ToImage();
        using var restored = blurred.Pipe().RichardsonLucyMotion(9, 0f, 12).ToImage();

        var blurPsnr = AnalysisOps.Psnr(View(sharp), View(blurred));
        var restorePsnr = AnalysisOps.Psnr(View(sharp), View(restored));

        restorePsnr.ShouldBeGreaterThan(blurPsnr + 2.5,
            $"trail {blurPsnr:F2} dB -> restored {restorePsnr:F2} dB");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    public void ADegenerateMotionTrail_IsTheIdentity(float angle)
    {
        using var image = Bars(19, 13, WorkingFormat.Rgba8);
        var before = image.WritablePixels.ToArray();

        using var result = image.Pipe().RichardsonLucyMotion(1, angle, 8).ToImage();

        result.WritablePixels.ToArray().ShouldBe(before);
    }

    [Fact]
    public void AlphaIsCopiedNotDeconvolved()
    {
        using var image = Bars(31, 21, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var i = 0; i < pixels.Length; i += 4)
            pixels[i + 3] = (byte)((i / 4 * 37) & 0xFF);

        var before = image.WritablePixels.ToArray();
        using var result = image.Pipe().RichardsonLucy(1.1f, 6).ToImage();
        var after = result.WritablePixels.ToArray();

        for (var i = 3; i < after.Length; i += 4)
            after[i].ShouldBe(before[i], $"alpha at byte {i}");
    }

    [Fact]
    public void FloatAboveOneIsNotClamped()
    {
        using var image = Image.Create(24, 16, WorkingFormat.Rgba32F);
        var pixels = image.WritablePixels;
        for (var i = 0; i < pixels.Length; i += 16)
        {
            BitConverter.TryWriteBytes(pixels.Slice(i, 4), 4.0f);
            BitConverter.TryWriteBytes(pixels.Slice(i + 4, 4), 4.0f);
            BitConverter.TryWriteBytes(pixels.Slice(i + 8, 4), 4.0f);
            BitConverter.TryWriteBytes(pixels.Slice(i + 12, 4), 1.0f);
        }

        using var result = image.Pipe().RichardsonLucy(1.2f, 4).ToImage();

        var span = result.WritablePixels;
        var value = BitConverter.ToSingle(span.Slice((8 * 24 + 8) * 4, 4));
        value.ShouldBeGreaterThan(3.9f, "a four-times-over-range image must survive deconvolution");
    }

    [Fact]
    public void ScalarAndVectorPathsAgreeByteForByte()
    {
        using var image = Noise(37, 29, WorkingFormat.Rgba8);
        using var vector = image.Pipe().RichardsonLucy(1.3f, 4).ToImage();
        var expected = vector.WritablePixels.ToArray();

        PlanarConvolve.ForceScalar = true;
        try
        {
            using var scalar = image.Pipe().RichardsonLucy(1.3f, 4).ToImage();
            scalar.WritablePixels.ToArray().ShouldBe(expected);
        }
        finally
        {
            PlanarConvolve.ForceScalar = false;
        }
    }

    [Fact]
    public void MotionAndGaussianPathsBothAgreeWithTheScalarTwin()
    {
        using var image = Noise(29, 23, WorkingFormat.Rgba16);
        using var vector = image.Pipe().RichardsonLucyMotion(7, 33f, 3).ToImage();
        var expected = vector.WritablePixels.ToArray();

        PlanarConvolve.ForceScalar = true;
        try
        {
            using var scalar = image.Pipe().RichardsonLucyMotion(7, 33f, 3).ToImage();
            scalar.WritablePixels.ToArray().ShouldBe(expected);
        }
        finally
        {
            PlanarConvolve.ForceScalar = false;
        }
    }

    [Fact]
    public void PatternIsStableAcrossWorkerCounts()
    {
        using var image = Bars(41, 33, WorkingFormat.Rgba8);
        using var reference = image.Pipe().RichardsonLucy(1.5f, 5).ToImage();
        var expected = reference.WritablePixels.ToArray();

        foreach (var degree in new[] { 1, 2, 16 })
        {
            RowParallel.DegreeOverride = degree;
            try
            {
                using var result = image.Pipe().RichardsonLucy(1.5f, 5).ToImage();
                result.WritablePixels.ToArray().ShouldBe(expected, $"degree {degree}");
            }
            finally
            {
                RowParallel.DegreeOverride = 0;
            }
        }
    }

    /// <summary>
    /// The declared halo must be the real support. A band executor feeds the operator a window and keeps only
    /// the inner rows, so a halo one pixel short would leave a seam of slightly different pixels at every band
    /// boundary — invisible to a fused-versus-step-by-step comparison, which uses the same windows on both sides.
    /// Comparing against the whole-image call is what actually pins it.
    /// </summary>
    [Fact]
    public void RunningAsAPlanStep_MatchesTheWholeImageCall()
    {
        using var image = Noise(29, 23, WorkingFormat.Rgba8);
        using var direct = image.Pipe().RichardsonLucy(1.2f, 3).ToImage();

        const float sigma = 1.2f;
        var halo = RichardsonLucyOps.RequiredHalo(sigma, 3);
        var plan = new ProcessingPlan { BandRows = 5 }
            .Add("rl", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => RichardsonLucyOps.RichardsonLucy(v, sigma, 3), halo, halo), halo, halo);

        using var planned = image.Apply(plan);

        planned.WritablePixels.ToArray().ShouldBe(direct.WritablePixels.ToArray());
    }

    [Fact]
    public void InvalidArgumentsThrow()
    {
        using var image = Bars(9, 9, WorkingFormat.Rgba8);
        var view = View(image);

        Should.Throw<ArgumentOutOfRangeException>(() => RichardsonLucyOps.RichardsonLucy(view, 0f));
        Should.Throw<ArgumentOutOfRangeException>(() => RichardsonLucyOps.RichardsonLucy(view, float.NaN));
        Should.Throw<ArgumentOutOfRangeException>(() => RichardsonLucyOps.RichardsonLucy(view, 1f, -1));
        Should.Throw<ArgumentOutOfRangeException>(() => RichardsonLucyOps.RichardsonLucyMotion(view, 0, 0f));
        Should.Throw<ArgumentOutOfRangeException>(() => RichardsonLucyOps.RichardsonLucyMotion(view, 5, float.NaN));
        Should.Throw<ArgumentOutOfRangeException>(() => RichardsonLucyOps.RichardsonLucyMotion(view, 5, 0f, -1));
    }

    // ------------------------------------------------------------------ helpers

    private static PixelView View(Image image) => image[0].Pixels.View;

    /// <summary>Vertical three-pixel bars over a diagonal texture: detail at two scales, no flat region that
    /// would hide a failed update.</summary>
    private static Image Bars(int width, int height, WorkingFormat format)
    {
        var image = Image.Create(width, height, format);
        var pixels = image.WritablePixels;
        var bytesPerPixel = format.BytesPerPixel();

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (x / 3) % 2 == 0 ? 232 : 24;
            if (y * 3 > height * 2)
                value = (x + y) % 7 < 3 ? 196 : 48;

            var offset = (y * width + x) * bytesPerPixel;
            if (format == WorkingFormat.Rgba32F)
            {
                BitConverter.TryWriteBytes(pixels.Slice(offset, 4), value / 255f);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 4, 4), value / 255f);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 8, 4), value / 255f);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 12, 4), 1f);
            }
            else if (format == WorkingFormat.Rgba16)
            {
                var sample = (ushort)(value * 257);
                BitConverter.TryWriteBytes(pixels.Slice(offset, 2), sample);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 2, 2), sample);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 4, 2), sample);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 6, 2), ushort.MaxValue);
            }
            else
            {
                pixels[offset] = (byte)value;
                pixels[offset + 1] = (byte)value;
                pixels[offset + 2] = (byte)value;
                pixels[offset + 3] = 255;
            }
        }

        return image;
    }

    private static Image Noise(int width, int height, WorkingFormat format)
    {
        var image = Image.Create(width, height, format);
        var pixels = image.WritablePixels;
        var random = new Random(90210);
        var bytesPerPixel = format.BytesPerPixel();

        for (var i = 0; i < width * height; i++)
        {
            var offset = i * bytesPerPixel;
            for (var c = 0; c < 3; c++)
            {
                var value = random.Next(0, 256);
                if (format == WorkingFormat.Rgba32F)
                    BitConverter.TryWriteBytes(pixels.Slice(offset + c * 4, 4), value / 255f);
                else if (format == WorkingFormat.Rgba16)
                    BitConverter.TryWriteBytes(pixels.Slice(offset + c * 2, 2), (ushort)(value * 257));
                else
                    pixels[offset + c] = (byte)value;
            }

            if (format == WorkingFormat.Rgba32F)
                BitConverter.TryWriteBytes(pixels.Slice(offset + 12, 4), 1f);
            else if (format == WorkingFormat.Rgba16)
                BitConverter.TryWriteBytes(pixels.Slice(offset + 6, 2), ushort.MaxValue);
            else
                pixels[offset + 3] = 255;
        }

        return image;
    }
}
