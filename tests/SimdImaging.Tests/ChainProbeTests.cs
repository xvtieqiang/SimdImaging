using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

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
}
