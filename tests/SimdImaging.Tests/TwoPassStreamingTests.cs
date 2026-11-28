using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The first two-pass streaming operator: Equalize analyzes the decoded rows for a LUT, then a second pass
/// applies that table as a point step. The pilot is limited to a path source (reopenable) and Rgba8, and must be
/// byte-identical to the materialised chain while staying below a full-frame working set.
/// </summary>
public sealed class TwoPassStreamingTests
{
    private const int Width = 2048;
    private const int Height = 4096;

    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), $"simd-two-pass-{Guid.NewGuid():N}{extension}");

    [Fact]
    public void EqualizeThenResize_StreamsInTwoPassesAndMatchesTheMaterialisedPath()
    {
        var source = Temp(".png");
        var streamed = Temp(".png");
        var materialized = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(Width, Height, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var explained = ImageTranscode.From(source).Equalize().Resize(1024, 768).Explain();
            explained.Backend.ShouldBe(PlanBackend.Stream);
            explained.Notes.ShouldContain(note => note.Contains("first pass", StringComparison.Ordinal));

            const long budget = 24L << 20;
            var workspace = new WorkspaceManager(budget);
            var report = ImageTranscode.From(source)
                .Equalize()
                .Resize(1024, 768)
                .WithOptions(TranscodeOptions.Default with
                {
                    Execute = new ExecuteOptions { Workspace = workspace, MaxWorkingBytes = budget },
                })
                .Run(streamed);

            report.Mode.ShouldBe(ProcessMode.Streamed,
                "a path source must take the two-pass row chain, not the materialised fallback");
            report.Refusal.ShouldBe(StreamRefusal.None);
            report.PeakWorkingBytes.ShouldBeLessThanOrEqualTo(budget);
            report.PeakWorkingBytes.ShouldBeLessThan((long)Width * Height * 4,
                "the two passes must stay below one full Rgba8 frame");

            // The same chain over a forward-only source cannot replay: it keeps the GlobalStep refusal and
            // materialises. That is the reference for output bytes, not a second implementation.
            var bytes = File.ReadAllBytes(source);
            using (var input = new MemoryStream(bytes))
            {
                var fallback = ImageTranscode.From(input)
                    .Equalize()
                    .Resize(1024, 768)
                    .Run(materialized);
                fallback.Mode.ShouldBe(ProcessMode.Materialized);
                fallback.Refusal.ShouldBe(StreamRefusal.GlobalStep);
            }

            using var streamedImage = Image.Load(streamed);
            using var materializedImage = Image.Load(materialized);
            streamedImage[0].Pixels.View.Span.ToArray()
                .ShouldBe(materializedImage[0].Pixels.View.Span.ToArray());
        }
        finally
        {
            foreach (var path in new[] { source, streamed, materialized })
                File.Delete(path);
        }
    }

    [Fact]
    public void ACropBeforeEqualize_KeepsTheMaterialisedPath()
    {
        var source = Temp(".png");
        var destination = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(256, 192, WorkingFormat.Rgba8))
                Image.Save(image, source);

            // The analysis must see the pixels the table is applied to; a leading crop would need its own pass.
            var report = ImageTranscode.From(source)
                .Crop(new PixelRect(16, 12, 128, 96))
                .Equalize()
                .Run(destination);

            report.Mode.ShouldBe(ProcessMode.Materialized);
            report.Refusal.ShouldBe(StreamRefusal.GlobalStep);
        }
        finally
        {
            foreach (var path in new[] { source, destination })
                File.Delete(path);
        }
    }

    [Fact]
    public void PeakWorkingSet_DoesNotGrowWithImageHeight()
    {
        // Same width, eight times the rows: the two-pass chain holds a band, the transfer buffer and the
        // analysis row buffer, all functions of width. If any stage materialised a frame, the tall run would
        // report eight times the working set instead of the same one.
        var shortFile = Temp(".png");
        var tallFile = Temp(".png");
        var shortOut = Temp(".png");
        var tallOut = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(1024, 1024, WorkingFormat.Rgba8))
                Image.Save(image, shortFile);
            using (var image = TestImages.Gradient(1024, 8192, WorkingFormat.Rgba8))
                Image.Save(image, tallFile);

            const long budget = 8L << 20;
            var shortReport = RunEqualize(shortFile, shortOut, budget);
            var tallReport = RunEqualize(tallFile, tallOut, budget);

            shortReport.Mode.ShouldBe(ProcessMode.Streamed);
            tallReport.Mode.ShouldBe(ProcessMode.Streamed);
            tallReport.PeakWorkingBytes.ShouldBe(shortReport.PeakWorkingBytes,
                "a row chain's working set is a function of width and band height, not image height");
            tallReport.PeakWorkingBytes.ShouldBeLessThan((long)1024 * 8192 * 4);
        }
        finally
        {
            foreach (var path in new[] { shortFile, tallFile, shortOut, tallOut })
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ParallelAnalysis_IsByteIdenticalAcrossDegrees(int degree)
    {
        // The analysis counts each worker's rows into its own histogram and merges them. Integer counts commute,
        // so the LUT — and the streamed image — must not depend on how many workers the option gives the pass.
        var source = Temp(".png");
        var single = Temp(".png");
        var parallel = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(512, 384, WorkingFormat.Rgba8))
                Image.Save(image, source);

            RunAtDegree(source, single, 1);
            RunAtDegree(source, parallel, degree);

            using var singleImage = Image.Load(single);
            using var parallelImage = Image.Load(parallel);
            parallelImage[0].Pixels.View.Span.ToArray()
                .ShouldBe(singleImage[0].Pixels.View.Span.ToArray());
        }
        finally
        {
            foreach (var path in new[] { source, single, parallel })
                File.Delete(path);
        }
    }

    private static void RunAtDegree(string source, string destination, int degree)
    {
        const long budget = 8L << 20;
        var workspace = new WorkspaceManager(budget);
        var report = ImageTranscode.From(source)
            .Equalize()
            .WithOptions(TranscodeOptions.Default with
            {
                Execute = new ExecuteOptions
                {
                    Workspace = workspace,
                    MaxWorkingBytes = budget,
                    MaxDegreeOfParallelism = degree,
                },
            })
            .Run(destination);
        report.Mode.ShouldBe(ProcessMode.Streamed);
    }

    private static ProcessReport RunEqualize(string source, string destination, long budget)
    {
        var workspace = new WorkspaceManager(budget);
        return ImageTranscode.From(source)
            .Equalize()
            .WithOptions(TranscodeOptions.Default with
            {
                Execute = new ExecuteOptions { Workspace = workspace, MaxWorkingBytes = budget },
            })
            .Run(destination);
    }

    /// <summary>
    /// The report's <c>SourceReads</c> / <c>Passes</c> / <c>ReplayRequired</c> describe what the run actually did.
    /// The two-pass path reopens the file — once for the histogram, once to apply the table — so reporting 1 is a
    /// measurement that is simply wrong, and the fields exist precisely so a caller can reason about cost.
    /// <para>
    /// Three cases, because a constant 2 would pass the first one alone: the two-pass chain must say 2, a
    /// single-pass chain on the same source must say 1, and a materialised fallback (the source cannot be
    /// replayed) must also say 1.
    /// </para>
    /// </summary>
    [Fact]
    public void ReportedSourceReadsMatchTheTwoPassPath()
    {
        var source = Temp(".png");
        var twoPassDestination = Temp(".png");
        var singlePassDestination = Temp(".png");
        var materializedDestination = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(256, 192, WorkingFormat.Rgba8))
                Image.Save(image, source);

            // Two passes: the path source is reopenable and Rgba8, so the analysis runs first.
            var twoPass = ImageTranscode.From(source).Equalize().Run(twoPassDestination);
            twoPass.Mode.ShouldBe(ProcessMode.Streamed);
            twoPass.Execution.SourceReads.ShouldBe(2, "the two-pass equalize traverses the source twice");
            twoPass.Execution.Passes.ShouldBe(2);
            twoPass.Execution.ReplayRequired.ShouldBeTrue("the source is reopened, so it must be replayable");

            // One pass: no analysis, so the counts are 1 and the source is read once.
            var singlePass = ImageTranscode.From(source).Invert().Run(singlePassDestination);
            singlePass.Execution.SourceReads.ShouldBe(1);
            singlePass.Execution.Passes.ShouldBe(1);
            singlePass.Execution.ReplayRequired.ShouldBeFalse();

            // A forward-only source cannot be replayed, so the chain materialises and reads the source once.
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new MemoryStream();
            var materialized = ImageTranscode.From(stream).Equalize()
                .Run(output, ImageFormat.Png);
            materialized.Mode.ShouldBe(ProcessMode.Materialized);
            materialized.Execution.SourceReads.ShouldBe(1,
                "a materialised run reads the source once, whatever the chain contains");
        }
        finally
        {
            foreach (var path in new[] { source, twoPassDestination, singlePassDestination, materializedDestination })
                File.Delete(path);
        }
    }
}
