using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// A plan that ends with a size change: the same pixels as running the steps and resizing afterwards, in the whole
/// image path and in the streamed one.
/// </summary>
public class PlanResizeTests
{
    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), $"simd-planresize-{Guid.NewGuid():N}{extension}");

    private static ProcessingPlan Chain() =>
        new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Contrast(1.1f)).Resize(97, 61);

    [Fact]
    public void ApplyWithResize_MatchesStepsThenResize()
    {
        using var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8);
        var plan = Chain();

        using var actual = image.Apply(plan);

        using var filtered = image.Apply(new ProcessingPlan().Add(PlanOps.GaussianBlur(1f))
            .Add(PlanOps.Contrast(1.1f)));
        using var expected = filtered.Pipe().Resize(97, 61, Resampler.Bicubic).ToImage();

        actual.Width.ShouldBe(97);
        actual.Height.ShouldBe(61);
        actual.WritablePixels.ToArray().ShouldBe(expected.WritablePixels.ToArray());
    }

    [Fact]
    public void TranscodeWithResize_MatchesTheWholeChain()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var report = ImageTranscode.From(source).GaussianBlur(1f).Contrast(1.1f).Resize(97, 61)
                .Run(streamed);

            using (var loaded = Image.Load(source))
            using (var filtered = loaded.Apply(new ProcessingPlan().Add(PlanOps.GaussianBlur(1f))
                .Add(PlanOps.Contrast(1.1f))))
            using (var resized = filtered.Pipe().Resize(97, 61, Resampler.Bicubic).ToImage())
                Image.Save(resized, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
            report.Width.ShouldBe(97);
            report.Height.ShouldBe(61);
            report.Mode.ShouldBe(ProcessMode.Streamed);
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void APlanWithResizeOnly_IsAPlainResize()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba16);
        var plan = new ProcessingPlan().Resize(31, 23, Resampler.Lanczos3);

        using var actual = image.Apply(plan);
        using var expected = image.Pipe().Resize(31, 23, Resampler.Lanczos3).ToImage();

        actual.Format.ShouldBe(WorkingFormat.Rgba16);
        actual.WritablePixels.ToArray().ShouldBe(expected.WritablePixels.ToArray());
    }

    [Fact]
    public void StepsAfterAResize_AreRefused()
    {
        var plan = new ProcessingPlan().Resize(10, 10);
        Should.Throw<InvalidOperationException>(() => plan.Add(PlanOps.Invert()));
        Should.Throw<InvalidOperationException>(() => plan.AddPointLut("lut", new byte[4 * 256]));
        Should.Throw<InvalidOperationException>(() => plan.Resize(5, 5));
    }

    [Fact]
    public void ARegionRun_RefusesAPlanWithResize()
    {
        var path = Temp(".tif");
        try
        {
            File.WriteAllBytes(path, TiffFixture.Strips(32, 24, PixelFormat.Rgb24, new byte[32 * 24 * 3]));
            var plan = new ProcessingPlan().Add(PlanOps.Invert()).Resize(16, 12);

            plan.CanExecuteRegion().ShouldBe(RegionRefusal.GeometricStep);
            Should.Throw<NotSupportedException>(() =>
                Image.ProcessRegion(path, plan, new PixelRect(0, 0, 32, 24)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
