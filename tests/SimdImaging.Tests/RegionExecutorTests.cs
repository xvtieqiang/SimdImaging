using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Internal;
using FusionCountingPool = SimdImaging.Processing.Fusion.CountingPool;

namespace SimdImaging.Tests;

/// <summary>
/// The region path: <see cref="ProcessingPlan.TryExecuteRegion"/> over an <see cref="IRegionSource"/>.
/// <para>Every equivalence test compares against the whole-image path after slicing the same rectangle. That is the
/// contract: a region run is the same pixels, reached by reading less — never an approximation.</para>
/// </summary>
public class RegionExecutorTests
{
    /// <summary>An image held in memory, read as rectangles: the reference <see cref="IRegionSource"/>.</summary>
    internal sealed class ArrayRegionSource(PixelView image) : IRegionSource
    {
        public int Width => image.Width;

        public int Height => image.Height;

        public WorkingFormat Format => image.Format;

        public int Calls { get; private set; }

        public PixelRect LargestRequest { get; private set; }

        public void ReadRegion(PixelRect region, PixelView destination)
        {
            if (!region.FitsIn(image.Width, image.Height))
                throw new ArgumentException($"The region {region} does not lie inside {image.Width}x{image.Height}.");
            if (destination.Width != region.Width || destination.Height != region.Height || destination.Format != image.Format)
                throw new ArgumentException("The destination does not match the region.");

            var rowBytes = region.Width * image.Format.BytesPerPixel();
            for (var y = 0; y < region.Height; y++)
            {
                image.Row(region.Y + y).Slice(region.X * image.Format.BytesPerPixel(), rowBytes)
                    .CopyTo(destination.Row(y));
            }

            Calls++;
            if ((long)region.Width * region.Height > (long)LargestRequest.Width * LargestRequest.Height)
                LargestRequest = region;
        }
    }

    /// <summary>An arbitrarily large image that is generated on demand — proves nothing is held whole.</summary>
    internal sealed class SyntheticRegionSource(int width, int height, WorkingFormat format) : IRegionSource
    {
        public int Width => width;

        public int Height => height;

        public WorkingFormat Format => format;

        public PixelRect LargestRequest { get; private set; }

        public void ReadRegion(PixelRect region, PixelView destination)
        {
            for (var y = 0; y < region.Height; y++)
            {
                var row = destination.Row(y);
                for (var x = 0; x < region.Width; x++)
                {
                    var index = ((region.Y + y) * 3) + (region.X + x) * 5;
                    var o = x * 4;
                    row[o] = (byte)index;
                    row[o + 1] = (byte)(index * 7);
                    row[o + 2] = (byte)(index * 13);
                    row[o + 3] = 255;
                }
            }

            if ((long)region.Width * region.Height > (long)LargestRequest.Width * LargestRequest.Height)
                LargestRequest = region;
        }
    }

    public static TheoryData<string, WorkingFormat> Chains => new()
    {
        { "empty", WorkingFormat.Rgba8 },
        { "point", WorkingFormat.Rgba8 },
        { "blur", WorkingFormat.Rgba8 },
        { "3x3", WorkingFormat.Rgba8 },
        { "point", WorkingFormat.Rgba16 },
        { "blur", WorkingFormat.Rgba16 },
        { "mixed", WorkingFormat.Rgba16 },
        { "empty", WorkingFormat.Rgba16 },
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

    public static TheoryData<string, int, int, int, int> Rects => new()
    {
        { "top-left", 0, 0, 10, 10 },
        { "bottom-right", 43, 27, 10, 10 },
        { "whole", 0, 0, 53, 37 },
        { "interior", 13, 11, 7, 5 },
        { "column", 20, 0, 1, 37 },
        { "row", 0, 20, 53, 1 },
    };

    [Theory]
    [MemberData(nameof(Chains))]
    public void Region_MatchesTheWholeImageSlice(string chain, WorkingFormat format)
    {
        using var image = TestImages.Gradient(53, 37, format);
        var plan = Build(chain);
        using var whole = plan.ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions(), out _);

        using var destination = WorkingBuffer.Rent(53, 37, format);
        var result = plan.TryExecuteRegion(new ArrayRegionSource(image[0].Pixels.View),
            new PixelRect(0, 0, 53, 37), destination.View);

        result.Succeeded.ShouldBeTrue($"{chain}/{format}: {result.Refusal} at '{result.RefusingStep}'");
        destination.View.Span.ToArray().ShouldBe(whole.View.Span.ToArray());
    }

    [Theory]
    [MemberData(nameof(Rects))]
    public void Region_Rectangle_MatchesTheWholeImageSlice(string name, int x, int y, int width, int height)
    {
        _ = name;
        using var image = TestImages.Gradient(53, 37, WorkingFormat.Rgba8);
        var plan = Build("mixed");
        using var whole = plan.ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions(), out _);

        var region = new PixelRect(x, y, width, height);
        using var destination = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8);
        plan.TryExecuteRegion(new ArrayRegionSource(image[0].Pixels.View), region, destination.View)
            .Succeeded.ShouldBeTrue();

        var expected = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            whole.View.Row(y + row).Slice(x * 4, width * 4).CopyTo(expected.AsSpan(row * width * 4));

        destination.View.Span.ToArray().ShouldBe(expected, $"region {region}");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 1)]
    [InlineData(64, 4)]
    [InlineData(7, 4)]
    public void BandHeightAndDegree_DoNotChangeTheBytes(int bandRows, int degree)
    {
        using var image = TestImages.Gradient(41, 29, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan { BandRows = bandRows }
            .Add(PlanOps.Brightness(0.05f)).Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Contrast(1.1f));
        using var whole = plan.ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions(), out _);

        var region = new PixelRect(9, 5, 23, 17);
        using var destination = WorkingBuffer.Rent(23, 17, WorkingFormat.Rgba8);
        plan.TryExecuteRegion(new ArrayRegionSource(image[0].Pixels.View), region, destination.View,
            new ExecuteOptions { MaxDegreeOfParallelism = degree }).Succeeded.ShouldBeTrue();

        var expected = new byte[23 * 17 * 4];
        for (var row = 0; row < 17; row++)
            whole.View.Row(5 + row).Slice(9 * 4, 23 * 4).CopyTo(expected.AsSpan(row * 23 * 4));
        destination.View.Span.ToArray().ShouldBe(expected, $"band {bandRows}, degree {degree}");
    }

    /// <summary>A 5×3 box mean, written as an internal step so X and Y halos differ: every <see cref="PlanOps"/>
    /// operator has <c>HaloX == HaloY</c>, so without this the horizontal halo is never exercised.</summary>
    private static OpKernel Box(int haloX, int haloY) => (source, destination, rect, _) =>
    {
        var count = (2 * haloX + 1) * (2 * haloY + 1);
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            var outRow = destination.Row(y);
            for (var x = rect.X; x < rect.X + rect.Width; x++)
            {
                var sum = 0;
                for (var dy = -haloY; dy <= haloY; dy++)
                {
                    var row = source.Row(Math.Clamp(y + dy, 0, source.Height - 1));
                    for (var dx = -haloX; dx <= haloX; dx++)
                    {
                        var column = Math.Clamp(x + dx, 0, source.Width - 1);
                        sum += row[column * 4];
                    }
                }

                var value = (byte)(sum / count);
                var o = x * 4;
                outRow[o] = value;
                outRow[o + 1] = value;
                outRow[o + 2] = value;
                outRow[o + 3] = 255;
            }
        }
    };

    [Fact]
    public void AsymmetricHalo_IsCorrectInBothAxes()
    {
        using var image = TestImages.Gradient(45, 31, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan();
        plan.Add("box5x3", OpCategory.Neighborhood, Box(2, 1), 2, 1, null);
        using var whole = plan.ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions(), out _);

        var region = new PixelRect(7, 6, 19, 11);
        using var destination = WorkingBuffer.Rent(19, 11, WorkingFormat.Rgba8);
        plan.TryExecuteRegion(new ArrayRegionSource(image[0].Pixels.View), region, destination.View)
            .Succeeded.ShouldBeTrue();

        for (var row = 0; row < 11; row++)
        {
            whole.View.Row(6 + row).Slice(7 * 4, 19 * 4)
                .SequenceEqual(destination.View.Row(row)).ShouldBeTrue($"row {row} differs");
        }
    }

    [Fact]
    public void HaloLargerThanTheRegion_StillMatches()
    {
        using var image = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.GaussianBlur(6f));
        using var whole = plan.ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions(), out _);

        var region = new PixelRect(15, 11, 1, 1);
        using var destination = WorkingBuffer.Rent(1, 1, WorkingFormat.Rgba8);
        plan.TryExecuteRegion(new ArrayRegionSource(image[0].Pixels.View), region, destination.View)
            .Succeeded.ShouldBeTrue();

        destination.View.Span.ToArray().ShouldBe(whole.View.Row(11).Slice(15 * 4, 4).ToArray());
    }

    [Fact]
    public void GlobalStep_IsRefused_AndNothingIsWritten()
    {
        using var image = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Equalize());
        plan.CanExecuteRegion().ShouldBe(RegionRefusal.GlobalStep);

        using var destination = WorkingBuffer.Rent(8, 8, WorkingFormat.Rgba8, clear: true);
        var result = plan.TryExecuteRegion(new ArrayRegionSource(image[0].Pixels.View),
            new PixelRect(4, 4, 8, 8), destination.View);

        result.Succeeded.ShouldBeFalse();
        result.Refusal.ShouldBe(RegionRefusal.GlobalStep);
        result.RefusingStep.ShouldBe("Equalize");
        destination.View.Span.ToArray().ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void CompositeStep_IsRefused()
    {
        using var image = TestImages.Gradient(16, 16, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan();
        plan.Add("blend", OpCategory.Composite, (_, _, _, _) => { }, 0, 0, null);

        plan.CanExecuteRegion().ShouldBe(RegionRefusal.CompositeStep);
    }

    [Fact]
    public void AStepThatReadsTheWholeImage_IsRefused()
    {
        float[] kernel = [1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9, 1f / 9];
        var plan = new ProcessingPlan().Add(PlanOps.Convolve(kernel, 1, BorderMode.Mirror));
        plan.CanExecuteRegion().ShouldBe(RegionRefusal.StepNeedsWholeSource);
    }

    [Fact]
    public void Cancellation_ThrowsBeforeAnythingIsWritten()
    {
        using var image = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.GaussianBlur(1f));
        using var destination = WorkingBuffer.Rent(8, 8, WorkingFormat.Rgba8, clear: true);

        Should.Throw<OperationCanceledException>(() => plan.TryExecuteRegion(
            new ArrayRegionSource(image[0].Pixels.View), new PixelRect(4, 4, 8, 8), destination.View,
            new ExecuteOptions { CancellationToken = new CancellationToken(canceled: true) }));

        destination.View.Span.ToArray().ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void WorkingSet_DoesNotGrowWithTheSource()
    {
        static long Peak(int width, int height)
        {
            var source = new SyntheticRegionSource(width, height, WorkingFormat.Rgba8);
            var plan = new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.GaussianBlur(1f));
            var pool = new FusionCountingPool(ArrayPixelPool.Shared);
            using var destination = WorkingBuffer.Rent(64, 64, WorkingFormat.Rgba8);
            plan.TryExecuteRegion(source, new PixelRect(0, 0, 64, 64), destination.View, null, pool)
                .Succeeded.ShouldBeTrue();

            // The window the source was asked for proves nothing larger was read.
            source.LargestRequest.Width.ShouldBeLessThanOrEqualTo(64 + (2 * 3));
            source.LargestRequest.Height.ShouldBeLessThanOrEqualTo(64 + (2 * 3));
            return pool.PeakBytes;
        }

        var small = Peak(512, 512);
        var huge = Peak(100_000, 100_000);
        huge.ShouldBeLessThanOrEqualTo((long)(small * 1.25), $"peak {huge} for 100000² vs {small} for 512²");
    }
}
