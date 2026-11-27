using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// A legal plan whose steps a rectangle cannot run must still produce the image. The tile entry point falls
/// back to whole-image execution rather than turning a backend limitation into a user-visible failure; an
/// explicit memory ceiling still refuses, because that is the caller's own constraint.
/// </summary>
public sealed class TileFallbackTests
{
    [Fact]
    public void APlanThatCannotTile_ReturnsTheWholeImageInsteadOfThrowing()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Equalize());
        var source = new ViewRegionSource(image[0].Pixels.View);

        using var tiled = Image.ProcessTiles(source, plan, 16, 16, new ExecuteOptions(), null, out _);
        using var expected = plan.Execute(image[0].Pixels.View);

        tiled.Width.ShouldBe(expected.Width);
        tiled.Height.ShouldBe(expected.Height);
        tiled.View.Span.ToArray().ShouldBe(expected.View.Span.ToArray());
    }

    [Fact]
    public void TheSinkForm_DeliversTheFallbackInBands()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Equalize());
        var source = new ViewRegionSource(image[0].Pixels.View);
        var sink = new BufferSink(64, 48, WorkingFormat.Rgba8);

        Image.ProcessTiles(source, sink, plan, 16, 16, new ExecuteOptions(), null, out _);

        using var expected = plan.Execute(image[0].Pixels.View);
        sink.Bytes.ShouldBe(expected.View.Span.ToArray());
    }

    [Fact]
    public void TheFallbackHonoursAnExplicitCeiling()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Equalize());
        var source = new ViewRegionSource(image[0].Pixels.View);

        var error = Should.Throw<InvalidOperationException>(() => Image.ProcessTiles(source, plan, 16, 16,
            new ExecuteOptions { MaxWorkingBytes = 1024 }, null, out _));
        error.Message.ShouldContain("MaxWorkingBytes");
    }

    private sealed class ViewRegionSource(PixelView view) : IRegionSource
    {
        public int Width => view.Width;
        public int Height => view.Height;
        public WorkingFormat Format => view.Format;

        public void ReadRegion(PixelRect region, PixelView destination)
        {
            var bytes = Format.BytesPerPixel();
            for (var y = 0; y < region.Height; y++)
            {
                view.Row(region.Y + y).Slice(region.X * bytes, region.Width * bytes)
                    .CopyTo(destination.Row(y).Slice(0, region.Width * bytes));
            }
        }
    }

    private sealed class BufferSink(int width, int height, WorkingFormat format) : IRegionSink
    {
        private readonly byte[] _bytes = new byte[width * height * format.BytesPerPixel()];

        public int Width => width;
        public int Height => height;
        public WorkingFormat Format => format;

        public byte[] Bytes => _bytes;

        public void WriteRegion(PixelRect region, PixelView source)
        {
            var bytes = format.BytesPerPixel();
            for (var y = 0; y < region.Height; y++)
            {
                source.Row(y).Slice(0, region.Width * bytes)
                    .CopyTo(_bytes.AsSpan(((region.Y + y) * width + region.X) * bytes, region.Width * bytes));
            }
        }
    }

    [Fact]
    public void TheEncoderEntry_EncodesTheFallbackInsteadOfThrowing()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Equalize());
        var source = new ViewRegionSource(image[0].Pixels.View);
        using var stream = new MemoryStream();

        var report = Image.ProcessTilesTo(source, stream, ImageFormat.Png, plan, 16, 16);

        report.Mode.ShouldBe(ProcessMode.Materialized);
        stream.Position = 0;
        using var actual = Image.Load(stream);
        using var expected = plan.Execute(image[0].Pixels.View);
        actual[0].Pixels.View.Span.ToArray().ShouldBe(expected.View.Span.ToArray());
    }

    [Fact]
    public void TheOutOverload_ReportsThePlannerDecision()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var source = new ViewRegionSource(image[0].Pixels.View);

        using var fallback = Image.ProcessTiles(source, new ProcessingPlan().Add(PlanOps.Equalize()),
            16, 16, options: new ExecuteOptions(), pool: null, out var fallbackReport);
        fallbackReport.SelectedMode.ShouldBe(ExecutionMode.Materialized);
        fallbackReport.RequestedPolicy.ShouldBe(ExecutionPolicy.Auto);
        fallbackReport.FallbackReason.ShouldBe(ExecutionFallbackReason.Capability);
        fallbackReport.Message.ShouldNotBeNull().ShouldContain("Global");

        using var tiled = Image.ProcessTiles(source, new ProcessingPlan().Add(PlanOps.Invert()),
            16, 16, options: new ExecuteOptions(), pool: null, out var tiledReport);
        tiledReport.SelectedMode.ShouldBe(ExecutionMode.Tiled);
        tiledReport.FallbackReason.ShouldBe(ExecutionFallbackReason.None);
        tiledReport.OutputStrategy.ShouldBe(OutputStrategy.Buffered);
    }
}
