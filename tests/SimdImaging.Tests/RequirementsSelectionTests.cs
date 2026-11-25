using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Contracts;

namespace SimdImaging.Tests;

/// <summary>
/// The orthogonal requirement flags are load-bearing: a row-partitionable step that declares per-call state or
/// source ordering is refused by the band/rectangle backends even though its category alone would allow them.
/// </summary>
public sealed class RequirementsSelectionTests
{
    [Fact]
    public void AStatefulRowStep_IsRefusedByStreamAndRegion()
    {
        var plan = new ProcessingPlan().Add(StatefulNeighborhood());

        plan.CanStream().ShouldBe(StreamRefusal.StatefulStep);
        plan.CanExecuteRegion().ShouldBe(RegionRefusal.StatefulStep);
    }

    [Fact]
    public void AStatefulRowStep_StillRunsInTheFrameExecutor()
    {
        using var image = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        using var expected = image.Pipe().Invert().ToImage();
        using var actual = new ProcessingPlan().Add(StatefulNeighborhood()).Execute(image[0].Pixels.View);

        actual.View.Span.ToArray().ShouldBe(expected.WritablePixels.ToArray());
    }

    [Fact]
    public void AStatefulRowStep_IsRefusedByTile()
    {
        using var image = TestImages.Gradient(24, 18, WorkingFormat.Rgba8);

        Should.Throw<NotSupportedException>(() =>
            Image.ProcessTiles(new ViewRegionSource(image[0].Pixels.View),
                new ProcessingPlan().Add(StatefulNeighborhood()), 8, 8));
    }

    /// <summary>A neighbourhood step whose declared state says "this depends on the call sequence".</summary>
    private static ImageOp StatefulNeighborhood()
    {
        var contract = OperatorContract.Create("stateful-invert", OpCategory.Neighborhood,
            WorkingFormatMask.All, IdentityFootprint.Instance, WorkspaceModel.Of((_, _, _, _) => 0L),
            requirements: ExecutionRequirements.RowBands with
            {
                State = StateScope.PerCall,
                Order = ExecutionOrder.SourceOrder,
            });

        return new ImageOp(contract, new KernelImplementations((source, destination, rect, _) =>
        {
            // Rgba8, leaving alpha alone, like PointOps.Invert.
            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            {
                var from = source.Row(y);
                var to = destination.Row(y);
                for (var x = rect.X; x < rect.X + rect.Width; x++)
                {
                    var o = x * 4;
                    to[o] = (byte)(255 - from[o]);
                    to[o + 1] = (byte)(255 - from[o + 1]);
                    to[o + 2] = (byte)(255 - from[o + 2]);
                    to[o + 3] = from[o + 3];
                }
            }
        }));
    }

    private sealed class ViewRegionSource(PixelView view) : IRegionSource
    {
        public int Width => view.Width;
        public int Height => view.Height;
        public WorkingFormat Format => view.Format;

        public void ReadRegion(PixelRect region, PixelView destination)
        {
            var bytes = view.Format.BytesPerPixel();
            for (var y = 0; y < region.Height; y++)
            {
                view.Row(region.Y + y).Slice(region.X * bytes, region.Width * bytes)
                    .CopyTo(destination.Row(y));
            }
        }
    }
}
