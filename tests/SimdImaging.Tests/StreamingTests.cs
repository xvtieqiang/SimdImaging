using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// The file-level streaming path: <see cref="ProcessingPlan.TryExecuteTo"/> and <see cref="ImageTranscode"/>.
/// <para>Every test here compares against the whole-image path byte for byte. That is the contract: a streamed
/// result is the same image, reached with less memory — never a similar one.</para>
/// </summary>
public class StreamingTests
{
    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), $"simd-stream-{Guid.NewGuid():N}{extension}");

    /// <summary>Pulls rows out of a view, as a decoder would.</summary>
    private sealed class ViewRowSource(PixelView view) : IRowSource
    {
        private int _row;

        public int Width => view.Width;

        public int Height => view.Height;

        public WorkingFormat Format => view.Format;

        public int MaxRequested { get; private set; }

        public int ReadRows(Span<byte> destination, int stride, int maxRows)
        {
            if (stride == 0)
                stride = view.RowBytes;
            var rows = Math.Min(maxRows, view.Height - _row);
            MaxRequested = Math.Max(MaxRequested, rows);
            for (var y = 0; y < rows; y++)
                view.Row(_row + y).CopyTo(destination.Slice(y * stride, view.RowBytes));
            _row += rows;
            return rows;
        }
    }

    /// <summary>Collects finished rows into one buffer, as an encoder writing a file would not.</summary>
    private sealed class CollectingSink(int width, int height, WorkingFormat format) : IRowSink
    {
        private readonly int _rowBytes = width * format.BytesPerPixel();
        private int _written;

        public byte[] Bytes { get; } = new byte[width * height * format.BytesPerPixel()];

        public void WriteRows(ReadOnlySpan<byte> rows, int stride, int rowCount)
        {
            if (stride == 0)
                stride = _rowBytes;
            if (_written + rowCount > height || (long)stride * rowCount > rows.Length || stride < _rowBytes)
                throw new InvalidOperationException(
                    $"sink overflow: written={_written} count={rowCount} stride={stride} rowBytes={_rowBytes} len={rows.Length} height={height}");

            for (var y = 0; y < rowCount; y++)
                rows.Slice(y * stride, _rowBytes).CopyTo(Bytes.AsSpan((_written + y) * _rowBytes));
            _written += rowCount;
        }
    }

    public static TheoryData<string, WorkingFormat> Chains => new()
    {
        { "empty", WorkingFormat.Rgba8 },
        { "point", WorkingFormat.Rgba8 },
        { "blur", WorkingFormat.Rgba8 },
        { "3x3", WorkingFormat.Rgba8 },
        { "mixed", WorkingFormat.Rgba8 },
        { "point", WorkingFormat.Rgba16 },
        { "mixed", WorkingFormat.Rgba16 },
    };

    private static ProcessingPlan Build(string chain) => chain switch
    {
        "empty" => new ProcessingPlan(),
        "point" => new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.Contrast(1.1f)),
        "blur" => new ProcessingPlan().Add(PlanOps.GaussianBlur(1.5f)),
        "3x3" => new ProcessingPlan().Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel()),
        _ => new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.GaussianBlur(1f))
            .Add(PlanOps.Contrast(1.1f)),
    };

    [Theory]
    [MemberData(nameof(Chains))]
    public void Streamed_MatchesWholeImage_ByteForByte(string chain, WorkingFormat format)
    {
        using var image = TestImages.Gradient(53, 37, format);
        var plan = Build(chain);

        using var whole = plan.ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions(), out _);

        var sink = new CollectingSink(image.Width, image.Height, format);
        var result = plan.TryExecuteTo(new ViewRowSource(image[0].Pixels.View), sink);

        result.Succeeded.ShouldBeTrue($"{chain}/{format}: {result.Refusal} at '{result.RefusingStep}'");
        result.RowsWritten.ShouldBe(image.Height);
        sink.Bytes.ShouldBe(whole.View.Span.ToArray(), $"{chain}/{format}: streamed bytes must be the image");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    public void BandHeight_DoesNotChangeTheBytes(int bandRows)
    {
        using var image = TestImages.Gradient(41, 29, WorkingFormat.Rgba8);
        var expected = new CollectingSink(image.Width, image.Height, WorkingFormat.Rgba8);
        Build("mixed").TryExecuteTo(new ViewRowSource(image[0].Pixels.View), expected);

        var plan = new ProcessingPlan { BandRows = bandRows }
            .Add(PlanOps.Brightness(0.05f)).Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Contrast(1.1f));
        var actual = new CollectingSink(image.Width, image.Height, WorkingFormat.Rgba8);
        plan.TryExecuteTo(new ViewRowSource(image[0].Pixels.View), actual);

        actual.Bytes.ShouldBe(expected.Bytes, $"band height {bandRows} must not move a pixel");
    }

    [Fact]
    public void RowsHeld_DoNotGrowWithTheImageHeight()
    {
        static int MaxRowsFor(int height)
        {
            using var image = TestImages.Gradient(37, height, WorkingFormat.Rgba8);
            var plan = new ProcessingPlan { BandRows = 16 }.Add(PlanOps.GaussianBlur(1f));
            var source = new ViewRowSource(image[0].Pixels.View);
            plan.TryExecuteTo(source, new CollectingSink(image.Width, height, WorkingFormat.Rgba8));
            return source.MaxRequested;
        }

        var shortImage = MaxRowsFor(64);
        var tallImage = MaxRowsFor(512);
        tallImage.ShouldBe(shortImage, "the rows a band asks for must not scale with the image height");
    }

    [Fact]
    public void GlobalStep_IsRefused_AndNothingIsWritten()
    {
        using var image = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Equalize());

        plan.CanStream().ShouldBe(StreamRefusal.GlobalStep);

        var sink = new CollectingSink(image.Width, image.Height, WorkingFormat.Rgba8);
        var result = plan.TryExecuteTo(new ViewRowSource(image[0].Pixels.View), sink);
        result.Succeeded.ShouldBeFalse();
        result.Refusal.ShouldBe(StreamRefusal.GlobalStep);
        result.RefusingStep.ShouldBe("Equalize");
        sink.Bytes.ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void AStepThatReadsTheWholeImage_IsRefused()
    {
        using var image = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        float[] kernel = [1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9];
        var plan = new ProcessingPlan().Add(PlanOps.Convolve(kernel, 1, BorderMode.Mirror));

        plan.CanStream().ShouldBe(StreamRefusal.StepNeedsWholeSource);
    }

    [Fact]
    public void Cancellation_StopsMidStream()
    {
        using var image = TestImages.Gradient(32, 80, WorkingFormat.Rgba8);
        using var cts = new CancellationTokenSource();
        var plan = new ProcessingPlan { BandRows = 8 }.Add(PlanOps.GaussianBlur(1f));
        var source = new CancelAfterRows(image[0].Pixels.View, cts, 16);

        Should.Throw<OperationCanceledException>(() => plan.TryExecuteTo(source,
            new CollectingSink(image.Width, image.Height, WorkingFormat.Rgba8),
            new ExecuteOptions { CancellationToken = cts.Token }));
    }

    /// <summary>Cancels the token once <c>rowsBeforeCancelling</c> rows have been handed out: a request body that
    /// timed out halfway.</summary>
    private sealed class CancelAfterRows(PixelView view, CancellationTokenSource cts, int rowsBeforeCancelling)
        : IRowSource
    {
        private int _row;

        public int Width => view.Width;

        public int Height => view.Height;

        public WorkingFormat Format => view.Format;

        public int ReadRows(Span<byte> destination, int stride, int maxRows)
        {
            if (stride == 0)
                stride = view.RowBytes;
            if (_row >= rowsBeforeCancelling)
            {
                cts.Cancel();
                return 0;
            }

            var rows = Math.Min(maxRows, view.Height - _row);
            for (var y = 0; y < rows; y++)
                view.Row(_row + y).CopyTo(destination.Slice(y * stride, view.RowBytes));
            _row += rows;
            return rows;
        }
    }

    [Fact]
    public void Transcode_Png_Streams_AndMatchesTheWholeImagePath()
    {
        using var image = TestImages.Gradient(97, 41, WorkingFormat.Rgba8);
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            Image.Save(image, source);
            var plan = new ProcessingPlan().Add(PlanOps.GaussianBlur(1.2f)).Add(PlanOps.Contrast(1.15f));

            var report = ImageTranscode.From(source).Append(plan).Run(streamed);
            using (var loaded = Image.Load(source))
            using (var whole = loaded.Apply(plan))
                Image.Save(whole, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
            report.Mode.ShouldBe(ProcessMode.Streamed);
            report.Refusal.ShouldBe(StreamRefusal.None);
            report.RowsWritten.ShouldBe(41);
            report.Height.ShouldBe(41);
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void Transcode_Rgba16Png_KeepsTheDepth()
    {
        using var image = TestImages.Gradient(64, 33, WorkingFormat.Rgba16);
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            Image.Save(image, source);
            var plan = new ProcessingPlan().Add(PlanOps.Median3x3());

            var report = ImageTranscode.From(source).Append(plan).Run(streamed);
            using (var loaded = Image.Load(source))
            using (var whole = loaded.Apply(plan))
                Image.Save(whole, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected));
            report.Mode.ShouldBe(ProcessMode.Streamed);
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void Transcode_DropsMetadataByDefault_AndSaysSo()
    {
        var exif = new byte[26];
        exif[0] = 0x49;
        exif[1] = 0x49;
        exif[2] = 0x2A;
        exif[4] = 0x08;
        exif[8] = 0x01;
        exif[10] = 0x12;
        exif[11] = 0x01;
        exif[12] = 0x03;
        exif[14] = 0x01;

        var source = Temp(".jpg");
        var streamed = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(48, 32, WorkingFormat.Rgba8))
            using (var stamped = image.WithMetadata(new ImageMetadata { Exif = exif }))
                Image.Save(stamped, source);

            var report = ImageTranscode.From(source).Invert().Run(streamed);

            using var check = Image.Load(streamed);
            check.Metadata.Exif.Length.ShouldBe(0, "the default trade is a header-only read");
            report.MetadataCarried.ShouldBeFalse();
            check.Width.ShouldBe(48);
        }
        finally
        {
            File.Delete(source);
            File.Delete(streamed);
        }
    }

    [Fact]
    public void Transcode_ReadsTheSourceAboutOnce()
    {
        foreach (var (extension, format) in new[]
                 {
                     (".png", ImageFormat.Png),
                     (".jpg", ImageFormat.Jpeg),
                     (".tif", ImageFormat.Tiff),
                 })
        {
            // A high-entropy image, so the compressed file is about a megabyte and a "whole file read twice"
            // cannot hide behind a 4 KiB sniff.
            using var image = Image.Create(256, 1024, WorkingFormat.Rgba8);
            var pixels = image.WritablePixels;
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(((uint)i * 2654435761u) >> 13);

            var source = Temp(extension);
            try
            {
                File.WriteAllBytes(source, Image.Encode(image, format));
                using var file = File.OpenRead(source);
                using var counting = new CountingStream(file);
                using var output = new MemoryStream();

                ImageTranscode.From(counting).Invert().Run(output, ImageFormat.Png);

                // One pass, plus the sniff. 1.5× allows for the TIFF reader's own pre-read (measured 1.41×) and
                // is far below the 2.0–3.4× the full-file probe used to cost.
                var bound = (long)(file.Length * 1.5);
                counting.BytesRead.ShouldBeLessThanOrEqualTo(bound,
                    $"{extension}: read {counting.BytesRead} of {file.Length} bytes");
            }
            finally
            {
                File.Delete(source);
            }
        }
    }

    [Fact]
    public void Transcode_Materializes_WhenThePlanCannotStream_AndSaysWhy()
    {
        using var image = TestImages.Gradient(48, 30, WorkingFormat.Rgba8);
        var source = Temp(".png");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            Image.Save(image, source);
            // Equalize at the head is now two-pass capable on a path; use a global step with no analysis form.
            var plan = new ProcessingPlan().Add(PlanOps.Noise(0.05f, seed: 3));

            var report = ImageTranscode.From(source).Append(plan).Run(streamed);
            using (var loaded = Image.Load(source))
            using (var whole = loaded.Apply(plan))
                Image.Save(whole, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected),
                "the materialised path must be exactly Load + Apply + Save");
            report.Mode.ShouldBe(ProcessMode.Materialized);
            report.Refusal.ShouldBe(StreamRefusal.GlobalStep);
            report.FullyStreamed.ShouldBeFalse();
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void EnterTopLevel_OverridesTheDegreeOnThisThreadOnly()
    {
        using (RowParallel.EnterTopLevel(1))
            RowParallel.Degree.ShouldBe(1);
        using (RowParallel.EnterTopLevel(3))
            RowParallel.Degree.ShouldBe(3);

        // Out of the scope the frozen rule applies again (this test host has at least one core).
        RowParallel.Degree.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void MaxDegreeOfParallelism_One_StreamsTheSameBytes()
    {
        using var image = TestImages.Gradient(97, 53, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Contrast(1.1f));

        var serial = new CollectingSink(image.Width, image.Height, WorkingFormat.Rgba8);
        plan.TryExecuteTo(new ViewRowSource(image[0].Pixels.View), serial,
            new ExecuteOptions { MaxDegreeOfParallelism = 1 }).Succeeded.ShouldBeTrue();

        using var whole = plan.ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions(), out _);
        serial.Bytes.ShouldBe(whole.View.Span.ToArray(),
            "a single-threaded pump must produce the same pixels, not merely a plausible image");
    }

    [Fact]
    public void Transcode_KeepsTheHeadersExif_AndMatchesTheWholeImagePath()
    {
        // A minimal little-endian TIFF block with one IFD entry, as MetadataOpsTests builds it.
        var exif = new byte[26];
        exif[0] = 0x49;
        exif[1] = 0x49;
        exif[2] = 0x2A;
        exif[4] = 0x08;
        exif[8] = 0x01;
        exif[10] = 0x12;
        exif[11] = 0x01;
        exif[12] = 0x03;
        exif[14] = 0x01;

        var source = Temp(".jpg");
        var streamed = Temp(".png");
        var expected = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8))
            using (var stamped = image.WithMetadata(new ImageMetadata { Exif = exif }))
                Image.Save(stamped, source);

            var plan = new ProcessingPlan().Add(PlanOps.GaussianBlur(1f));
            var report = ImageTranscode.From(source).Append(plan)
                .WithOptions(new TranscodeOptions { PreserveMetadata = true }).Run(streamed);
            using (var loaded = Image.Load(source))
            using (var whole = loaded.Apply(plan))
                Image.Save(whole, expected);

            File.ReadAllBytes(streamed).ShouldBe(File.ReadAllBytes(expected),
                "a header block like Exif must survive the streamed path");
            using var check = Image.Load(streamed);
            check.Metadata.Exif.Length.ShouldBeGreaterThan(0);
            report.Mode.ShouldBe(ProcessMode.Streamed);
            report.MetadataCarried.ShouldBeTrue();
        }
        finally
        {
            foreach (var path in new[] { source, streamed, expected })
                File.Delete(path);
        }
    }

    [Fact]
    public void Transcode_PreferNativeScale_Materializes_AndKeepsTheDecodesSize()
    {
        using var image = TestImages.Gradient(64, 64, WorkingFormat.Rgba8);
        var source = Temp(".png");
        var destination = Temp(".png");
        try
        {
            Image.Save(image, source);
            var report = ImageTranscode.From(source).Invert()
                .WithOptions(new TranscodeOptions
                {
                    Load = new LoadOptions { ScaleDenominator = 2, PreferNativeScale = true },
                })
                .Run(destination);

            report.Mode.ShouldBe(ProcessMode.Materialized);
            report.Refusal.ShouldBe(StreamRefusal.DecodeNeedsWholeInput);
            report.Width.ShouldBe(32);
            report.Height.ShouldBe(32);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public void Transcode_RefusesFloatOutput()
    {
        using var image = TestImages.Gradient(16, 9, WorkingFormat.Rgba8);
        var source = Temp(".png");
        var destination = Temp(".png");
        try
        {
            Image.Save(image, source);
            var plan = new ProcessingPlan().Add(PlanOps.Invert());

            Should.Throw<NotSupportedException>(() => ImageTranscode.From(source).Append(plan)
                .WithOptions(new TranscodeOptions { Load = new LoadOptions { Format = WorkingFormat.Rgba32F } })
                .Run(destination));
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public void Transcode_NonSeekableSource_Streams()
    {
        using var image = TestImages.Gradient(40, 26, WorkingFormat.Rgba8);
        var buffer = new MemoryStream(Image.Encode(image, ImageFormat.Png));
        var output = new MemoryStream();

        var report = ImageTranscode.From(new ForwardOnlyStream(buffer)).Invert().Run(output, ImageFormat.Png);

        report.Mode.ShouldBe(ProcessMode.Streamed);
        output.Length.ShouldBeGreaterThan(0);
    }
}
