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
            using var wideResult = wide.Invert();
            RowParallel.LastWorkerCount.ShouldBeGreaterThan(1, "wide short images are enough work to parallelise");
            var parallelBytes = wideResult.WritablePixels.ToArray();

            using var small = Image.Create(64, 64, WorkingFormat.Rgba8);
            using var smallResult = small.Invert();
            RowParallel.LastWorkerCount.ShouldBe(1, "tiny images must not pay for thread setup");

            RowParallel.DegreeOverride = 1;
            using var singleResult = wide.Invert();
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
            ("GaussianBlur", i => i.GaussianBlur(3f)),
            ("BoxBlur", i => i.BoxBlur(3)),
            ("Resize bicubic", i => i.Resize(129, 1025, Resampler.Bicubic)),
            ("Resize Lanczos3", i => i.Resize(129, 1025, Resampler.Lanczos3)),
            ("Rotate90", i => i.Rotate90()),
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
}
