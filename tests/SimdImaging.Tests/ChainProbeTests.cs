using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>The pull chain in isolation: a plan pumping rows out of a resize source must equal resize then plan.</summary>
public class PipelineChainTests
{
    private sealed class Rows(PixelView view) : IRowSource
    {
        private int _row;
        public int Width => view.Width;
        public int Height => view.Height;
        public WorkingFormat Format => view.Format;
        public int ReadRows(Span<byte> destination, int stride, int maxRows)
        {
            if (stride == 0) stride = view.RowBytes;
            var rows = Math.Min(maxRows, view.Height - _row);
            for (var y = 0; y < rows; y++) view.Row(_row + y).CopyTo(destination.Slice(y * stride, view.RowBytes));
            _row += rows;
            return rows;
        }
    }

    private sealed class Sink(int width, int height, WorkingFormat format) : IRowSink
    {
        private readonly int _rowBytes = width * format.BytesPerPixel();
        private int _row;
        public byte[] Bytes { get; } = new byte[width * height * format.BytesPerPixel()];
        public void WriteRows(ReadOnlySpan<byte> rows, int stride, int rowCount)
        {
            if (stride == 0) stride = _rowBytes;
            for (var y = 0; y < rowCount; y++)
                rows.Slice(y * stride, _rowBytes).CopyTo(Bytes.AsSpan((_row + y) * _rowBytes));
            _row += rowCount;
        }
    }

    [Fact]
    public void PlanOverAResizeSource_MatchesResizeThenPlan()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        using var resized = Resample.Resize(image[0].Pixels.View, 31, 23, Resampler.Bicubic, null);
        var plan = new ProcessingPlan().Add(PlanOps.Brightness(0.05f));

        using var pump = new PlanRowSource(plan, new Rows(resized.View), new ExecuteOptions());
        var sink = new Sink(31, 23, WorkingFormat.Rgba8);
        Pipeline.Copy(pump, sink, default);

        using var expected = plan.ExecuteWithOptions(resized.View, new ExecuteOptions(), out _);
        sink.Bytes.ShouldBe(expected.View.Span.ToArray());
    }

    /// <summary>
    /// The pump's windows come from the pool its caller supplied, and it rents exactly the number the estimator
    /// charges. Before this was threaded through, a streamed run silently fell back to the shared pool, so a counting
    /// or limiting pool saw nothing of its largest allocations.
    /// </summary>
    [Fact]
    public void ThePumpRentsItsWindowsFromTheCallersPool()
    {
        // Three steps is where the halos alternate through both ping-pong windows, so every window the accounting
        // charges is rented and every one goes back.
        using var image = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan()
            .Add(PlanOps.Invert()).Add(PlanOps.Contrast(1.1f)).Add(PlanOps.Grayscale());
        var pool = new CountingPool();

        using (var pump = new PlanRowSource(plan, new Rows(image[0].Pixels.View), new ExecuteOptions(), pool))
            Pipeline.Copy(pump, new Sink(32, 24, WorkingFormat.Rgba8), default);

        SequentialBands.Windows(plan.Steps).ShouldBe(4);
        pool.Rented.ShouldBe(4, "cache, output and the two scratch windows all come from the caller's pool");
        pool.Returned.ShouldBe(pool.Rented, "and every one of them goes back");
    }

    /// <summary>
    /// A two-step chain has one intermediate, so it rents three windows, not four: the last step writes the output and
    /// the second ping-pong slot is never touched. That spare was a quarter of the predicted peak on the shape a
    /// transcode benchmark actually runs (blur + invert), and the count is what the estimator charges — so the fix is
    /// one number in <c>SequentialBands.Windows</c>, not a special case here.
    /// </summary>
    [Fact]
    public void ATwoStepChainRentsOneIntermediateNotTwo()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Invert());
        var pool = new CountingPool();
        var sink = new Sink(64, 48, WorkingFormat.Rgba8);

        using (var pump = new PlanRowSource(plan, new Rows(image[0].Pixels.View), new ExecuteOptions(), pool))
            Pipeline.Copy(pump, sink, default);

        SequentialBands.Windows(plan.Steps).ShouldBe(3);
        pool.Rented.ShouldBe(3, "cache, output, one intermediate");
        pool.Returned.ShouldBe(3);

        using var whole = plan.Execute(image[0].Pixels.View);
        sink.Bytes.ShouldBe(whole.View.Span.ToArray(),
            "handing the same view for both ping-pong slots must not change a byte");
    }
}
