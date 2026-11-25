using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// The file-side chain is now one recipe compiled by the same compiler as the in-memory pipeline. These tests pin the
/// two facts that matter for a file: the order of geometry is the order the caller wrote, and a step the compiler
/// refuses still materialises with the reason it always had.
/// </summary>
public sealed class TranscodeRecipeTests
{
    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), $"simd-recipe-{Guid.NewGuid():N}{extension}");

    [Fact]
    public void CropAfterResize_CropsTheResizedImage()
    {
        var source = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(120, 80, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var rect = new PixelRect(10, 5, 20, 20);
            using var output = new MemoryStream();
            ImageTranscode.From(source).Resize(60, 40).Crop(rect).Run(output, ImageFormat.Png);

            output.Position = 0;
            using var actual = Image.Load(output);
            using var loaded = Image.Load(source);
            using var resized = Canvas.Resize(loaded[0].Pixels.View, 60, 40, Resampler.Bicubic, ResizeMode.Stretch,
                Anchor.Centre, default);
            using var cropped = Transforms.Crop(resized.View, rect);

            actual.Width.ShouldBe(20);
            actual.Height.ShouldBe(20);
            Rows(actual[0].Pixels.View).ShouldBe(Rows(cropped.View));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void OperatorsAfterResize_RunAtTheNewSize()
    {
        var source = Temp(".png");
        var destination = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(120, 80, WorkingFormat.Rgba8))
                Image.Save(image, source);

            ImageTranscode.From(source).Resize(60, 40).GaussianBlur(1f).Contrast(1.1f).Save(destination);

            using (var loaded = Image.Load(source))
            using (var resized = Canvas.Resize(loaded[0].Pixels.View, 60, 40, Resampler.Bicubic, ResizeMode.Stretch,
                       Anchor.Centre, default))
            using (var blurred = FilterOps.GaussianBlur(resized.View, 1f))
            using (var contrasted = PointOps.Contrast(blurred.View, 1.1f))
            using (var reference = Image.Create(60, 40, WorkingFormat.Rgba8))
            {
                for (var y = 0; y < 40; y++)
                    contrasted.View.Row(y).CopyTo(reference[0].Pixels.View.Row(y));
                Image.Save(reference, expected);
            }

            File.ReadAllBytes(destination).ShouldBe(File.ReadAllBytes(expected));
        }
        finally
        {
            foreach (var path in new[] { source, destination, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void AWholeSourceStep_Materialises_WithTheReasonItAlwaysHad()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(96, 72, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var plan = new ProcessingPlan().Add(
                PlanOps.Convolve([0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f], 1, BorderMode.Mirror));
            var report = ImageTranscode.From(source).Convolve([0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f], 1,
                BorderMode.Mirror).Run(streamed);

            Image.ProcessTo(source, plan, expected);

            report.Mode.ShouldBe(ProcessMode.Materialized);
            report.Refusal.ShouldBe(StreamRefusal.StepNeedsWholeSource);
            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void AGlobalStep_OverAStreamSource_MaterialisesOnce()
    {
        var source = Temp(".png");
        var destination = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8))
                Image.Save(image, source);

            using var input = File.OpenRead(source);
            var report = ImageTranscode.From(input).Clahe().Run(destination);

            report.Mode.ShouldBe(ProcessMode.Materialized);
            report.Refusal.ShouldBe(StreamRefusal.GlobalStep);
            File.Exists(destination).ShouldBeTrue();
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public void RegionWithStepsAfterTheResize_IsRefusedInsteadOfSilentlyDropped()
    {
        var source = Temp(".tif");
        try
        {
            File.WriteAllBytes(source, TiffFixture.Strips(64, 48, PixelFormat.Rgb24, TestPattern(64, 48, 3)));

            Should.Throw<NotSupportedException>(() =>
                ImageTranscode.From(source).Region(new PixelRect(8, 6, 32, 24)).Resize(16, 12).Contrast(1.1f)
                    .ToImage());
        }
        finally
        {
            File.Delete(source);
        }
    }

    private static byte[] TestPattern(int width, int height, int channels)
    {
        var bytes = new byte[width * height * channels];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((i * 7) % 251);
        return bytes;
    }

    private static byte[] Rows(PixelView view)
    {
        var bytes = new byte[view.Height * view.RowBytes];
        for (var y = 0; y < view.Height; y++)
            view.Row(y).CopyTo(bytes.AsSpan(y * view.RowBytes, view.RowBytes));
        return bytes;
    }
}
