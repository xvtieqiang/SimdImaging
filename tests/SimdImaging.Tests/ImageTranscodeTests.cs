using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>The file builder is a facade: every path it takes must equal the static entry point it routes to.</summary>
public class ImageTranscodeTests
{
    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), $"simd-file-{Guid.NewGuid():N}{extension}");

    private static ProcessingPlan BlurContrast() =>
        new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Contrast(1.1f));

    [Fact]
    public void Explain_ReportsTheStreamBackendAndTheGeometryOutput()
    {
        var source = Temp(".png");
        var destination = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var transcode = ImageTranscode.From(source).Invert().Resize(32, 24);
            var explanation = transcode.Explain();

            explanation.Backend.ShouldBe(PlanBackend.Stream);
            explanation.Refusals.Stream.ShouldBe(StreamRefusal.None);
            explanation.Output.Width.ShouldBe(32);
            explanation.Output.Height.ShouldBe(24);
            explanation.Steps.Count.ShouldBe(2);
            explanation.Steps[0].Name.ShouldBe("Invert");
            explanation.Steps[1].IsGeometry.ShouldBeTrue();

            // Explaining reads the header only: the same builder still runs, and it still streams.
            transcode.Run(destination).Mode.ShouldBe(ProcessMode.Streamed);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public void Explain_ReportsWhyAWholeImageStepCannotStream()
    {
        var source = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(32, 24, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var explanation = ImageTranscode.From(source).Noise(0.05f, seed: 3).Explain();

            explanation.Backend.ShouldBe(PlanBackend.Frame);
            explanation.Refusals.Stream.ShouldBe(StreamRefusal.GlobalStep);
            explanation.Notes.ShouldContain(note => note.Contains("stream refused"));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void Explain_RefusesAForwardOnlyStream()
    {
        var bytes = Image.Encode(TestImages.Gradient(16, 12, WorkingFormat.Rgba8), ImageFormat.Png);
        using var stream = new NonSeekableStream(bytes);

        Should.Throw<NotSupportedException>(() => ImageTranscode.From(stream).Invert().Explain());
    }

    [Fact]
    public void ApplySave_MatchesTheWholeImageChain()
    {
        var source = Temp(".png");
        var builder = Temp(".png");
        var direct = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8))
                Image.Save(image, source);

            ImageTranscode.From(source).GaussianBlur(1f).Contrast(1.1f).Save(builder);
            using (var loaded = Image.Load(source))
            using (var filtered = loaded.Apply(BlurContrast()))
                Image.Save(filtered, direct);

            File.ReadAllBytes(builder).ShouldBe(File.ReadAllBytes(direct));
        }
        finally
        {
            foreach (var path in new[] { source, builder, direct })
                File.Delete(path);
        }
    }

    [Fact]
    public void NamedOperators_MatchTheWholeImageChain()
    {
        var source = Temp(".png");
        var fluent = Temp(".png");
        var planned = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8))
                Image.Save(image, source);

            ImageTranscode.From(source).GaussianBlur(1f).Contrast(1.1f).Resize(97, 61).Save(fluent);
            using (var loaded = Image.Load(source))
            using (var filtered = loaded.Apply(BlurContrast()))
            using (var resized = filtered.Pipe().Resize(97, 61, Resampler.Bicubic).ToImage())
                Image.Save(resized, planned);

            File.ReadAllBytes(fluent).ShouldBe(File.ReadAllBytes(planned));
        }
        finally
        {
            foreach (var path in new[] { source, fluent, planned })
                File.Delete(path);
        }
    }

    [Fact]
    public void ApplyResizeApplySave_MatchesTheWholeImageChain()
    {
        var source = Temp(".png");
        var builder = Temp(".png");
        var direct = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8))
                Image.Save(image, source);

            ImageTranscode.From(source).GaussianBlur(1f).Contrast(1.1f).Resize(97, 61).Invert().Save(builder);
            using (var loaded = Image.Load(source))
            using (var filtered = loaded.Apply(BlurContrast()))
            using (var resized = filtered.Pipe().Resize(97, 61, Resampler.Bicubic).ToImage())
            using (var inverted = resized.Apply(new ProcessingPlan().Add(PlanOps.Invert())))
                Image.Save(inverted, direct);

            File.ReadAllBytes(builder).ShouldBe(File.ReadAllBytes(direct));
        }
        finally
        {
            foreach (var path in new[] { source, builder, direct })
                File.Delete(path);
        }
    }

    [Fact]
    public void RegionApplyResizeToImage_MatchesLoadApplyCropResize()
    {
        var source = Temp(".tif");
        try
        {
            File.WriteAllBytes(source, TiffFixture.Strips(320, 200, PixelFormat.Rgb24,
                TestPattern(320, 200, 3)));
            var region = new PixelRect(40, 30, 160, 120);

            using var built = ImageTranscode.From(source).Region(region).GaussianBlur(1f).Contrast(1.1f).Resize(61, 47).ToImage();
            using var loaded = Image.Load(source);
            using var filtered = loaded.Apply(BlurContrast());
            using var cropped = filtered.Pipe().Crop(region).ToImage();
            using var direct = cropped.Pipe().Resize(61, 47, Resampler.Bicubic).ToImage();

            built.WritablePixels.ToArray().ShouldBe(direct.WritablePixels.ToArray());
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void RegionSave_MatchesLoadCropResizeEncode()
    {
        var source = Temp(".tif");
        var saved = Temp(".png");
        var expected = Temp(".png");
        try
        {
            File.WriteAllBytes(source, TiffFixture.Strips(64, 48, PixelFormat.Rgb24, TestPattern(64, 48, 3)));

            ImageTranscode.From(source).Region(new PixelRect(8, 6, 32, 24)).Resize(16, 12).Save(saved);

            using (var loaded = Image.Load(source))
            using (var cropped = loaded.Pipe().Crop(new PixelRect(8, 6, 32, 24)).ToImage())
            using (var resized = cropped.Pipe().Resize(16, 12, Resampler.Bicubic).ToImage())
                Image.Save(resized, expected);

            File.ReadAllBytes(saved).ShouldBe(File.ReadAllBytes(expected));
        }
        finally
        {
            foreach (var path in new[] { source, saved, expected })
                File.Delete(path);
        }
    }


    [Fact]
    public void StreamWorkspaceRefusal_DoesNotWriteToTheDestination()
    {
        var sourcePath = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8))
                Image.Save(image, sourcePath);

            using var source = File.OpenRead(sourcePath);
            using var destination = new MemoryStream();
            var error = Should.Throw<InvalidOperationException>(() =>
                ImageTranscode.From(source).Invert()
                    .WithOptions(new TranscodeOptions
                    {
                        Execute = new ExecuteOptions { MaxWorkingBytes = 1 },
                    })
                    .Run(destination, ImageFormat.Png));

            error.Message.ShouldContain("estimated peak");
            destination.Length.ShouldBe(0,
                "the compiled chain and its reservations must be ready before the encoder writes a header");

            // A caller-supplied WorkspaceManager can be tighter than MaxWorkingBytes. The operator windows fit,
            // but the additional Pipeline.Copy buffer does not; that refusal must still happen before encoding.
            var manager = new WorkspaceManager(limitBytes: 30_000);
            using var secondSource = File.OpenRead(sourcePath);
            using var secondDestination = new MemoryStream();
            Should.Throw<InvalidOperationException>(() => ImageTranscode.From(secondSource).Invert()
                .WithOptions(new TranscodeOptions
                {
                    Execute = new ExecuteOptions { Workspace = manager },
                })
                .Run(secondDestination, ImageFormat.Png));
            secondDestination.Length.ShouldBe(0);
            manager.OutstandingBytes.ShouldBe(0);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }


    [Fact]
    public void PathTranscode_BudgetRefusal_PreservesExistingDestination()
    {
        var source = Temp(".png");
        var destination = Temp(".png");
        var original = new byte[] { 7, 11, 19, 23, 31 };
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8))
                Image.Save(image, source);
            File.WriteAllBytes(destination, original);

            Should.Throw<InvalidOperationException>(() => ImageTranscode.From(source).Invert()
                .WithOptions(new TranscodeOptions
                {
                    Execute = new ExecuteOptions { MaxWorkingBytes = 1 },
                })
                .Run(destination));

            File.ReadAllBytes(destination).ShouldBe(original);
            Directory.GetFiles(Path.GetDirectoryName(destination)!, $".{Path.GetFileNameWithoutExtension(destination)}.*.png")
                .ShouldBeEmpty("the failed operation must remove its sibling temporary output");
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public void StreamSourceAndDestination_MatchThePathForm()
    {
        var source = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(120, 80, WorkingFormat.Rgba8))
                Image.Save(image, source);

            using var input = File.OpenRead(source);
            using var built = new MemoryStream();
            var report = ImageTranscode.From(input).GaussianBlur(1f).Contrast(1.1f).Resize(61, 41).Run(built, ImageFormat.Png);

            using var direct = Image.Load(source);
            using var filtered = direct.Apply(BlurContrast());
            using var resized = filtered.Pipe().Resize(61, 41, Resampler.Bicubic).ToImage();
            var expected = Image.Encode(resized, ImageFormat.Png);

            report.Width.ShouldBe(61);
            built.ToArray().ShouldBe(expected);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void AStreamSourceToAPath_StreamsAndMatchesThePathForm()
    {
        // The entry point used to decode the whole image for this shape: Run(string) only opened a row chain when
        // the SOURCE was a path, so From(stream).Run(path) materialised while From(stream).Run(stream) streamed.
        // Bytes were equal, which is why nothing caught it — the report was the only witness, and it said None.
        var source = Temp(".png");
        var fromStream = Temp(".png");
        var fromPath = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(160, 120, WorkingFormat.Rgba8))
                Image.Save(image, source);

            using var input = File.OpenRead(source);
            var report = ImageTranscode.From(input).GaussianBlur(1f).Contrast(1.1f).Resize(61, 41).Run(fromStream);

            ImageTranscode.From(source).GaussianBlur(1f).Contrast(1.1f).Resize(61, 41).Run(fromPath);

            report.Mode.ShouldBe(ProcessMode.Streamed);
            report.Refusal.ShouldBe(StreamRefusal.None);
            report.Width.ShouldBe(61);
            report.RowsWritten.ShouldBe(41);
            File.ReadAllBytes(fromStream).ShouldBe(File.ReadAllBytes(fromPath));
        }
        finally
        {
            foreach (var path in new[] { source, fromStream, fromPath })
                File.Delete(path);
        }
    }

    [Fact]
    public void AForwardOnlyStream_WithAWholeInputDecode_MaterialisesOnce()
    {
        // A codec-negotiated scale is settled from the header, so the transcode materialises such a source before
        // the row chain is opened. A forward-only stream is read exactly once instead of turning the call into a throw.
        var source = Temp(".png");
        var destination = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(64, 64, WorkingFormat.Rgba8))
                Image.Save(image, source);

            using var input = new ForwardOnlyStream(File.OpenRead(source));
            var report = ImageTranscode.From(input).GaussianBlur(1f)
                .WithOptions(new TranscodeOptions
                {
                    Load = new LoadOptions { PreferNativeScale = true, ScaleDenominator = 2 },
                })
                .Run(destination);

            report.Mode.ShouldBe(ProcessMode.Materialized);
            report.Refusal.ShouldBe(StreamRefusal.DecodeNeedsWholeInput);
            report.Width.ShouldBe(32);
            report.Height.ShouldBe(32);
            File.Exists(destination).ShouldBeTrue();
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public void AnOperatorBeyondTheOldShortcuts_WorksOnTheFileSide()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(160, 120, WorkingFormat.Rgba8))
                Image.Save(image, source);

            // Median3x3 never had a shortcut; it comes from the shared operator surface now.
            ImageTranscode.From(source).Median3x3().Save(streamed);

            using (var loaded = Image.Load(source))
            using (var filtered = loaded.Pipe().Median3x3().ToImage())
                Image.Save(filtered, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void AGlobalOperator_MaterialisesOnTheFileSide_WithTheSameBytes()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(96, 72, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var report = ImageTranscode.From(source).Clahe().Run(streamed);

            using (var loaded = Image.Load(source))
            using (var filtered = loaded.Pipe().Clahe().ToImage())
                Image.Save(filtered, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
            report.Mode.ShouldBe(ProcessMode.Materialized);
            report.Refusal.ShouldBe(StreamRefusal.GlobalStep);
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void AStreamSource_CannotBeRunTwice()
    {
        var source = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8))
                Image.Save(image, source);

            using var input = File.OpenRead(source);
            var transcode = ImageTranscode.From(input).GaussianBlur(1f);
            using var first = new MemoryStream();
            transcode.Run(first, ImageFormat.Png);

            using var second = new MemoryStream();
            var error = Should.Throw<InvalidOperationException>(() => transcode.Run(second, ImageFormat.Png));
            error.Message.ShouldContain("runs once");
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void APathSource_CanBeRunTwice()
    {
        var source = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var transcode = ImageTranscode.From(source).GaussianBlur(1f);
            using var first = new MemoryStream();
            using var second = new MemoryStream();
            transcode.Run(first, ImageFormat.Png);
            transcode.Run(second, ImageFormat.Png);

            first.ToArray().ShouldBe(second.ToArray());
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void CropThenResize_MatchesTheInMemoryChain()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var rect = new PixelRect(40, 30, 160, 120);
            ImageTranscode.From(source).Crop(rect).GaussianBlur(1f).Resize(61, 47).Save(streamed);

            using (var loaded = Image.Load(source))
            using (var cropped = loaded.Pipe().Crop(rect).ToImage())
            using (var filtered = cropped.Pipe().GaussianBlur(1f).ToImage())
            using (var resized = filtered.Pipe().Resize(61, 47, Resampler.Bicubic).ToImage())
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
    public void CropWithAGlobalOperator_MaterialisesWithTheSameBytes()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(160, 120, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var rect = new PixelRect(20, 10, 80, 60);
            var report = ImageTranscode.From(source).Crop(rect).Clahe().Run(streamed);

            using (var loaded = Image.Load(source))
            using (var cropped = loaded.Pipe().Crop(rect).ToImage())
            using (var filtered = cropped.Pipe().Clahe().ToImage())
                Image.Save(filtered, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
            report.Mode.ShouldBe(ProcessMode.Materialized);
            report.Refusal.ShouldBe(StreamRefusal.GlobalStep);
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void PadAndFlip_StreamWithTheSameBytesAsTheInMemoryChain()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(160, 120, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var color = new RgbaColor(20, 40, 60, 255);
            ImageTranscode.From(source)
                .Crop(new PixelRect(20, 10, 80, 60))
                .Pad(7, 5, 9, 3, color)
                .FlipHorizontal()
                .GaussianBlur(1f)
                .Resize(41, 31)
                .Save(streamed);

            using (var loaded = Image.Load(source))
            using (var cropped = loaded.Pipe().Crop(new PixelRect(20, 10, 80, 60)).ToImage())
            using (var padded = cropped.Pipe().Pad(7, 5, 9, 3, color).ToImage())
            using (var flipped = padded.Pipe().FlipHorizontal().ToImage())
            using (var filtered = flipped.Pipe().GaussianBlur(1f).ToImage())
            using (var resized = filtered.Pipe().Resize(41, 31, Resampler.Bicubic).ToImage())
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
    public void PadOnRgba16_StreamsWithTheSameBytes()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(96, 72, WorkingFormat.Rgba16))
                Image.Save(image, source);

            ImageTranscode.From(source).Pad(6, new RgbaColor(10, 20, 30, 255)).Save(streamed);

            using (var loaded = Image.Load(source))
            using (var padded = loaded.Pipe().Pad(6, new RgbaColor(10, 20, 30, 255)).ToImage())
                Image.Save(padded, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void TheSameStreamAsSourceAndDestination_IsRefused()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var error = Should.Throw<ArgumentException>(() =>
            ImageTranscode.From(stream).GaussianBlur(1f).Run(stream, ImageFormat.Png));
        error.Message.ShouldContain("same stream");
    }

    [Fact]
    public async Task RunAsync_ProducesTheSameBytes()
    {
        var source = Temp(".png");
        var sync = Temp(".png");
        var async = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(160, 120, WorkingFormat.Rgba8))
                Image.Save(image, source);

            ImageTranscode.From(source).GaussianBlur(1f).Resize(61, 47).Save(sync);
            await ImageTranscode.From(source).GaussianBlur(1f).Resize(61, 47).SaveAsync(async);

            File.ReadAllBytes(async).ShouldBe(File.ReadAllBytes(sync));
        }
        finally
        {
            foreach (var path in new[] { source, sync, async })
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ACancelledToken_StopsBeforeAnyWork()
    {
        using var input = new MemoryStream([1, 2, 3]);
        using var output = new MemoryStream();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ImageTranscode.From(input).GaussianBlur(1f).RunAsync(output, ImageFormat.Png, cancelled.Token));
    }

    [Fact]
    public void MisorderedChains_AreRefused()
    {
        ImageTranscode.From("x.png").Resize(10, 10);
        Should.Throw<InvalidOperationException>(() => ImageTranscode.From("x.png").Resize(10, 10).Resize(5, 5));
        Should.Throw<InvalidOperationException>(() =>
            ImageTranscode.From("x.png").Region(new PixelRect(0, 0, 1, 1)).Region(new PixelRect(0, 0, 2, 2)));
    }

    private static byte[] TestPattern(int width, int height, int channels)
    {
        var bytes = new byte[width * height * channels];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(((uint)i * 2654435761u) >> 13);
        return bytes;
    }
}
