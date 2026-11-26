using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// The streaming resize: a resize that never holds the image, against <c>Load → Resize → Save</c>.
/// </summary>
public class TranscodeResizeTests
{
    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), $"simd-transcoderesize-{Guid.NewGuid():N}{extension}");

    [Theory]
    [InlineData("Nearest")]
    [InlineData("Bilinear")]
    [InlineData("Bicubic")]
    [InlineData("Lanczos3")]
    [InlineData("Box")]
    public void Rgba8_MatchesLoadResize(string filter)
    {
        var resampler = filter switch
        {
            "Nearest" => Resampler.Nearest,
            "Bilinear" => Resampler.Bilinear,
            "Bicubic" => Resampler.Bicubic,
            "Lanczos3" => Resampler.Lanczos3,
            _ => Resampler.Box,
        };

        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var report = ImageTranscode.From(source).Resize(97, 61, resampler).Run(streamed);

            using (var loaded = Image.Load(source))
            using (var resized = loaded.Pipe().Resize(97, 61, resampler).ToImage())
                Image.Save(resized, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected), filter);
            report.Mode.ShouldBe(ProcessMode.Streamed);
            report.Width.ShouldBe(97);
            report.Height.ShouldBe(61);
            report.RowsWritten.ShouldBe(61);
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void Rgba16_MatchesLoadResize()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba16))
                Image.Save(image, source);

            ImageTranscode.From(source).Resize(31, 23, Resampler.Bicubic).Run(streamed);

            using (var loaded = Image.Load(source))
            using (var resized = loaded.Pipe().Resize(31, 23, Resampler.Bicubic).ToImage())
                Image.Save(resized, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void APlanPrefix_RunsInTheSamePass()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var plan = new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Contrast(1.1f));
            var report = ImageTranscode.From(source).Append(plan).Resize(97, 61, Resampler.Bicubic).Run(streamed);

            using (var loaded = Image.Load(source))
            using (var filtered = loaded.Apply(plan))
            using (var resized = filtered.Pipe().Resize(97, 61, Resampler.Bicubic).ToImage())
                Image.Save(resized, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
            report.Mode.ShouldBe(ProcessMode.Streamed);
            report.Width.ShouldBe(97);
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void AnAfterPlan_RunsOnTheResizedRows()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var before = new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Contrast(1.1f));
            var after = new ProcessingPlan().Add(PlanOps.Sharpen(0.5f)).Add(PlanOps.Brightness(0.02f));

            var report = ImageTranscode.From(source).Append(before).Resize(97, 61, Resampler.Bicubic).Append(after)
                .Run(streamed);

            using (var loaded = Image.Load(source))
            using (var filtered = loaded.Apply(before))
            using (var resized = filtered.Pipe().Resize(97, 61, Resampler.Bicubic).ToImage())
            using (var sharpened = resized.Apply(after))
                Image.Save(sharpened, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
            report.Width.ShouldBe(97);
            report.Height.ShouldBe(61);
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void APlanThatCannotStream_IsMaterialisedThenResized()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var plan = new ProcessingPlan().Add(PlanOps.Equalize());
            var report = ImageTranscode.From(source).Append(plan).Resize(31, 23).Run(streamed);

            using (var loaded = Image.Load(source))
            using (var filtered = loaded.Apply(plan))
            using (var resized = filtered.Pipe().Resize(31, 23).ToImage())
                Image.Save(resized, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
            report.Mode.ShouldBe(ProcessMode.Materialized);
            report.Refusal.ShouldBe(StreamRefusal.GlobalStep);
            report.Height.ShouldBe(23);
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void AnUpscale_MatchesLoadResize()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(40, 30, WorkingFormat.Rgba8))
                Image.Save(image, source);

            ImageTranscode.From(source).Resize(157, 91, Resampler.Bilinear).Run(streamed);

            using (var loaded = Image.Load(source))
            using (var resized = loaded.Pipe().Resize(157, 91, Resampler.Bilinear).ToImage())
                Image.Save(resized, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }
}
