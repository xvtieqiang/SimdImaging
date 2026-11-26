using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// <see cref="WorkspaceScope"/> is part of the contract, not a comment: the same declared number must be multiplied
/// by the worker count only when the operator says it is per-worker.
/// </summary>
public sealed class WorkspaceScopeTests
{
    [Fact]
    public void WholeImageScope_IsCountedOnce_AndPerWorkerScope_IsCountedPerWorker()
    {
        var perWorker = MemoryEstimator.Estimate(1024, 256, WorkingFormat.Rgba8,
            [new PlanStep(ScopedOp(WorkspaceScope.PerWorker))], 64, allowFusion: false, degree: 4);
        var wholeImage = MemoryEstimator.Estimate(1024, 256, WorkingFormat.Rgba8,
            [new PlanStep(ScopedOp(WorkspaceScope.WholeImage))], 128, allowFusion: false, degree: 4);

        perWorker.PerWorkerBytes.ShouldBe(1024);
        perWorker.WholeImageScratchBytes.ShouldBe(0);
        perWorker.OperatorScratchBytes.ShouldBe(4096);

        wholeImage.PerWorkerBytes.ShouldBe(0);
        wholeImage.WholeImageScratchBytes.ShouldBe(1024);
        wholeImage.OperatorScratchBytes.ShouldBe(1024);

        // PerCall has no current user. A banded step calls once per band, so the contract's honest reading is the
        // per-worker term; the point of the test is that it is counted, not silently dropped.
        var perCall = MemoryEstimator.Estimate(1024, 256, WorkingFormat.Rgba8,
            [new PlanStep(ScopedOp(WorkspaceScope.PerCall))], 128, allowFusion: false, degree: 4);
        perCall.PerWorkerBytes.ShouldBe(1024);
        perCall.OperatorScratchBytes.ShouldBe(4096);
    }

    [Fact]
    public void OperatorScratchBytes_UsesTheActualPerWorkerPeakAcrossDifferentContracts()
    {
        var singleContract = OperatorContract.Create("single-worker", OpCategory.Neighborhood,
            WorkingFormatMask.All, IdentityFootprint.Instance, WorkspaceModel.Of((_, _, _, _) => 2000),
            requirements: ExecutionRequirements.RowBands with { Threading = ThreadSafety.SingleThread });
        var concurrent = ScopedOp(WorkspaceScope.PerWorker);
        var plan = new ProcessingPlan { BandRows = 64 }
            .Add(new ImageOp(singleContract, new KernelImplementations((_, _, _, _) => { })))
            .Add(concurrent);

        var estimate = MemoryEstimator.Estimate(1024, 256, WorkingFormat.Rgba8, plan.Steps,
            bandRows: 64, allowFusion: false, degree: 4);

        estimate.PerWorkerBytes.ShouldBe(2000);
        estimate.Workers.ShouldBe(4);
        estimate.PerWorkerPeakBytes.ShouldBe(4096);
        estimate.OperatorScratchBytes.ShouldBe(4096);
    }
    [Fact]
    public void TheCompilerAndTheStreamingBudget_ReadTheSameScope()
    {
        var options = new ExecuteOptions { MaxDegreeOfParallelism = 4 };
        var perWorker = ScopedOp(WorkspaceScope.PerWorker);
        var wholeImage = ScopedOp(WorkspaceScope.WholeImage);

        CompiledExecutionPlan Compile(ImageOp op) => PipelineCompiler.Compile(
            new PipelineRecipeBuilder().Append(op).Build(), new CompileOptions
            {
                SourceWidth = 1024,
                SourceHeight = 256,
                BandRows = 64,
                SourceFormat = WorkingFormat.Rgba8,
                Source = SourceCapabilities.ForwardOnly,
                Sink = SinkCapabilities.RowWritable,
                Execute = options,
            });

        var perWorkerPlan = Compile(perWorker);
        var wholeImagePlan = Compile(wholeImage);
        perWorkerPlan.Backend.ShouldBe(ExecutionBackend.Stream);
        perWorkerPlan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.PerWorkerScratch).Bytes
            .ShouldBe(1024 * 2);
        wholeImagePlan.Workspace.Terms.ShouldNotContain(term => term.Kind == WorkspaceTermKind.PerWorkerScratch);
        wholeImagePlan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.WholeImageScratch).Bytes
            .ShouldBe(1024);

        // The stream and region executors measure through WorkingSetBudget, which must apply the same rule.
        var perWorkerBudget = WorkingSetBudget.Measure([new PlanStep(perWorker)], options, 1024, 256, 1024, 64,
            WorkingFormat.Rgba8, windows: 2, out _);
        var wholeImageBudget = WorkingSetBudget.Measure([new PlanStep(wholeImage)], options, 1024, 256, 1024, 64,
            WorkingFormat.Rgba8, windows: 2, out _);

        (perWorkerBudget - wholeImageBudget).ShouldBe((1024 * 2) - 1024,
            "the scope must change the multiplier on every path, not only in MemoryEstimator");
    }

    [Fact]
    public void WholeImageInvocation_CanDeclareBothWorkerPrivateAndSharedScratch()
    {
        var op = MixedGlobalOp();
        var model = op.Contract.Workspace;

        model.HasPerWorkerEstimate.ShouldBeTrue();
        model.HasWholeImageEstimate.ShouldBeTrue();
        model.Estimate!(1024, 256, 256, WorkingFormat.Rgba8).ShouldBe(3072);

        var estimate = MemoryEstimator.Estimate(1024, 256, WorkingFormat.Rgba8,
            [new PlanStep(op)], bandRows: 64, allowFusion: false, degree: 4);

        estimate.PerWorkerBytes.ShouldBe(1024);
        estimate.Workers.ShouldBe(4);
        estimate.WholeImageScratchBytes.ShouldBe(2048);
        estimate.OperatorScratchBytes.ShouldBe((1024 * 4) + 2048);

        var execute = new ExecuteOptions { MaxDegreeOfParallelism = 4 };
        var workingSet = WorkingSetBudget.Measure([new PlanStep(op)], execute, 1024, 256,
            1024, 256, WorkingFormat.Rgba8, windows: 2, out var undeclared);
        undeclared.ShouldBe(0);
        (workingSet - (2L * 1024 * 256 * 4)).ShouldBe((1024 * 4) + 2048);
    }

    [Fact]
    public void GlobalPerCallScratch_IsCountedOnceAndKeepsItsReturnedFrame()
    {
        const int width = 80;
        const int height = 40;
        var op = PlanOps.Dither(4);
        var workspace = op.Contract.Workspace;

        workspace.PerCallEstimate.ShouldNotBeNull();
        workspace.HasWholeImageEstimate.ShouldBeTrue();
        workspace.HasPerWorkerEstimate.ShouldBeFalse();

        var wholeImageBytes = ((long)width * height * WorkingFormat.Rgba8.BytesPerPixel())
            + (64L * width);
        workspace.EstimateWholeImage(width, height, WorkingFormat.Rgba8).ShouldBe(wholeImageBytes);

        var estimate = MemoryEstimator.Estimate(width, height, WorkingFormat.Rgba8,
            [new PlanStep(op)], bandRows: 16, allowFusion: false, degree: 4);
        estimate.PerWorkerBytes.ShouldBe(0);
        estimate.WholeImageScratchBytes.ShouldBe(wholeImageBytes);
    }

    [Fact]
    public void GlowWorkspace_SeparatesWholeFramesFromWorkerPrivateRows()
    {
        const int width = 320;
        const int height = 240;
        const float sigma = 2f;
        var workspace = PlanOps.Glow(sigma: sigma).Contract.Workspace;
        var format = WorkingFormat.Rgba8;
        var frameBytes = (long)width * height * format.BytesPerPixel();

        workspace.HasPerWorkerEstimate.ShouldBeTrue();
        workspace.HasWholeImageEstimate.ShouldBeTrue();
        workspace.EstimateWholeImage(width, height, format)
            .ShouldBeGreaterThanOrEqualTo(3L * frameBytes);
        workspace.EstimatePerWorker(width, height, height, format)
            .ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GlobalOperatorScopes_MatchMeasuredAlgorithmLifetimes()
    {
        const int width = 128;
        const int height = 96;
        const long rgba8LocalHistogramBytes = 3L * 256 * sizeof(int);

        var equalize = PlanOps.Equalize().Contract.Workspace;
        equalize.HasPerWorkerEstimate.ShouldBeTrue();
        equalize.EstimatePerWorker(width, height, height, WorkingFormat.Rgba8)
            .ShouldBe(rgba8LocalHistogramBytes);
        equalize.EstimateWholeImage(width, height, WorkingFormat.Rgba8)
            .ShouldBeGreaterThan((long)width * height * WorkingFormat.Rgba8.BytesPerPixel());

        var adaptive = PlanOps.AdaptiveThreshold(radius: 4).Contract.Workspace;
        adaptive.HasPerWorkerEstimate.ShouldBeFalse();
        adaptive.HasWholeImageEstimate.ShouldBeTrue();
        var windowRows = 9L;
        var expectedAdaptiveScratch = windowRows * width * sizeof(float)
            + (52L * width) + (2L * sizeof(double))
            + ((long)width * height * WorkingFormat.Rgba8.BytesPerPixel());
        adaptive.EstimateWholeImage(width, height, WorkingFormat.Rgba8).ShouldBe(expectedAdaptiveScratch);

        var clahe = PlanOps.Clahe(tilesX: 4, tilesY: 3).Contract.Workspace;
        clahe.EstimatePerWorker(width, height, height, WorkingFormat.Rgba8).ShouldBe(16L * width);
        clahe.EstimateWholeImage(width, height, WorkingFormat.Rgba8)
            .ShouldBe(2L * 4 * 3 * 3 * 256 * sizeof(float)
                + ((long)width * height * WorkingFormat.Rgba8.BytesPerPixel()));
    }

    private static ImageOp MixedGlobalOp()
    {
        var workspace = WorkspaceModel.OfMixed(
            (_, _, _, _) => 1024L,
            (_, _, _, _) => 2048L);
        var contract = OperatorContract.Create("mixed-global", OpCategory.Global, WorkingFormatMask.All,
            WholeSourceFootprint.Instance, workspace, requirements: ExecutionRequirements.WholeImage);

        return new ImageOp(contract, new KernelImplementations((source, destination, rect, _) =>
        {
            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
                source.Row(y).CopyTo(destination.Row(y));
        }));
    }

    private static ImageOp ScopedOp(WorkspaceScope scope)
    {
        var contract = OperatorContract.Create("scope-probe", OpCategory.Point, WorkingFormatMask.All,
            IdentityFootprint.Instance, WorkspaceModel.Of((_, _, _, _) => 1024, scope), haloX: 0, haloY: 0);

        return new ImageOp(contract, new KernelImplementations((source, destination, rect, sourceRect) =>
        {
            for (var y = 0; y < rect.Height; y++)
                source.Row(y).Slice(rect.X * 4, rect.Width * 4).CopyTo(destination.Row(rect.Y + y).Slice(rect.X * 4));
        }));
    }
}
