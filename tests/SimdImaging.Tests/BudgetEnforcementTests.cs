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
    public void APlanBeyondTheBudget_IsRefusedAtCompileTime()
    {
        var recipe = Recipe(PlanOps.GaussianBlur(1f), PlanOps.Invert());

        var error = Should.Throw<InvalidOperationException>(() =>
            Compile(recipe, new ExecuteOptions { MaxWorkingBytes = 1024 }));
        error.Message.ShouldContain("working set");
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
    public void WithinTheBudget_EverythingRuns()
    {
        using var image = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        var formatPlan = Compile(Recipe(PlanOps.DistanceTransform()),
            new ExecuteOptions { MaxWorkingBytes = 1 << 20 });
        using var formatted = CompiledPlanExecutor.ExecuteFrame(formatPlan, image[0].Pixels.View);
        formatted.Format.ShouldBe(WorkingFormat.Rgba32F);

        using var tiled = Image.ProcessTiles(new ViewRegionSource(image[0].Pixels.View),
            new ProcessingPlan().Add(PlanOps.Invert()), 16, 12, new ExecuteOptions { MaxWorkingBytes = 1 << 20 });
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
