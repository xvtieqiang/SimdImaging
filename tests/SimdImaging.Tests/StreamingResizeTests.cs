using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// The row-streaming resize against the whole-image one: same weights, same order, same bytes.
/// </summary>
public class StreamingResizeTests
{
    private sealed class RowSource(PixelView view) : IRowSource
    {
        private int _row;

        public int Width => view.Width;

        public int Height => view.Height;

        public WorkingFormat Format => view.Format;

        public int ReadRows(Span<byte> destination, int stride, int maxRows)
        {
            if (stride == 0)
                stride = view.RowBytes;
            var rows = Math.Min(maxRows, view.Height - _row);
            for (var y = 0; y < rows; y++)
                view.Row(_row + y).CopyTo(destination.Slice(y * stride, view.RowBytes));
            _row += rows;
            return rows;
        }
    }

    private sealed class CollectingSink(int width, int height, WorkingFormat format) : IRowSink
    {
        private readonly int _rowBytes = width * format.BytesPerPixel();
        private int _written;

        public byte[] Bytes { get; } = new byte[width * height * format.BytesPerPixel()];

        public void WriteRows(ReadOnlySpan<byte> rows, int stride, int rowCount)
        {
            if (stride == 0)
                stride = _rowBytes;
            if (_written + rowCount > height || (long)stride * rowCount > rows.Length)
                throw new InvalidOperationException($"sink overflow: {_written}+{rowCount} of {height}");

            for (var y = 0; y < rowCount; y++)
                rows.Slice(y * stride, _rowBytes).CopyTo(Bytes.AsSpan((_written + y) * _rowBytes));
            _written += rowCount;
        }
    }

    public static TheoryData<int, int, int, int> Sizes => new()
    {
        { 320, 200, 97, 61 },    // down, non-integer ratio
        { 100, 80, 517, 331 },   // up
        { 64, 48, 64, 48 },      // identity
        { 37, 23, 13, 9 },       // odd, down
    };

    public static TheoryData<string> Filters => new()
    {
        "Nearest", "Bilinear", "Bicubic", "Lanczos3", "Box",
    };

    private static Resampler Parse(string name) => name switch
    {
        "Nearest" => Resampler.Nearest,
        "Bilinear" => Resampler.Bilinear,
        "Bicubic" => Resampler.Bicubic,
        "Lanczos3" => Resampler.Lanczos3,
        _ => Resampler.Box,
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgba8_MatchesTheWholeImageResize(int sourceWidth, int sourceHeight, int width, int height)
    {
        foreach (var format in new[] { WorkingFormat.Rgba8, WorkingFormat.Rgba16 })
        {
            foreach (var filter in new[] { "Nearest", "Bilinear", "Bicubic", "Lanczos3", "Box" })
            {
                using var image = TestImages.Gradient(sourceWidth, sourceHeight, format);
                var resampler = Parse(filter);

                using var expected = Resample.Resize(image[0].Pixels.View, width, height, resampler, null);
                var sink = new CollectingSink(width, height, format);
                var rows = StreamingResize.Execute(new RowSource(image[0].Pixels.View), sink, width, height,
                    resampler);

                rows.ShouldBe(height);
                sink.Bytes.ShouldBe(expected.View.Span.ToArray(),
                    $"{format} {filter} {sourceWidth}x{sourceHeight} -> {width}x{height}");
            }
        }
    }

    /// <summary>A source that generates rows instead of holding them: the point is the ring's arithmetic, not
    /// the pixels.</summary>
    private sealed class SyntheticSource(int width, int height, WorkingFormat format) : IRowSource
    {
        public int Width => width;

        public int Height => height;

        public WorkingFormat Format => format;

        public int ReadRows(Span<byte> destination, int stride, int maxRows)
        {
            if (stride == 0)
                stride = width * format.BytesPerPixel();
            var rows = Math.Min(maxRows, height);
            // The test only needs the call to complete; the bytes are never compared.
            destination[..(stride * rows)].Clear();
            return rows;
        }
    }

    [Fact]
    public void An8KDownscale_FitsTheDefaultBudget()
    {
        // 7680x4320 -> 1920x1080 with the default 24 MiB budget: the ring is ~12.5 MiB and the staging chunk
        // ~2 MiB, so nothing is refused. Generated rows, so the test does not allocate the source image.
        var source = new SyntheticSource(7680, 4320, WorkingFormat.Rgba8);
        var sink = new CollectingSink(1920, 1080, WorkingFormat.Rgba8);

        StreamingResize.Execute(source, sink, 1920, 1080, Resampler.Bicubic).ShouldBe(1080);
    }

    [Fact]
    public void AHeavyDownscale_IsRefusedWhenTheRingWouldExceedTheBudget()
    {
        // 4000 wide to 4000 wide but 4000 tall to 4: ~1000 vertical taps, so the ring would be ~64 MiB, above the
        // 24 MiB this path allows. Refused before a buffer is rented.
        var source = new SyntheticSource(4000, 4000, WorkingFormat.Rgba8);
        var sink = new CollectingSink(4000, 4, WorkingFormat.Rgba8);

        var error = Should.Throw<InvalidOperationException>(() =>
            StreamingResize.Execute(source, sink, 4000, 4, Resampler.Box));
        error.Message.ShouldContain("ring");
    }
}
