using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The facade: <c>Image.ProcessRegion</c> against the whole-image path, plus the two promises that make it worth
/// having — the file is not read whole, and the header's information survives.
/// </summary>
public class RegionEndToEndTests
{
    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), $"simd-region-e2e-{Guid.NewGuid():N}{extension}");

    /// <summary>A deterministic, high-entropy pattern: linear ramps would compress to a few kilobytes and make a
    /// "read less than the file" assertion pass for the wrong reason.</summary>
    private static byte[] Pattern(int width, int height, int channels)
    {
        var bytes = new byte[width * height * channels];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(((uint)i * 2654435761u) >> 13);
        return bytes;
    }

    private static readonly (string Name, Func<ProcessingPlan> Build)[] Chains =
    [
        ("empty", () => new ProcessingPlan()),
        ("point", () => new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.Contrast(1.1f))),
        ("blur", () => new ProcessingPlan().Add(PlanOps.GaussianBlur(1.2f))),
        ("mixed", () => new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.GaussianBlur(1f))
            .Add(PlanOps.Contrast(1.1f))),
    ];

    [Fact]
    public void ProcessRegion_MatchesLoadApplyCrop()
    {
        var path = Temp(".tif");
        try
        {
            File.WriteAllBytes(path, TiffFixture.Strips(53, 37, PixelFormat.Rgb48, Pattern(53, 37, 6)));
            var region = new PixelRect(9, 6, 21, 17);

            foreach (var (name, build) in Chains)
            {
                var plan = build();
                using var whole = Image.Load(path);
                using var processed = whole.Apply(plan);
                using var expected = processed.Pipe().Crop(region).ToImage();
                using var actual = Image.ProcessRegion(path, plan, region);

                actual.Width.ShouldBe(region.Width);
                actual.Height.ShouldBe(region.Height);
                actual.Format.ShouldBe(whole.Format);
                actual.Info.BitDepth.ShouldBe(whole.Info.BitDepth);
                actual.Info.Orientation.ShouldBe(whole.Info.Orientation);
                actual.WritablePixels.ToArray().ShouldBe(expected.WritablePixels.ToArray(), $"{name}: pixels");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProcessRegion_RefusesIfThePlanNeedsTheWholeImage()
    {
        var path = Temp(".tif");
        try
        {
            File.WriteAllBytes(path, TiffFixture.Strips(24, 18, PixelFormat.Rgb24, Pattern(24, 18, 3)));
            var error = Should.Throw<NotSupportedException>(() =>
                Image.ProcessRegion(path, new ProcessingPlan().Add(PlanOps.Equalize()),
                    new PixelRect(0, 0, 8, 8)));
            error.Message.ShouldContain("GlobalStep");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProcessRegion_ReadsLessThanTheWholeFile_AndScalesWithTheRegion()
    {
        static long BytesFor(int height, PixelRect region)
        {
            var path = Temp(".tif");
            try
            {
                File.WriteAllBytes(path, TiffFixture.Strips(512, height, PixelFormat.Rgb24,
                    Pattern(512, height, 3), rowsPerStrip: 32));

                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var counting = new CountingStream(file);
                using var image = Image.ProcessRegion(counting, new ProcessingPlan(), region);
                image.Width.ShouldBe(region.Width);
                return counting.BytesRead;
            }
            finally
            {
                File.Delete(path);
            }
        }

        var region = new PixelRect(100, 100, 16, 16);
        var shortFile = BytesFor(512, region);
        var tallFile = BytesFor(2048, region);

        // 512x512 RGB is ~786 KiB uncompressed; the 16x16 region lies inside one 32-row strip, so the read is that
        // strip plus the directory. The bound is the file's eighth, which only an incompressible pattern makes
        // meaningful.
        shortFile.ShouldBeLessThan(786_432 / 8);
        tallFile.ShouldBeLessThanOrEqualTo((long)(shortFile * 1.25), $"read {tallFile} vs {shortFile}");
        tallFile.ShouldBeGreaterThanOrEqualTo((long)(shortFile * 0.75));
    }

    [Fact]
    public void RegionResize_MatchesLoadCropResize()
    {
        var path = Temp(".tif");
        try
        {
            File.WriteAllBytes(path, TiffFixture.Strips(320, 200, PixelFormat.Rgb24, Pattern(320, 200, 3)));

            foreach (var (filter, border) in new[]
                     {
                         (Resampler.Bicubic, "Bicubic"),
                         (Resampler.Lanczos3, "Lanczos3"),
                         (Resampler.Nearest, "Nearest"),
                     })
            {
                var region = new PixelRect(40, 30, 160, 120);
                using var actual = ImageTranscode.From(path).Region(region).Resize(61, 47, filter).ToImage();

                using var whole = Image.Load(path);
                using var cropped = whole.Pipe().Crop(region).ToImage();
                using var expected = cropped.Pipe().Resize(61, 47, filter).ToImage();

                actual.Width.ShouldBe(61);
                actual.Height.ShouldBe(47);
                actual.WritablePixels.ToArray().ShouldBe(expected.WritablePixels.ToArray(), border);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RegionResize_WithAPlan_MatchesLoadApplyCropResize()
    {
        var path = Temp(".tif");
        try
        {
            File.WriteAllBytes(path, TiffFixture.Strips(320, 200, PixelFormat.Rgb24, Pattern(320, 200, 3)));
            var plan = new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Contrast(1.1f));
            var region = new PixelRect(40, 30, 160, 120);

            using var actual = ImageTranscode.From(path).Region(region).Append(plan)
                .Resize(61, 47, Resampler.Bicubic).ToImage();

            using var whole = Image.Load(path);
            using var applied = whole.Apply(plan);
            using var cropped = applied.Pipe().Crop(region).ToImage();
            using var expected = cropped.Pipe().Resize(61, 47, Resampler.Bicubic).ToImage();

            actual.WritablePixels.ToArray().ShouldBe(expected.WritablePixels.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RegionResize_RefusesAPlanThatNeedsTheWholeImage()
    {
        var path = Temp(".tif");
        try
        {
            File.WriteAllBytes(path, TiffFixture.Strips(64, 48, PixelFormat.Rgb24, Pattern(64, 48, 3)));
            var error = Should.Throw<NotSupportedException>(() =>
                ImageTranscode.From(path).Region(new PixelRect(0, 0, 32, 24)).Equalize()
                    .Resize(16, 12).ToImage());
            error.Message.ShouldContain("GlobalStep");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RegionResize_AtTheEdge_MatchesLoadCropResize()
    {
        var path = Temp(".tif");
        try
        {
            File.WriteAllBytes(path, TiffFixture.Strips(64, 48, PixelFormat.Rgb48, Pattern(64, 48, 6)));
            var region = new PixelRect(0, 0, 64, 48);

            using var actual = ImageTranscode.From(path).Region(region).Resize(31, 23).ToImage();
            using var whole = Image.Load(path);
            using var cropped = whole.Pipe().Crop(region).ToImage();
            using var expected = cropped.Pipe().Resize(31, 23, Resampler.Bicubic).ToImage();

            actual.WritablePixels.ToArray().ShouldBe(expected.WritablePixels.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProcessRegion_AtTheTopAcrossManyBands_MatchesLoadApplyCrop()
    {
        var path = Temp(".tif");
        try
        {
            File.WriteAllBytes(path, TiffFixture.Strips(256, 256, PixelFormat.Rgb24, Pattern(256, 256, 3)));
            var region = new PixelRect(0, 0, 256, 256);
            // A small band height guarantees interior bands, whose windows carry halo on both sides. The first
            // band's window is clamped at the top, so it is the smallest — not the largest.
            var plan = new ProcessingPlan { BandRows = 32 }.Add(PlanOps.GaussianBlur(1f))
                .Add(PlanOps.Contrast(1.1f));

            using var whole = Image.Load(path);
            using var applied = whole.Apply(plan);
            using var expected = applied.Pipe().Crop(region).ToImage();
            using var actual = Image.ProcessRegion(path, plan, region);

            actual.WritablePixels.ToArray().ShouldBe(expected.WritablePixels.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProcessRegion_TakesThePageIndex_AndReportsItsOrientation()
    {
        var path = Temp(".tif");
        try
        {
            // Orientation 6 is the tag a phone writes; the pixels stay stored, the tag is reported.
            File.WriteAllBytes(path, TiffFixture.Build(20, 14, 3, 8, photometric: 2, Pattern(20, 14, 3), orientation: 6));
            using var image = Image.ProcessRegion(path, new ProcessingPlan(), new PixelRect(2, 3, 5, 4));

            image.Info.Orientation.ShouldBe(ImageOrientation.Rotate90);
            image.Info.Width.ShouldBe(5);
            image.Info.Height.ShouldBe(4);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>A stream that counts the bytes actually read through it — the proof that a region read did not pull the
/// whole file.</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
