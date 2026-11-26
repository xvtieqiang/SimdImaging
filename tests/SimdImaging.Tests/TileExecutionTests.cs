using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// The tile backend is a walk over rectangles: each tile is executed by the region executor, which reads the halo
/// and writes the tile to an <see cref="IRegionSink"/>. These tests compare tiled output with the frame result at
/// several tile sizes, and pin the reasons the compiler still refuses a tile request.
/// </summary>
public sealed class TileExecutionTests
{
    public static TheoryData<WorkingFormat> Formats => new()
    {
        WorkingFormat.Rgba8,
        WorkingFormat.Rgba16,
        WorkingFormat.Rgba32F,
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void PointAndBlur_TiledMatchesFrame(WorkingFormat format)
    {
        var recipe = Recipe(PlanOps.Invert(), PlanOps.GaussianBlur(1f));
        using var image = TestImages.Gradient(64, 48, format);
        var frame = FrameBytes(recipe, image[0].Pixels.View, format);

        foreach (var (width, height) in new[] { (16, 12), (7, 5), (64, 48) })
        {
            TileBytes(recipe, image[0].Pixels.View, format, width, height)
                .ShouldBe(frame, $"{format}, tile {width}x{height}");
        }
    }

    [Fact]
    public void ATileRequest_ChoosesTheTileBackend_WhenTheSinkCanWriteRectangles()
    {
        var plan = Compile(Recipe(PlanOps.Invert()), TileOptions(64, 48, WorkingFormat.Rgba8));

        plan.Backend.ShouldBe(ExecutionBackend.Tile);
        plan.TileRefusal.ShouldBeNull();
    }

    [Fact]
    public void AGlobalStep_RefusesTile()
    {
        var plan = Compile(Recipe(PlanOps.Equalize()), TileOptions(64, 48, WorkingFormat.Rgba8));

        plan.Backend.ShouldBe(ExecutionBackend.TileRefused);
        plan.TileRefusal.ShouldBe(TileRefusal.GlobalStep);
    }

    [Fact]
    public void ASinkWithoutRectangles_RefusesTile()
    {
        var options = TileOptions(64, 48, WorkingFormat.Rgba8) with { Sink = SinkCapabilities.None };
        var plan = Compile(Recipe(PlanOps.Invert()), options);

        plan.TileRefusal.ShouldBe(TileRefusal.NoRegionSink);
    }

    [Fact]
    public void AWholeSourceStep_RefusesTile()
    {
        var recipe = Recipe(PlanOps.Convolve(new float[9], 1, BorderMode.Mirror));
        var plan = Compile(recipe, TileOptions(64, 48, WorkingFormat.Rgba8));

        plan.TileRefusal.ShouldBe(TileRefusal.WholeSourceStep);
    }

    [Fact]
    public void ABlockAlignedStep_RefusesTile()
    {
        var plan = Compile(Recipe(PlanOps.Pixelate(8)), TileOptions(64, 48, WorkingFormat.Rgba8));

        plan.TileRefusal.ShouldBe(TileRefusal.BlockAlignedOperator);
    }

    [Fact]
    public void AFormatChange_RefusesTile()
    {
        var plan = Compile(Recipe(PlanOps.DistanceTransform()), TileOptions(64, 48, WorkingFormat.Rgba8));

        plan.TileRefusal.ShouldBe(TileRefusal.FormatChange);
    }

    [Fact]
    public void TileExecution_HoldsWellUnderOneFrame()
    {
        var plan = new ProcessingPlan().Add(PlanOps.Invert()).Add(PlanOps.GaussianBlur(1f));
        using var image = TestImages.Gradient(512, 512, WorkingFormat.Rgba8);
        var pool = new SimdImaging.Processing.Fusion.CountingPool(ArrayPixelPool.Shared);

        Image.ProcessTiles(new BufferRegionSource(image[0].Pixels.View), new DiscardSink(512, 512),
            plan, 64, 64, pool: pool);

        pool.PeakBytes.ShouldBeLessThan(512L * 512 * 4 / 2,
            $"a 64x64 tile should not need half a 512x512 frame; peaked at {pool.PeakBytes}");
    }

    /// <summary>
    /// The tile the walk rents is the destination of the per-rectangle work, so it is reserved once, by the walk,
    /// before the rent. Before this the region executor added the same tile to its own estimate, and a limit of
    /// "one tile plus its windows" was refused as if two tiles were alive. A 16x16 single-step walk holds exactly
    /// one tile and two windows; the limit below is that sum, so a second tile entry would throw.
    /// </summary>
    [Fact]
    public void ATileWalk_CountsTheTargetTileOnce()
    {
        const int size = 16;
        const long tileBytes = (long)size * size * 4;
        const long windowBytes = 2 * tileBytes;
        var step = PlanOps.Invert();
        var scratchBytes = step.Contract.Workspace
            .EstimatePerWorker(size, size, size, WorkingFormat.Rgba8);
        using var image = TestImages.Gradient(size, size, WorkingFormat.Rgba8);
        var workspace = new WorkspaceManager(tileBytes + windowBytes + scratchBytes);

        Image.ProcessTiles(new BufferRegionSource(image[0].Pixels.View), new DiscardSink(size, size),
            new ProcessingPlan().Add(step), size, size,
            new ExecuteOptions { Workspace = workspace, MaxDegreeOfParallelism = 1 });

        workspace.PeakBytes.ShouldBe(tileBytes + windowBytes + scratchBytes,
            "one target tile, its windows and the operator's declared scratch, not the tile twice");
        workspace.OutstandingBytes.ShouldBe(0);
    }

    [Fact]
    public void TheTilesCoverEveryPixelExactlyOnce()
    {
        var recipe = Recipe(PlanOps.Invert());
        using var image = TestImages.Gradient(50, 33, WorkingFormat.Rgba8);
        var sink = new BufferRegionSink(50, 33, WorkingFormat.Rgba8);
        var plan = Compile(recipe, TileOptions(50, 33, WorkingFormat.Rgba8));

        CompiledPlanExecutor.ExecuteTiles(plan, new BufferRegionSource(image[0].Pixels.View), sink, 16, 10);

        sink.Tiles.Count.ShouldBe(4 * 4);
        var covered = new int[50 * 33];
        foreach (var tile in sink.Tiles)
        {
            for (var y = tile.Y; y < tile.Y + tile.Height; y++)
            for (var x = tile.X; x < tile.X + tile.Width; x++)
                covered[(y * 50) + x]++;
        }

        covered.ShouldAllBe(count => count == 1);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static PipelineRecipe Recipe(params ImageOp[] steps)
    {
        var builder = new PipelineRecipeBuilder();
        foreach (var step in steps)
            builder.Append(step);
        return builder.Build();
    }

    private static CompileOptions TileOptions(int width, int height, WorkingFormat format) => new()
    {
        SourceWidth = width,
        SourceHeight = height,
        SourceFormat = format,
        Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion,
        Sink = SinkCapabilities.RandomRegionWrite,
        TileRequested = true,
    };

    private static CompileOptions FrameOptions(int width, int height, WorkingFormat format) => new()
    {
        SourceWidth = width,
        SourceHeight = height,
        SourceFormat = format,
        Sink = SinkCapabilities.None,
    };

    private static CompiledExecutionPlan Compile(PipelineRecipe recipe, CompileOptions options) =>
        PipelineCompiler.Compile(recipe, options);

    private static byte[] FrameBytes(PipelineRecipe recipe, PixelView view, WorkingFormat format)
    {
        var plan = Compile(recipe, FrameOptions(view.Width, view.Height, format));
        using var result = CompiledPlanExecutor.ExecuteFrame(plan, view);
        return Rows(result.View);
    }

    private static byte[] TileBytes(PipelineRecipe recipe, PixelView view, WorkingFormat format, int tileWidth,
        int tileHeight)
    {
        var plan = Compile(recipe, TileOptions(view.Width, view.Height, format));
        plan.Backend.ShouldBe(ExecutionBackend.Tile);

        var sink = new BufferRegionSink(view.Width, view.Height, format);
        CompiledPlanExecutor.ExecuteTiles(plan, new BufferRegionSource(view), sink, tileWidth, tileHeight);
        return sink.Bytes;
    }

    private static byte[] Rows(PixelView view)
    {
        var bytes = new byte[view.Height * view.RowBytes];
        for (var y = 0; y < view.Height; y++)
            view.Row(y).CopyTo(bytes.AsSpan(y * view.RowBytes, view.RowBytes));
        return bytes;
    }

    private sealed class BufferRegionSource(PixelView view) : IRegionSource
    {
        public int Width => view.Width;
        public int Height => view.Height;
        public WorkingFormat Format => view.Format;

        public void ReadRegion(PixelRect region, PixelView destination)
        {
            var bpp = view.Format.BytesPerPixel();
            for (var y = 0; y < region.Height; y++)
            {
                view.Row(region.Y + y).Slice(region.X * bpp, region.Width * bpp)
                    .CopyTo(destination.Row(y));
            }
        }
    }

    private sealed class DiscardSink(int width, int height) : IRegionSink
    {
        public int Width => width;
        public int Height => height;
        public WorkingFormat Format => WorkingFormat.Rgba8;

        public void WriteRegion(PixelRect region, PixelView source)
        {
            if (region.Width != source.Width || region.Height != source.Height)
                throw new ArgumentException("The source view is not the region's size.");
        }
    }

    private sealed class BufferRegionSink(int width, int height, WorkingFormat format) : IRegionSink
    {
        private readonly int _bpp = format.BytesPerPixel();

        public int Width => width;
        public int Height => height;
        public WorkingFormat Format => format;

        public byte[] Bytes { get; } = new byte[width * height * format.BytesPerPixel()];

        public List<PixelRect> Tiles { get; } = [];

        public void WriteRegion(PixelRect region, PixelView source)
        {
            if (region.Width != source.Width || region.Height != source.Height)
                throw new ArgumentException("The source view is not the region's size.");
            for (var y = 0; y < region.Height; y++)
            {
                source.Row(y).CopyTo(Bytes.AsSpan((((region.Y + y) * Width) + region.X) * _bpp,
                    region.Width * _bpp));
            }

            Tiles.Add(region);
        }
    }
}
