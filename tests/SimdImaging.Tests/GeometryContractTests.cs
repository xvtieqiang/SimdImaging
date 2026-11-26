using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;

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
        warp.Alpha.ShouldBe(AlphaPolicy.Recomputes);
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
        seam.Alpha.ShouldBe(AlphaPolicy.Preserve);
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
        resize.Alpha.ShouldBe(AlphaPolicy.Recomputes);

        var crop = GeometrySteps.Crop(new PixelRect(0, 0, 8, 8));
        crop.Requirements.Partition.ShouldBe(Partitioning.RowBands);
        crop.Wrap.ShouldNotBeNull();
        crop.MapRegions.ShouldNotBeNull();
        crop.Alpha.ShouldBe(AlphaPolicy.Preserve);

        var flip = GeometrySteps.FlipHorizontal();
        flip.Requirements.Partition.ShouldBe(Partitioning.RowBands);
        flip.Alpha.ShouldBe(AlphaPolicy.Preserve);
    }

    [Fact]
    public void TheCompiler_ConsumesTheDeclarationsInTheExplanationAndWorkspace()
    {
        var seamRecipe = new PipelineRecipeBuilder().Append(GeometrySteps.SeamCarve(16, 12)).Build();
        var seam = PipelineCompiler.Compile(seamRecipe, Options(24, 18));

        seam.Workspace.IsLowerBound.ShouldBeTrue("the seam step declares no workspace estimate");
        seam.StepInfo[0].Requirements.Pass.ShouldBe(PassModel.WholeFrameAlgorithm);
        seam.StepInfo[0].Requirements.State.ShouldBe(StateScope.PerImage);
        seam.StepInfo[0].Requirements.Alpha.ShouldBe(AlphaPolicy.Preserve);

        var cropRecipe = new PipelineRecipeBuilder().Append(GeometrySteps.Crop(new PixelRect(0, 0, 8, 8))).Build();
        var crop = PipelineCompiler.Compile(cropRecipe, Options(24, 18));

        crop.Workspace.IsLowerBound.ShouldBeFalse();
        crop.StepInfo[0].Requirements.Partitioning.ShouldBe(Partitioning.RowBands);
        crop.StepInfo[0].Requirements.Alpha.ShouldBe(AlphaPolicy.Preserve);
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
