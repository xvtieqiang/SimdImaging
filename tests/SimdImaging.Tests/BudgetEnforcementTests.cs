using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// The compiled executor allocates some full frames itself — a format section's output, a geometry reshape, a tile
/// buffer — outside the plan's own estimator. Those allocations now pass the same MaxWorkingBytes check as the
/// plan, the window and the resize ring.
/// </summary>
public sealed class BudgetEnforcementTests
{
    [Fact]
    public void AFormatStepBeyondTheBudget_IsRefused()
    {
        var recipe = Recipe(PlanOps.DistanceTransform());
        using var image = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        // The compile-time gate uses the whole-plan upper bound; this test exercises the executor's own check when
        // the budget is supplied only at execution time.
        var plan = Compile(recipe, new ExecuteOptions());
        var execute = new ExecuteOptions { MaxWorkingBytes = 1024 };

        var error = Should.Throw<InvalidOperationException>(() =>
            CompiledPlanExecutor.ExecuteFrame(plan, image[0].Pixels.View, options: execute));
        error.Message.ShouldContain("format step");
        error.Message.ShouldContain("MaxWorkingBytes");
    }

    [Fact]
    public void AGeometryStepBeyondTheBudget_IsRefused()
    {
        var recipe = Recipe(GeometrySteps.Resize(64, 64, Resampler.Bicubic));
        using var image = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        var plan = Compile(recipe, new ExecuteOptions());
        var execute = new ExecuteOptions { MaxWorkingBytes = 1024 };

        var error = Should.Throw<InvalidOperationException>(() =>
            CompiledPlanExecutor.ExecuteFrame(plan, image[0].Pixels.View, options: execute));
        error.Message.ShouldContain("geometry step");
    }

    [Fact]
    public void AContentDerivedGeometry_BeyondTheBudget_IsRefusedBeforeTheAllocation()
    {
        var plan = Compile(Recipe(GeometrySteps.Trim(0f)), new ExecuteOptions());
        using var image = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        // The output size is only known after reading pixels, but an upper bound is: input frame + input-sized
        // output + the declared row scratch. The executor refuses on that bound before trimming.
        var execute = new ExecuteOptions { MaxWorkingBytes = 1024 };

        var error = Should.Throw<InvalidOperationException>(() =>
            CompiledPlanExecutor.ExecuteFrame(plan, image[0].Pixels.View, options: execute));
        error.Message.ShouldContain("geometry step");
        error.Message.ShouldContain("MaxWorkingBytes");
    }

    [Fact]
    public void AContentDerivedGeometry_IsBoundedAndDeclaresItsScratch()
    {
        var plan = Compile(Recipe(GeometrySteps.Trim(0f)), new ExecuteOptions { MaxWorkingBytes = 1 << 20 });

        plan.Workspace.IsLowerBound.ShouldBeFalse("a declared content-derived crop is bounded by its input size");
        plan.Workspace.Terms.ShouldContain(term =>
            term.Kind == WorkspaceTermKind.GeometryScratch && term.Bytes > 0);
        plan.Workspace.Terms.ShouldContain(term =>
            term.Kind == WorkspaceTermKind.SectionTransition && term.Bytes > 0);
    }

    [Fact]
    public void APlanBeyondTheBudget_IsRefusedAtCompileTime()
    {
        var recipe = Recipe(PlanOps.GaussianBlur(1f), PlanOps.Invert());

        var error = Should.Throw<InvalidOperationException>(() =>
            Compile(recipe, new ExecuteOptions { MaxWorkingBytes = 1024 }));
        error.Message.ShouldContain("estimated peak");
        error.Message.ShouldContain("MaxWorkingBytes");
    }

    [Fact]
    public void ATileBeyondTheBudget_IsRefused()
    {
        using var image = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        var tilePlan = PipelineCompiler.Compile(Recipe(PlanOps.Invert()), new CompileOptions
        {
            SourceWidth = 32,
            SourceHeight = 32,
            SourceFormat = WorkingFormat.Rgba8,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion,
            Sink = SinkCapabilities.RandomRegionWrite,
            TileRequested = true,
        });

        var error = Should.Throw<InvalidOperationException>(() =>
            CompiledPlanExecutor.ExecuteTiles(tilePlan, new ViewRegionSource(image[0].Pixels.View),
                new DiscardSink(32, 32), 16, 12, new ExecuteOptions { MaxWorkingBytes = 512 }));
        error.Message.ShouldContain("tile");
    }

    [Fact]
    public void TileBudget_CombinesTargetAndRegionWindows()
    {
        using var image = TestImages.Gradient(16, 16, WorkingFormat.Rgba8);
        var source = new ViewRegionSource(image[0].Pixels.View);
        var plan = new ProcessingPlan().Add(PlanOps.Invert());

        // One 16x16 target tile (1024 bytes) and two 16x16 windows (2048 bytes) are resident together.
        Should.Throw<InvalidOperationException>(() =>
            Image.ProcessTiles(source, new DiscardSink(16, 16), plan, 16, 16,
                new ExecuteOptions { MaxWorkingBytes = 2500, MaxDegreeOfParallelism = 1 }, null, out _));
    }

    [Fact]
    public void BufferReturningTileBudget_IncludesFullFrameResult()
    {
        using var image = TestImages.Gradient(16, 16, WorkingFormat.Rgba8);
        var source = new ViewRegionSource(image[0].Pixels.View);
        var plan = new ProcessingPlan().Add(PlanOps.Invert());

        // The returned 16x16 frame (1024 bytes) coexists with the 16x16 tile and its two windows (3072 bytes).
        Should.Throw<InvalidOperationException>(() =>
            Image.ProcessTiles(source, plan, 16, 16,
                new ExecuteOptions { MaxWorkingBytes = 4000, MaxDegreeOfParallelism = 1 }, null, out _));
    }

    [Fact]
    public void TileBudget_UsesTileFootprintInsteadOfWholeFrame()
    {
        using var image = TestImages.Gradient(512, 512, WorkingFormat.Rgba8);
        var source = new ViewRegionSource(image[0].Pixels.View);
        var plan = new ProcessingPlan().Add(PlanOps.Invert());

        // A 16x16 tile walk holds the tile, two windows and the point step's declared scratch (about 5 KiB);
        // any whole-frame accounting would need the 512x512 frame's 1 MiB and refuse this budget.
        Image.ProcessTiles(source, new DiscardSink(512, 512), plan, 16, 16,
            new ExecuteOptions { MaxWorkingBytes = 8192, MaxDegreeOfParallelism = 1 }, null, out _);
    }

    [Fact]
    public void WithinTheBudget_EverythingRuns()
    {
        using var image = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        var formatPlan = Compile(Recipe(PlanOps.DistanceTransform()),
            new ExecuteOptions { MaxWorkingBytes = 1 << 20 });
        using var formatted = CompiledPlanExecutor.ExecuteFrame(formatPlan, image[0].Pixels.View);
        formatted.Format.ShouldBe(WorkingFormat.Rgba32F);

        using var tiled = Image.ProcessTiles(new ViewRegionSource(image[0].Pixels.View),
            new ProcessingPlan().Add(PlanOps.Invert()), 16, 12, new ExecuteOptions { MaxWorkingBytes = 1 << 20 }, null, out _);
        tiled.Width.ShouldBe(32);
    }

    private static PipelineRecipe Recipe(params object[] steps)
    {
        var builder = new PipelineRecipeBuilder();
        foreach (var step in steps)
        {
            if (step is ImageOp op)
                builder.Append(op);
            else
                builder.Append((Processing.Compilation.GeometryDescriptor)step);
        }

        return builder.Build();
    }

    private static CompiledExecutionPlan Compile(PipelineRecipe recipe, ExecuteOptions execute) =>
        PipelineCompiler.Compile(recipe, new CompileOptions
        {
            SourceWidth = 32,
            SourceHeight = 32,
            SourceFormat = WorkingFormat.Rgba8,
            Sink = SinkCapabilities.None,
            Execute = execute,
        });

    private sealed class DiscardSink(int width, int height) : IRegionSink
    {
        public int Width => width;
        public int Height => height;
        public WorkingFormat Format => WorkingFormat.Rgba8;

        public void WriteRegion(PixelRect region, PixelView source)
        {
        }
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
}
