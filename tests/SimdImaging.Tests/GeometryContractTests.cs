using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// Geometry steps used to be described only by "has a row wrapper / does not". These tests pin the declaration in
/// the same vocabulary as an operator contract: partitioning, pass, state, order, determinism, alpha and workspace.
/// Warp and SeamCarve are the two the architecture review called out; Crop and Resize are the row-local controls.
/// </summary>
public sealed class GeometryContractTests
{
    [Fact]
    public void Warp_DeclaresWholeSourceAndResampledAlpha()
    {
        var warp = GeometrySteps.Warp(32, 24, Identity, Resampler.Bilinear, default);

        warp.Requirements.Partition.ShouldBe(Partitioning.WholeImage);
        warp.Pass.ShouldBe(PassModel.Single);
        warp.State.ShouldBe(StateScope.None);
        warp.Order.ShouldBe(ExecutionOrder.Any);
        warp.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Recomputes);
        warp.PixelSemantics.Domain.ShouldBe(PixelDomain.Encoded);
        warp.PixelSemantics.Border.ShouldBe(BorderMode.Constant);
        warp.PixelSemantics.Precision.ShouldBe(Precision.SingleRoundFloat);
        warp.MapRegions.ShouldBeNull("an arbitrary destination-to-source lambda cannot be inverted region-wise");
        warp.WorkspaceModel.IsDeclared.ShouldBeTrue();
    }

    [Fact]
    public void SeamCarving_DeclaresAWholeFrameAlgorithmWithPerImageState()
    {
        var seam = GeometrySteps.SeamCarve(16, 12);

        seam.Requirements.Partition.ShouldBe(Partitioning.WholeImage);
        seam.Pass.ShouldBe(PassModel.WholeFrameAlgorithm);
        seam.State.ShouldBe(StateScope.PerImage);
        seam.Order.ShouldBe(ExecutionOrder.SourceOrder);
        seam.Determinism.ShouldBe(Determinism.OrderDependent);
        seam.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        seam.PixelSemantics.Precision.ShouldBe(Precision.IntegerExact);
        seam.WorkspaceModel.IsDeclared.ShouldBeFalse(
            "seam carving rents a full-size back-pointer and working planes that are not yet estimated");
    }

    [Fact]
    public void RowLocalGeometry_DeclaresRowBandsAndExactRegions()
    {
        var resize = GeometrySteps.Resize(16, 12, Resampler.Bilinear);
        resize.Requirements.Partition.ShouldBe(Partitioning.RowBands);
        resize.Wrap.ShouldNotBeNull();
        resize.MapRegions.ShouldNotBeNull();
        resize.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Recomputes);
        resize.PixelSemantics.Border.ShouldBe(BorderMode.Clamp);

        var crop = GeometrySteps.Crop(new PixelRect(0, 0, 8, 8));
        crop.Requirements.Partition.ShouldBe(Partitioning.RowBands);
        crop.Wrap.ShouldNotBeNull();
        crop.MapRegions.ShouldNotBeNull();
        crop.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        crop.PixelSemantics.Precision.ShouldBe(Precision.IntegerExact);

        var flip = GeometrySteps.FlipHorizontal();
        flip.Requirements.Partition.ShouldBe(Partitioning.RowBands);
        flip.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        flip.PixelSemantics.Precision.ShouldBe(Precision.IntegerExact);
    }

    [Fact]
    public void TheCompiler_ConsumesTheDeclarationsInTheExplanationAndWorkspace()
    {
        var seamRecipe = new PipelineRecipeBuilder().Append(GeometrySteps.SeamCarve(16, 12)).Build();
        var seam = PipelineCompiler.Compile(seamRecipe, Options(24, 18));

        seam.Workspace.IsLowerBound.ShouldBeTrue("the seam step declares no workspace estimate");
        seam.StepInfo[0].Requirements.Pass.ShouldBe(PassModel.WholeFrameAlgorithm);
        seam.StepInfo[0].Requirements.State.ShouldBe(StateScope.PerImage);
        seam.StepInfo[0].Requirements.Semantics.Alpha.ShouldBe(AlphaPolicy.Preserve);

        var cropRecipe = new PipelineRecipeBuilder().Append(GeometrySteps.Crop(new PixelRect(0, 0, 8, 8))).Build();
        var crop = PipelineCompiler.Compile(cropRecipe, Options(24, 18));

        crop.Workspace.IsLowerBound.ShouldBeFalse();
        crop.StepInfo[0].Requirements.Partitioning.ShouldBe(Partitioning.RowBands);
        crop.StepInfo[0].Requirements.Semantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
    }

    [Fact]
    public void AnUndeclaredWorkspace_IsRefusedUnderAStrictCeiling()
    {
        var recipe = new PipelineRecipeBuilder().Append(GeometrySteps.SeamCarve(16, 12)).Build();
        var strict = Options(24, 18) with
        {
            Execute = new ExecuteOptions { MaxWorkingBytes = 1 << 20 },
        };

        var error = Should.Throw<InvalidOperationException>(() => PipelineCompiler.Compile(recipe, strict));
        error.Message.ShouldContain("lower bound");

        var allowed = strict with
        {
            Execute = new ExecuteOptions { MaxWorkingBytes = 1 << 20, AllowUnestimatedScratch = true },
        };
        PipelineCompiler.Compile(recipe, allowed).Workspace.IsLowerBound.ShouldBeTrue();
    }

    [Fact]
    public void AGeometryTransition_IsInTheWorkspaceReport()
    {
        var recipe = new PipelineRecipeBuilder()
            .Append(GeometrySteps.Resize(96, 72, Resampler.Bilinear))
            .Append(PlanOps.Invert())
            .Build();

        var plan = PipelineCompiler.Compile(recipe, Options(24, 18));
        var transition = plan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.SectionTransition);

        transition.Bytes.ShouldBe((24L * 18 * 4) + (96L * 72 * 4));
        plan.Workspace.BoundedBytes.ShouldBeGreaterThan(transition.Bytes);
    }

    [Fact]
    public void TheOperatorAfterAResize_IsEstimatedAtTheResizedGeometry()
    {
        var direct = new PipelineRecipeBuilder().Append(PlanOps.GaussianBlur(1f)).Build();
        var resized = new PipelineRecipeBuilder()
            .Append(GeometrySteps.Resize(96, 72, Resampler.Bilinear))
            .Append(PlanOps.GaussianBlur(1f))
            .Build();

        var plain = PipelineCompiler.Compile(direct, Options(24, 18));
        var afterResize = PipelineCompiler.Compile(resized, Options(24, 18));

        long Scratch(CompiledExecutionPlan plan) => plan.Workspace.Terms
            .Single(term => term.Kind == WorkspaceTermKind.PerWorkerScratch).Bytes;

        Scratch(afterResize).ShouldBeGreaterThan(Scratch(plain) * 3,
            "Gaussian scratch scales with the width the step actually sees");
    }

    [Fact]
    public void TwoStreamedResizes_ReportTheLargestRingOnce()
    {
        var recipe = new PipelineRecipeBuilder()
            .Append(GeometrySteps.Resize(48, 36, Resampler.Bilinear))
            .Append(GeometrySteps.Resize(96, 72, Resampler.Bilinear))
            .Build();
        var options = Options(24, 18) with { Source = SourceCapabilities.ForwardOnly, Sink = SinkCapabilities.RowWritable };

        var plan = PipelineCompiler.Compile(recipe, options);

        plan.Backend.ShouldBe(ExecutionBackend.Stream);
        var terms = string.Join(", ", plan.Workspace.Terms.Select(term => $"{term.Kind}={term.Bytes}"));
        var ring = plan.Workspace.Terms.FirstOrDefault(term => term.Kind == WorkspaceTermKind.ResizeRing);
        ring.Bytes.ShouldBeGreaterThan(0, $"terms: {terms}");
        var first = Ring(24, 18, 48, 36);
        var second = Ring(48, 36, 96, 72);
        ring.Bytes.ShouldBe(Math.Max(first, second));
        ring.Bytes.ShouldBeGreaterThan(first, "the second resize's ring is larger and must win, not the first");
    }

    private static long Ring(int sourceWidth, int sourceHeight, int width, int height)
    {
        var weightsY = WeightTable.Build(sourceHeight, height, Resampler.Bilinear);
        return ResizeWorkspace.Estimate(sourceWidth, sourceHeight, width, height, weightsY.Taps, nearest: false,
            WorkingFormat.Rgba8, new ExecuteOptions()).RingBytes;
    }

    private static void Identity(float destinationX, float destinationY, out float sourceX, out float sourceY)
    {
        sourceX = destinationX;
        sourceY = destinationY;
    }

    private static CompileOptions Options(int width, int height) => new()
    {
        SourceWidth = width,
        SourceHeight = height,
        SourceFormat = WorkingFormat.Rgba8,
        Sink = SinkCapabilities.None,
        Execute = new ExecuteOptions(),
    };
}
