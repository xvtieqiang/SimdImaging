using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// Architecture.md §6.2 contract: row bands are fixed, never work-stealing, so the number of workers
/// cannot change a single output byte. Uses an image tall enough to actually enter the parallel path
/// (RowParallel.For stays on the calling thread below 256 rows).
/// </summary>
public class RowBandTests
{
    [Fact]
    public void PointOperators_AreByteIdenticalAcrossParallelismDegrees()
    {
        const int width = 257, height = 513;
        var random = new Random(20261106);
        var data = new byte[width * height * 4];
        random.NextBytes(data);

        using var image = Image.Create(width, height, WorkingFormat.Rgba8);
        data.CopyTo(image.WritablePixels);
        var view = image[0].Pixels.View;

        var cases = new (string Name, Func<PixelView, WorkingBuffer> Run)[]
        {
            ("Invert (LUT)", v => PointOps.Invert(v)),
            ("Brightness (LUT)", v => PointOps.Brightness(v, 0.17f)),
            ("Contrast (LUT)", v => PointOps.Contrast(v, 1.3f)),
            ("Gamma (LUT)", v => PointOps.Gamma(v, 1.8f)),
            ("Opacity (LUT)", v => PointOps.Opacity(v, 0.7f)),
            ("Grayscale (matrix)", v => PointOps.Grayscale(v)),
            ("Sepia (matrix)", v => PointOps.Sepia(v)),
            ("HueRotate (matrix)", v => PointOps.HueRotate(v, 41f)),
            ("ColorMatrix (matrix)", v => PointOps.ColorMatrix(v,
                [0.9f, 0.1f, 0f, 0.02f, 0f, 1.1f, -0.1f, 0f, 0.05f, 0f, 0.8f, -0.01f])),
            ("Saturation (float)", v => PointOps.Saturation(v, 1.2f)),
            ("Vibrance (float)", v => PointOps.Vibrance(v, 0.3f)),
            ("Threshold (float)", v => PointOps.Threshold(v, 0.4f)),
            ("BlackWhite (float)", v => PointOps.BlackWhite(v, 0.6f)),
            ("Normalize (float)", v => PointOps.Normalize(v)),
        };

        try
        {
            foreach (var scalarPath in new[] { false, true })
            {
                PointOps.ForceScalarPoint = scalarPath;
                foreach (var (name, run) in cases)
                {
                    RowParallel.DegreeOverride = 1;
                    byte[] expected;
                    using (var single = run(view))
                        expected = single.View.Span.ToArray();

                    RowParallel.DegreeOverride = 16;
                    byte[] actual;
                    using (var parallel = run(view))
                        actual = parallel.View.Span.ToArray();

                    actual.AsSpan().SequenceEqual(expected)
                        .ShouldBeTrue($"{name} (scalarPath={scalarPath}): row bands must be byte-identical");
                }
            }
        }
        finally
        {
            RowParallel.DegreeOverride = 0;
            PointOps.ForceScalarPoint = false;
        }
    }

    [Fact]
    public void WorkBasedThreshold_WideShortImageParallel_SmallImageSingleThreaded()
    {
        try
        {
            RowParallel.DegreeOverride = 16;

            // 4000×200 = 800 K pixels but only 200 rows: the old height-only rule left this on one thread.
            using var wide = Image.Create(4000, 200, WorkingFormat.Rgba8);
            using var wideResult = wide.Pipe().Invert().ToImage();
            RowParallel.LastWorkerCount.ShouldBeGreaterThan(1, "wide short images are enough work to parallelise");
            var parallelBytes = wideResult.WritablePixels.ToArray();

            using var small = Image.Create(64, 64, WorkingFormat.Rgba8);
            using var smallResult = small.Pipe().Invert().ToImage();
            RowParallel.LastWorkerCount.ShouldBe(1, "tiny images must not pay for thread setup");

            RowParallel.DegreeOverride = 1;
            using var singleResult = wide.Pipe().Invert().ToImage();
            singleResult.WritablePixels.ToArray().AsSpan().SequenceEqual(parallelBytes)
                .ShouldBeTrue("wide short image must be byte-identical at any worker count");
        }
        finally
        {
            RowParallel.DegreeOverride = 0;
        }
    }

    [Fact]
    public void FiltersAndResize_AreByteIdenticalAcrossParallelismDegrees()
    {
        const int width = 257, height = 513;
        var random = new Random(77119);
        var data = new byte[width * height * 4];
        random.NextBytes(data);

        using var image = Image.Create(width, height, WorkingFormat.Rgba8);
        data.CopyTo(image.WritablePixels);

        // The resize targets are tall enough that the 512-row cap produces more than one band.
        var cases = new (string Name, Func<Image, Image> Run)[]
        {
            ("GaussianBlur", i => i.Pipe().GaussianBlur(3f).ToImage()),
            ("BoxBlur", i => i.Pipe().BoxBlur(3).ToImage()),
            ("Resize bicubic", i => i.Pipe().Resize(129, 1025, Resampler.Bicubic).ToImage()),
            ("Resize Lanczos3", i => i.Pipe().Resize(129, 1025, Resampler.Lanczos3).ToImage()),
            ("Rotate90", i => i.Pipe().Rotate90().ToImage()),
        };

        try
        {
            foreach (var (name, run) in cases)
            {
                RowParallel.DegreeOverride = 1;
                byte[] expected;
                using (var single = run(image))
                    expected = single.WritablePixels.ToArray();

                RowParallel.DegreeOverride = 16;
                byte[] actual;
                using (var parallel = run(image))
                    actual = parallel.WritablePixels.ToArray();

                actual.AsSpan().SequenceEqual(expected)
                    .ShouldBeTrue($"{name}: band parallelism must be byte-identical");
            }
        }
        finally
        {
            RowParallel.DegreeOverride = 0;
        }
    }

    /// <summary>
    /// The row pump's own bound: the buffers it holds are a shape, and a shape wider than one materialised frame is
    /// strictly worse than not streaming. The bound is `height / windows - 2 × Σhalo`, so it is a comparison with the
    /// alternative rather than a constant — 1080p gets colder, 4K and 8K are untouched. Measured on the file path:
    /// 1080p `point-3` peaked at 31.0 MiB (1.9 frames) against 25.2 MiB for the oracle; at 4K the same chain is
    /// already under the bound and did not change.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(7680, 4320)]
    [InlineData(512, 2000)]
    public void ThePumpBandKeepsItsWindowsInsideOneFrame(int width, int height)
    {
        var plan = new ProcessingPlan()
            .Add(PlanOps.Invert()).Add(PlanOps.Contrast(1.1f)).Add(PlanOps.Grayscale());

        var bandRows = SequentialBands.For(plan, width, height, WorkingFormat.Rgba8, 16, withinOneFrame: true);
        var windows = SequentialBands.Windows(plan.Steps);

        windows.ShouldBe(4);
        (windows * bandRows).ShouldBeLessThanOrEqualTo(height,
            $"{width}x{height}: {windows} buffers of {bandRows} rows hold more than a frame");
        bandRows.ShouldBeGreaterThanOrEqualTo(8);
    }

    [Fact]
    public void The1080pPumpBandIsCappedByTheFrameNotByTheWorkerCount()
    {
        var plan = new ProcessingPlan()
            .Add(PlanOps.Invert()).Add(PlanOps.Contrast(1.1f)).Add(PlanOps.Grayscale());

        // 16 workers × 32 rows = 512, which four buffers turn into 2048 rows on a 1080-row image.
        SequentialBands.For(plan, 1920, 1080, WorkingFormat.Rgba8, 16).ShouldBe(512);
        SequentialBands.For(plan, 1920, 1080, WorkingFormat.Rgba8, 16, withinOneFrame: true).ShouldBe(270);
    }
}
