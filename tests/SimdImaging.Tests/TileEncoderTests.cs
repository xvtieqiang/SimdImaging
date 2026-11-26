using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The encoder-side <see cref="IRegionSink"/>: tiles in, encoded rows out, with no whole-image buffer. The oracle is
/// the buffer tile path, so the test compares bytes rather than "looks the same".
/// </summary>
public sealed class TileEncoderTests
{
    [Fact]
    public void OpenRegionSource_FeedsTheTilePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"simd-tile-{Guid.NewGuid():N}.tif");
        try
        {
            File.WriteAllBytes(path, TiffFixture.Strips(41, 29, PixelFormat.Rgb24, Pattern(41, 29)));
            var plan = new ProcessingPlan().Add(PlanOps.Invert());

            using var source = Image.OpenRegionSource(path);
            source.Width.ShouldBe(41);
            source.Height.ShouldBe(29);
            source.Format.ShouldBe(WorkingFormat.Rgba8);

            using var tiled = Image.ProcessTiles(source, plan, 16, 12);
            using var loaded = Image.Load(path);
            using var expected = loaded.Pipe().Invert().ToImage();

            tiled.View.Span.ToArray().ShouldBe(expected.WritablePixels.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProcessTilesTo_MatchesTheBufferTilePath()
    {
        using var image = TestImages.Gradient(37, 23, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Invert());
        using var expected = Image.ProcessTiles(new ViewRegionSource(image[0].Pixels.View), plan, 16, 12);

        using var encoded = new MemoryStream();
        var report = Image.ProcessTilesTo(new ViewRegionSource(image[0].Pixels.View), encoded, ImageFormat.Png,
            plan, 16, 12);

        report.Mode.ShouldBe(ProcessMode.Tiled);
        report.RowsWritten.ShouldBe(23);
        report.Width.ShouldBe(37);
        report.Height.ShouldBe(23);
        report.PeakWorkingBytes.ShouldBeGreaterThan(0);
        report.PredictedWorkingBytes.ShouldBeGreaterThan(0);
        report.PeakWorkingBytes.ShouldBeLessThanOrEqualTo(report.PredictedWorkingBytes,
            "the tile run's region windows and the encoder band are part of the compiled bound");

        encoded.Position = 0;
        using var decoded = Image.Load(encoded);
        decoded.WritablePixels.ToArray().ShouldBe(expected.View.Span.ToArray());
    }


    [Fact]
    public void ProcessTilesTo_WorkspaceRefusal_DoesNotWriteToTheDestination()
    {
        using var image = TestImages.Gradient(37, 23, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Invert());
        var manager = new WorkspaceManager(limitBytes: 1);
        using var encoded = new MemoryStream();

        Should.Throw<InvalidOperationException>(() => Image.ProcessTilesTo(
            new ViewRegionSource(image[0].Pixels.View), encoded, ImageFormat.Png,
            plan, 16, 12, new ExecuteOptions { Workspace = manager }));

        encoded.Length.ShouldBe(0,
            "tile execution and encoder-band reservations must succeed before the encoder writes its header");
        manager.OutstandingBytes.ShouldBe(0);
    }

    [Fact]
    public void ProcessTilesTo_FromATiff_MatchesLoadApply()
    {
        var source = Path.Combine(Path.GetTempPath(), $"simd-tile-{Guid.NewGuid():N}.tif");
        var destination = Path.Combine(Path.GetTempPath(), $"simd-tile-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(source, TiffFixture.Strips(41, 29, PixelFormat.Rgb24, Pattern(41, 29)));

            var plan = new ProcessingPlan().Add(PlanOps.Invert());
            using (var loaded = Image.Load(source))
            using (var expected = loaded.Pipe().Invert().ToImage())
            {
                var report = Image.ProcessTilesTo(source, destination, plan, 16, 12);

                report.Mode.ShouldBe(ProcessMode.Tiled);
                using var actual = Image.Load(destination);
                actual.Width.ShouldBe(expected.Width);
                actual.Height.ShouldBe(expected.Height);
                actual.WritablePixels.ToArray().ShouldBe(expected.WritablePixels.ToArray());
            }
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }


    [Fact]
    public void ProcessTilesTo_PathBudgetRefusal_PreservesExistingDestination()
    {
        var source = Path.Combine(Path.GetTempPath(), $"simd-tile-{Guid.NewGuid():N}.tif");
        var destination = Path.Combine(Path.GetTempPath(), $"simd-tile-{Guid.NewGuid():N}.png");
        var original = new byte[] { 5, 13, 17, 29 };
        try
        {
            File.WriteAllBytes(source, TiffFixture.Strips(37, 23, PixelFormat.Rgb24, Pattern(37, 23)));
            File.WriteAllBytes(destination, original);
            using var region = Image.OpenRegionSource(source);
            var plan = new ProcessingPlan().Add(PlanOps.Invert());

            Should.Throw<InvalidOperationException>(() => Image.ProcessTilesTo(source, destination, plan,
                16, 12, new TranscodeOptions
                {
                    Execute = new ExecuteOptions { Workspace = new WorkspaceManager(limitBytes: 1) },
                }));

            File.ReadAllBytes(destination).ShouldBe(original);
            Directory.GetFiles(Path.GetDirectoryName(destination)!, $".{Path.GetFileNameWithoutExtension(destination)}.*.png")
                .ShouldBeEmpty("the failed tile transcode must remove its sibling temporary output");
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public void ProcessTilesTo_RefusesAWholeImageStep()
    {
        using var image = TestImages.Gradient(24, 18, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Equalize());
        using var encoded = new MemoryStream();

        var error = Should.Throw<NotSupportedException>(() =>
            Image.ProcessTilesTo(new ViewRegionSource(image[0].Pixels.View), encoded, ImageFormat.Png, plan, 8, 8));
        error.Message.ShouldContain("GlobalStep");
    }

    [Fact]
    public void ProcessTilesTo_RefusesAResize()
    {
        using var image = TestImages.Gradient(24, 18, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Invert()).Resize(12, 9);
        using var encoded = new MemoryStream();

        Should.Throw<NotSupportedException>(() =>
            Image.ProcessTilesTo(new ViewRegionSource(image[0].Pixels.View), encoded, ImageFormat.Png, plan, 8, 8));
    }

    private static byte[] Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 3;
                pixels[o] = (byte)(x * 5);
                pixels[o + 1] = (byte)(y * 7);
                pixels[o + 2] = (byte)(x + y);
            }
        }

        return pixels;
    }

    private sealed class ViewRegionSource(PixelView view) : IRegionSource
    {
        public int Width => view.Width;
        public int Height => view.Height;
        public WorkingFormat Format => view.Format;

        public void ReadRegion(PixelRect region, PixelView destination)
        {
            var bytes = view.Format.BytesPerPixel();
            for (var y = 0; y < region.Height; y++)
            {
                view.Row(region.Y + y).Slice(region.X * bytes, region.Width * bytes)
                    .CopyTo(destination.Row(y));
            }
        }
    }
}
