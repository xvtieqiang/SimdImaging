using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>The public tile entry: a plan plus a region source in, a buffer or a sink out.</summary>
public sealed class TilePublicEntryTests
{
    public static TheoryData<WorkingFormat> Formats => new()
    {
        WorkingFormat.Rgba8,
        WorkingFormat.Rgba16,
        WorkingFormat.Rgba32F,
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void ProcessTiles_MatchesThePlanResult(WorkingFormat format)
    {
        var plan = new ProcessingPlan().Add(PlanOps.Invert()).Add(PlanOps.GaussianBlur(1f));
        using var image = TestImages.Gradient(64, 48, format);

        using var expected = plan.Execute(image[0].Pixels.View);
        using var actual = Image.ProcessTiles(new ViewRegionSource(image[0].Pixels.View), plan, 16, 12);

        Rows(actual.View).ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void TheSinkOverload_HandsEveryTileToTheSink()
    {
        var plan = new ProcessingPlan().Add(PlanOps.Invert());
        using var image = TestImages.Gradient(50, 33, WorkingFormat.Rgba8);
        var sink = new CollectSink(50, 33, WorkingFormat.Rgba8);

        Image.ProcessTiles(new ViewRegionSource(image[0].Pixels.View), sink, plan, 16, 10);

        sink.Tiles.Count.ShouldBe(16);
        using var expected = plan.Execute(image[0].Pixels.View);
        sink.Bytes.ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void AGlobalPlan_IsRefusedWithTheCompilerReason()
    {
        var plan = new ProcessingPlan().Add(PlanOps.Equalize());
        using var image = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);

        var error = Should.Throw<NotSupportedException>(() =>
            Image.ProcessTiles(new ViewRegionSource(image[0].Pixels.View), plan, 16, 12));
        error.Message.ShouldContain("GlobalStep");
    }

    private static byte[] Rows(PixelView view)
    {
        var bytes = new byte[view.Height * view.RowBytes];
        for (var y = 0; y < view.Height; y++)
            view.Row(y).CopyTo(bytes.AsSpan(y * view.RowBytes, view.RowBytes));
        return bytes;
    }

    private sealed class ViewRegionSource(PixelView view) : IRegionSource
    {
        public int Width => view.Width;
        public int Height => view.Height;
        public WorkingFormat Format => view.Format;

        public void ReadRegion(PixelRect region, PixelView destination)
        {
            var bpp = view.Format.BytesPerPixel();
            for (var y = 0; y < region.Height; y++)
                view.Row(region.Y + y).Slice(region.X * bpp, region.Width * bpp).CopyTo(destination.Row(y));
        }
    }

    private sealed class CollectSink(int width, int height, WorkingFormat format) : IRegionSink
    {
        private readonly int _bpp = format.BytesPerPixel();

        public int Width => width;
        public int Height => height;
        public WorkingFormat Format => format;
        public byte[] Bytes { get; } = new byte[width * height * format.BytesPerPixel()];
        public List<PixelRect> Tiles { get; } = [];

        public void WriteRegion(PixelRect region, PixelView source)
        {
            for (var y = 0; y < region.Height; y++)
            {
                source.Row(y).CopyTo(Bytes.AsSpan((((region.Y + y) * Width) + region.X) * _bpp,
                    region.Width * _bpp));
            }

            Tiles.Add(region);
        }
    }
}
