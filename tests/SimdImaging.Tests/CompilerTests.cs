using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Geometry;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// The compiler is the one place that decides geometry boundaries, fusion and the backend. These tests pin the
/// decisions, not the pixels: execution still goes through the existing executors, so the byte-identity tests of
/// those executors remain the correctness gate.
/// </summary>
public sealed class CompilerTests
{
    private static CompileOptions Streamable(int width = 320, int height = 200) => new()
    {
        SourceWidth = width,
        SourceHeight = height,
        SourceFormat = WorkingFormat.Rgba8,
        Source = SourceCapabilities.ForwardOnly,
        Sink = SinkCapabilities.RowWritable,
    };

    private static PipelineRecipe Recipe(params object[] steps)
    {
        var builder = new PipelineRecipeBuilder();
        foreach (var step in steps)
        {
            if (step is ImageOp op)
                builder.Append(op);
            else
                builder.Append((GeometryDescriptor)step);
        }

        return builder.Build();
    }

    private static GeometryDescriptor FixedGeometry(int width, int height, bool wrap = true)
    {
        WrapRowSource? wrapper = wrap ? (source, pool, execute) => source : null;
        return new($"resize-{width}x{height}", new FixedSizeRule(width, height),
            (source, pool) => Transforms.Copy(source, pool), wrapper);
    }

    [Fact]
    public void SectionsSplitAtGeometry_AndKeepOperatorOrder()
    {
        var recipe = Recipe(PlanOps.Invert(), PlanOps.GaussianBlur(1f), FixedGeometry(64, 40), PlanOps.Contrast(1.1f));

        var plan = PipelineCompiler.Compile(recipe, Streamable());

        plan.Steps.Count.ShouldBe(3);
        plan.Sections.Count.ShouldBe(3);
        plan.Sections[0].Kind.ShouldBe(CompiledSectionKind.OperatorRun);
        plan.Sections[0].FirstStep.ShouldBe(0);
        plan.Sections[0].StepCount.ShouldBe(2);
        plan.Sections[1].Kind.ShouldBe(CompiledSectionKind.Geometry);
        plan.Sections[1].RecipeIndex.ShouldBe(2);
        plan.Sections[2].Kind.ShouldBe(CompiledSectionKind.OperatorRun);
        plan.Sections[2].FirstStep.ShouldBe(2);
        plan.Sections[2].StepCount.ShouldBe(1);
        plan.Steps[2].Name.ShouldBe("Contrast");
    }

    [Fact]
    public void GeometrySizesFoldInOrder()
    {
        var recipe = Recipe(PlanOps.Invert(), FixedGeometry(100, 50), FixedGeometry(20, 10));

        var plan = PipelineCompiler.Compile(recipe, Streamable());

        plan.SizeKnown.ShouldBeTrue();
        plan.Width.ShouldBe(20);
        plan.Height.ShouldBe(10);
    }

    [Fact]
    public void ContentDerivedGeometry_ReportsAnUnknownSize()
    {
        var recipe = new PipelineRecipeBuilder()
            .Append(new GeometryDescriptor("trim", new ContentDerivedRule(),
                (source, pool) => Transforms.Copy(source, pool)))
            .Build();

        var plan = PipelineCompiler.Compile(recipe, Streamable());

        plan.SizeKnown.ShouldBeFalse();
    }

    [Fact]
    public void StreamIsChosen_WhenEveryStepAndGeometryCanStream()
    {
        var plan = PipelineCompiler.Compile(
            Recipe(PlanOps.Invert(), PlanOps.GaussianBlur(1f), FixedGeometry(64, 40), PlanOps.Contrast(1.1f)),
            Streamable());

        plan.Backend.ShouldBe(ExecutionBackend.Stream);
        plan.StreamRefusal.ShouldBe(StreamRefusal.None);
        plan.Explanation.ShouldContain("backend: Stream");
    }

    [Fact]
    public void GlobalStep_RefusesStream_WithTheExistingReason()
    {
        var plan = PipelineCompiler.Compile(Recipe(PlanOps.Invert(), PlanOps.Equalize()), Streamable());

        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        plan.StreamRefusal.ShouldBe(StreamRefusal.GlobalStep);
        plan.Explanation.ShouldContain("stream refused: GlobalStep");
    }

    [Fact]
    public void GeometryWithoutARowForm_RefusesStream()
    {
        var plan = PipelineCompiler.Compile(Recipe(PlanOps.Invert(), FixedGeometry(64, 40, wrap: false)), Streamable());

        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        plan.StreamRefusal.ShouldBe(StreamRefusal.GeometricStep);
    }

    [Fact]
    public void ObserverForcesFrame_WithoutInventingARefusal()
    {
        var options = Streamable() with { ObserveSteps = true };

        var plan = PipelineCompiler.Compile(Recipe(PlanOps.Invert()), options);

        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        plan.StreamRefusal.ShouldBe(StreamRefusal.None);
        plan.Explanation.ShouldContain("stream unavailable: an observer asks for every step's full output");
    }

    [Fact]
    public void ASinkWithoutRows_ForcesFrame()
    {
        var options = Streamable() with { Sink = SinkCapabilities.None };

        var plan = PipelineCompiler.Compile(Recipe(PlanOps.Invert()), options);

        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        plan.Explanation.ShouldContain("stream unavailable: the destination does not accept rows");
    }

    [Fact]
    public void RegionIsChosen_OnlyWhenTheSourceCanReadRectangles()
    {
        var options = new CompileOptions
        {
            SourceWidth = 320,
            SourceHeight = 200,
            SourceFormat = WorkingFormat.Rgba8,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion,
            Region = new PixelRect(8, 6, 64, 40),
        };

        var plan = PipelineCompiler.Compile(Recipe(PlanOps.Invert()), options);

        plan.Backend.ShouldBe(ExecutionBackend.Region);
        plan.RegionRefusal.ShouldBe(RegionRefusal.None);
    }

    [Fact]
    public void RegionRefusesGeometry_WithTheExistingReason()
    {
        var options = new CompileOptions
        {
            SourceWidth = 320,
            SourceHeight = 200,
            SourceFormat = WorkingFormat.Rgba8,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion,
            Sink = SinkCapabilities.RowWritable,
            Region = new PixelRect(8, 6, 64, 40),
        };

        var plan = PipelineCompiler.Compile(Recipe(PlanOps.Invert(), FixedGeometry(64, 40)), options);

        plan.RegionRefusal.ShouldBe(RegionRefusal.GeometricStep);
        plan.Backend.ShouldBe(ExecutionBackend.Stream);
    }

    [Fact]
    public void AFormatTheOperatorDoesNotSupport_IsRefusedBeforeAnythingRuns()
    {
        var options = Streamable() with { SourceFormat = WorkingFormat.Rgba16 };

        var error = Should.Throw<NotSupportedException>(() =>
            PipelineCompiler.Compile(Recipe(PlanOps.PointChain(PointOps.InvertRow())), options));
        error.Message.ShouldContain("point-chain");
        error.Message.ShouldContain("Rgba16");
    }

    [Fact]
    public void AFormatTheOperatorSupports_Compiles()
    {
        var options = Streamable() with { SourceFormat = WorkingFormat.Rgba16 };

        var plan = PipelineCompiler.Compile(Recipe(PlanOps.Invert()), options);

        plan.Steps.Count.ShouldBe(1);
    }

    [Fact]
    public void FusionFollowsTheSameDecisionAsThePlan()
    {
        var chain = Recipe(PlanOps.GaussianBlur(1f), PlanOps.Brightness(0.05f), PlanOps.Contrast(1.1f));

        var small = PipelineCompiler.Compile(chain, Streamable(1920, 1080));
        var huge = PipelineCompiler.Compile(chain, Streamable(7680, 4320));

        small.Sections[0].Fused.ShouldBeFalse("16.6 MB of intermediates is below the 64 MiB line");
        huge.Sections[0].Fused.ShouldBeTrue("265 MB of intermediates is above it");
    }

    [Fact]
    public void ForceFusionAndAllowFusionOverrideTheDecision()
    {
        var chain = Recipe(PlanOps.GaussianBlur(1f), PlanOps.Brightness(0.05f), PlanOps.Contrast(1.1f));
        var options = Streamable(1920, 1080);

        PipelineCompiler.Compile(chain, options with { Execute = new ExecuteOptions { ForceFusion = true } })
            .Sections[0].Fused.ShouldBeTrue();
        PipelineCompiler.Compile(chain, options with { Execute = new ExecuteOptions { AllowFusion = false } })
            .Sections[0].Fused.ShouldBeFalse();
    }

    [Fact]
    public void CompilerDoesNotForceFusionWhenAContractRequiresWholeImage()
    {
        var contract = OperatorContract.Create("whole-image", OpCategory.Neighborhood,
            WorkingFormatMask.All, IdentityFootprint.Instance, WorkspaceModel.None,
            requirements: ExecutionRequirements.WholeImage);
        var op = new ImageOp(contract, new KernelImplementations((source, destination, rect, _) =>
        {
            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
                source.Row(y).CopyTo(destination.Row(y));
        }));
        var recipe = Recipe(op, PlanOps.Invert());

        var plan = PipelineCompiler.Compile(recipe, Streamable() with
        {
            Execute = new ExecuteOptions { ForceFusion = true },
        });

        plan.Sections[0].Fused.ShouldBeFalse();
        plan.Explanation.ShouldContain(line => line.Contains("execution requirements incompatible with fusion",
            StringComparison.Ordinal));
    }

    [Fact]
    public void TileIsRefused_WithTheFirstRealBlocker()
    {
        var noSink = PipelineCompiler.Compile(Recipe(PlanOps.Invert()), Streamable() with { TileRequested = true });
        noSink.Backend.ShouldBe(ExecutionBackend.TileRefused);
        noSink.TileRefusal.ShouldBe(TileRefusal.NoRegionSink);

        var withSink = Streamable() with
        {
            TileRequested = true,
            Sink = SinkCapabilities.RandomRegionWrite,
        };
        PipelineCompiler.Compile(Recipe(PlanOps.Equalize()), withSink).TileRefusal.ShouldBe(TileRefusal.GlobalStep);
        PipelineCompiler.Compile(Recipe(PlanOps.Pixelate(8)), withSink).TileRefusal.ShouldBe(TileRefusal.BlockAlignedOperator);
        var tileable = PipelineCompiler.Compile(Recipe(PlanOps.Invert()), withSink);
        tileable.Backend.ShouldBe(ExecutionBackend.Tile);
        tileable.TileRefusal.ShouldBeNull();
    }

    [Fact]
    public void AnEmptyRecipe_CompilesToNoWork()
    {
        var plan = PipelineCompiler.Compile(PipelineRecipe.Empty, Streamable());

        plan.Steps.ShouldBeEmpty();
        plan.Sections.ShouldBeEmpty();
        plan.Backend.ShouldBe(ExecutionBackend.Stream);
    }
}
