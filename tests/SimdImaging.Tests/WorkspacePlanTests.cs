using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// The workspace report must come from the same arithmetic as the executors, or it is a second opinion nobody can
/// act on. These tests compare the report with the pool's measured peak and with the row source's own figure.
/// </summary>
public sealed class WorkspacePlanTests
{
    private static CompileOptions FrameOptions(int width = 128, int height = 96) => new()
    {
        SourceWidth = width,
        SourceHeight = height,
        SourceFormat = WorkingFormat.Rgba8,
        Sink = SinkCapabilities.None,
    };

    private static CompileOptions StreamOptions(int width = 128, int height = 96) => new()
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

    [Fact]
    public void FrameReport_CoversWhatThePoolActuallyRents()
    {
        var recipe = Recipe(PlanOps.Invert(), PlanOps.GaussianBlur(1f), PlanOps.Contrast(1.1f));
        var plan = PipelineCompiler.Compile(recipe, FrameOptions());

        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        plan.Workspace.Terms.ShouldContain(term => term.Kind == WorkspaceTermKind.Source);
        plan.Workspace.Terms.ShouldContain(term => term.Kind == WorkspaceTermKind.Destination);
        var predictedPeak = MemoryEstimator.Estimate(128, 96, WorkingFormat.Rgba8, plan.Steps,
            ProcessingPlan.DefaultBandRows, allowFusion: plan.Sections[0].Fused).PeakBytes;
        plan.Workspace.BoundedBytes.ShouldBe(predictedPeak);

        using var source = TestImages.Gradient(128, 96, WorkingFormat.Rgba8);
        var pool = new SimdImaging.Processing.Fusion.CountingPool(ArrayPixelPool.Shared);
        using var result = ProcessingPlan.FromCompiled(plan.Steps, ProcessingPlan.DefaultBandRows)
            .Execute(source[0].Pixels.View, pool);

        plan.Workspace.BoundedBytes.ShouldBeGreaterThanOrEqualTo(pool.PeakBytes,
            $"reported {plan.Workspace.BoundedBytes}, rented {pool.PeakBytes}");
    }

    [Fact]
    public void FormatStepScratch_IsIncludedInTheCompiledWorkspaceReport()
    {
        const int width = 128;
        const int height = 96;
        var plan = PipelineCompiler.Compile(Recipe(PlanOps.DistanceTransform()), FrameOptions(width, height));

        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        var scratch = (4L * width * height)
            + (256L * height)
            + (16L * Math.Max(width, height))
            + (16L * width);

        plan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.FormatScratch)
            .Bytes.ShouldBe(scratch);
        plan.Workspace.Terms.ShouldContain(term => term.Kind == WorkspaceTermKind.FormatStep);
    }

    [Fact]
    public void FormatTransition_ReservesItsDeclaredScratchBeforeExecuting()
    {
        const int width = 80;
        const int height = 48;
        var plan = PipelineCompiler.Compile(Recipe(PlanOps.DistanceTransform()), FrameOptions(width, height));
        var formatScratch = plan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.FormatScratch).Bytes;
        var oldBytes = (long)width * height * WorkingFormat.Rgba8.BytesPerPixel();
        var newBytes = (long)width * height * WorkingFormat.Rgba32F.BytesPerPixel();
        var manager = new WorkspaceManager(oldBytes + newBytes + formatScratch - 1);
        using var source = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8);

        Should.Throw<InvalidOperationException>(() =>
            CompiledPlanExecutor.ExecuteFrame(plan, source.View,
                options: new ExecuteOptions { Workspace = manager }));

        manager.OutstandingBytes.ShouldBe(0);
        manager.PeakBytes.ShouldBe(0);
    }

    [Fact]
    public void FormatExecution_MixedWorkspacePerCallIsReservedExactlyOnce()
    {
        const int width = 80;
        const int height = 48;
        const long perWorker = 1024;
        const long wholeImage = 2048;
        const long perCall = 512;
        var model = WorkspaceModel.OfMixed(
            (_, _, _, _) => perWorker,
            (_, _, _, _) => wholeImage,
            (_, _, _, _) => perCall);
        var contract = OperatorContract.Create("mixed-format-scratch", OpCategory.Global,
            WorkingFormatMask.All, WholeSourceFootprint.Instance, model,
            requirements: ExecutionRequirements.WholeImage, outputFormat: WorkingFormat.Rgba32F);
        var op = new ImageOp(contract, new KernelImplementations(
            (_, _, _, _) => { },
            wholeImage: source => WorkingBuffer.Rent(source.Width, source.Height, WorkingFormat.Rgba32F)));
        var compileOptions = FrameOptions(width, height) with
        {
            Execute = new ExecuteOptions { MaxDegreeOfParallelism = 1 },
        };
        var plan = PipelineCompiler.Compile(Recipe(op), compileOptions);
        plan.Backend.ShouldBe(ExecutionBackend.Frame);

        var oldBytes = (long)width * height * WorkingFormat.Rgba8.BytesPerPixel();
        var newBytes = (long)width * height * WorkingFormat.Rgba32F.BytesPerPixel();
        var scratchBytes = perWorker + wholeImage + perCall;
        var expectedWorkingBytes = oldBytes + newBytes + scratchBytes;
        plan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.FormatScratch)
            .Bytes.ShouldBe(scratchBytes);
        plan.Workspace.BoundedBytes.ShouldBe(expectedWorkingBytes);

        using var source = TestImages.Gradient(width, height, WorkingFormat.Rgba8);
        var manager = new WorkspaceManager(expectedWorkingBytes);
        using var result = CompiledPlanExecutor.ExecuteFrame(plan, source[0].Pixels.View,
            options: new ExecuteOptions { MaxDegreeOfParallelism = 1, Workspace = manager });

        result.View.Format.ShouldBe(WorkingFormat.Rgba32F);
        manager.PeakBytes.ShouldBe(expectedWorkingBytes);
        manager.OutstandingBytes.ShouldBe(0);
    }

    [Fact]
    public void ResizeTerms_MatchTheRowSourceThatWillRun()
    {
        var recipe = Recipe(PlanOps.Invert(), GeometrySteps.Resize(64, 40, Resampler.Bicubic));
        var plan = PipelineCompiler.Compile(recipe, StreamOptions());
        plan.Backend.ShouldBe(ExecutionBackend.Stream);

        var reported = plan.Workspace.Terms
            .Where(term => term.Kind is WorkspaceTermKind.ResizeRing or WorkspaceTermKind.ResizeStaging
                or WorkspaceTermKind.ResizeBatch or WorkspaceTermKind.ResizeWorkerRows
                or WorkspaceTermKind.ResizeSourceRow or WorkspaceTermKind.ResizeWorkerTapScratch)
            .Sum(term => term.Bytes);

        var source = new EmptySource(128, 96);
        using var rows = new ResizeRowSource(source, 64, 40, Resampler.Bicubic, new ExecuteOptions());

        reported.ShouldBe(rows.ResidentBytes);
        plan.Workspace.Unmanaged.ShouldNotBeEmpty();
    }

    [Fact]
    public void UndeclaredOperatorScratch_MakesTheReportALowerBound()
    {
        var adHoc = new ProcessingPlan()
            .Add("unestimated", OpCategory.Point, (_, _, _, _) => { })
            .Steps[0].Definition;
        var plan = PipelineCompiler.Compile(Recipe(adHoc), FrameOptions());

        plan.Workspace.IsLowerBound.ShouldBeTrue();
        plan.Workspace.UndeclaredSteps.ShouldBe(1);
    }

    [Fact]
    public void EveryDeclaredPlan_ListsWhatItCannotSee()
    {
        var plan = PipelineCompiler.Compile(Recipe(PlanOps.Invert()), StreamOptions());

        plan.Workspace.Unmanaged.ShouldContain(term => term.Name.Contains("codec"));
        plan.Workspace.Unmanaged.ShouldContain(term => term.Name.Contains("weight"));
    }

    [Fact]
    public void EmptyFramePlan_ReservesCopyDestinationAndHonorsTheLimit()
    {
        const int width = 32;
        const int height = 24;
        var bytes = (long)width * height * WorkingFormat.Rgba8.BytesPerPixel();
        var plan = PipelineCompiler.Compile(PipelineRecipe.Empty, FrameOptions(width, height) with
        {
            ObserveSteps = true,
        });
        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        plan.Workspace.BoundedBytes.ShouldBe(bytes);

        using var source = TestImages.Gradient(width, height, WorkingFormat.Rgba8);
        var manager = new WorkspaceManager(bytes);
        using var result = CompiledPlanExecutor.ExecuteFrame(plan, source[0].Pixels.View,
            options: new ExecuteOptions { MaxWorkingBytes = bytes, Workspace = manager });

        result.View.Width.ShouldBe(width);
        result.View.Height.ShouldBe(height);
        manager.PeakBytes.ShouldBe(bytes);
        manager.OutstandingBytes.ShouldBe(0);

        var refusedManager = new WorkspaceManager();
        Should.Throw<InvalidOperationException>(() => CompiledPlanExecutor.ExecuteFrame(plan,
            source[0].Pixels.View,
            options: new ExecuteOptions { MaxWorkingBytes = bytes - 1, Workspace = refusedManager }));
        refusedManager.PeakBytes.ShouldBe(0);
        refusedManager.OutstandingBytes.ShouldBe(0);
    }

    [Fact]
    public void RegionAndTileScratch_UseTheLargestSequentialOperatorPeak()
    {
        var perWorker = WorkspaceModel.Of((_, _, _, _) => 768);
        var wholeImage = WorkspaceModel.Of((_, _, _, _) => 2048, WorkspaceScope.WholeImage);
        var first = new ImageOp(
            OperatorContract.Create("per-worker-scratch", OpCategory.Point, WorkingFormatMask.All,
                IdentityFootprint.Instance, perWorker),
            new KernelImplementations((_, _, _, _) => { }));
        var second = new ImageOp(
            OperatorContract.Create("whole-image-scratch", OpCategory.Point, WorkingFormatMask.All,
                IdentityFootprint.Instance, wholeImage),
            new KernelImplementations((_, _, _, _) => { }));
        var recipe = Recipe(first, second);

        var regionOptions = new CompileOptions
        {
            SourceWidth = 64,
            SourceHeight = 40,
            SourceFormat = WorkingFormat.Rgba8,
            Source = Sdcb.SimdCodecs.Abstractions.SourceCapabilities.Seekable |
                     Sdcb.SimdCodecs.Abstractions.SourceCapabilities.RandomRegion,
            Region = new PixelRect(8, 6, 32, 20),
            Execute = new ExecuteOptions { MaxDegreeOfParallelism = 1 },
        };
        var regionPlan = PipelineCompiler.Compile(recipe, regionOptions);
        regionPlan.Backend.ShouldBe(ExecutionBackend.Region);
        regionPlan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.OperatorScratchPeak)
            .Bytes.ShouldBe(2048);
        var runtimeRegionBytes = WorkingSetBudget.Measure(regionPlan.Steps,
            new ExecuteOptions { MaxDegreeOfParallelism = 1 }, 64, 40, 32, 20,
            WorkingFormat.Rgba8, windows: 4, out var undeclared);
        runtimeRegionBytes.ShouldBe((long)4 * 32 * 20 * WorkingFormat.Rgba8.BytesPerPixel() + 2048);
        undeclared.ShouldBe(0);
        var targetBytes = (long)32 * 20 * WorkingFormat.Rgba8.BytesPerPixel();
        regionPlan.Workspace.BoundedBytes.ShouldBe(targetBytes + runtimeRegionBytes);

        var tilePlan = PipelineCompiler.Compile(recipe, regionOptions with
        {
            Region = null,
            TileRequested = true,
            TileWidth = 32,
            TileHeight = 20,
            Sink = Sdcb.SimdCodecs.Abstractions.SinkCapabilities.RandomRegionWrite,
        });
        tilePlan.Backend.ShouldBe(ExecutionBackend.Tile);
        tilePlan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.OperatorScratchPeak)
            .Bytes.ShouldBe(2048);
        var runtimeTileWindows = (long)4 * 32 * 20 * WorkingFormat.Rgba8.BytesPerPixel();
        tilePlan.Workspace.BoundedBytes.ShouldBe(targetBytes + runtimeTileWindows + 2048);
    }


    [Fact]
    public void EmptyStreamPlan_CountsOnlyTheActualPipelineTransferBuffer()
    {
        const int width = 64;
        const int height = 40;
        var plan = PipelineCompiler.Compile(PipelineRecipe.Empty, StreamOptions(width, height));

        plan.Backend.ShouldBe(ExecutionBackend.Stream);
        var transferBytes = (long)width * height * WorkingFormat.Rgba8.BytesPerPixel();
        plan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.CopyBuffer)
            .Bytes.ShouldBe(transferBytes);
        plan.Workspace.Terms.ShouldNotContain(term => term.Kind == WorkspaceTermKind.Window);
        plan.Workspace.BoundedBytes.ShouldBe(transferBytes);
    }

    [Fact]
    public void StreamGeometryWrappers_AndPipelineCopyReserveTheirResidentBuffers()
    {
        const int sourceWidth = 64;
        const int sourceHeight = 48;
        var recipe = Recipe(
            PlanOps.Invert(),
            GeometrySteps.Crop(new PixelRect(2, 3, 40, 20)),
            GeometrySteps.Pad(1, 2, 3, 4, new RgbaColor(8, 9, 10, 255)),
            GeometrySteps.FlipHorizontal());
        var options = StreamOptions(sourceWidth, sourceHeight) with
        {
            Execute = new ExecuteOptions { MaxDegreeOfParallelism = 1 },
        };
        var plan = PipelineCompiler.Compile(recipe, options);
        plan.Backend.ShouldBe(ExecutionBackend.Stream);

        var bytesPerPixel = WorkingFormat.Rgba8.BytesPerPixel();
        var cropBandBytes = (long)sourceWidth * 21 * bytesPerPixel;
        var padRowBytes = (long)40 * bytesPerPixel;
        var flipRowBytes = (long)44 * bytesPerPixel;
        var transferBytes = (long)44 * 26 * bytesPerPixel;
        plan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.GeometryRowScratch)
            .Bytes.ShouldBe(cropBandBytes + padRowBytes + flipRowBytes);
        plan.Workspace.Terms.Single(term => term.Kind == WorkspaceTermKind.CopyBuffer)
            .Bytes.ShouldBe(transferBytes);
        plan.Workspace.IsLowerBound.ShouldBeFalse();

        using var image = TestImages.Gradient(sourceWidth, sourceHeight, WorkingFormat.Rgba8);
        var manager = new WorkspaceManager(plan.Workspace.BoundedBytes);
        var execute = new ExecuteOptions { MaxDegreeOfParallelism = 1, Workspace = manager };
        var chain = CompiledPlanExecutor.CreateStreamChain(plan,
            new ViewSource(image[0].Pixels.View), execute);
        try
        {
            Pipeline.Copy(chain, new DiscardSink(), default, execute).ShouldBe(26);
        }
        finally
        {
            (chain as IDisposable)?.Dispose();
        }

        manager.PeakBytes.ShouldBe(plan.Workspace.BoundedBytes);
        manager.OutstandingBytes.ShouldBe(0);
    }


    private sealed class ViewSource(PixelView view) : IRowSource
    {
        private int _row;

        public int Width => view.Width;
        public int Height => view.Height;
        public WorkingFormat Format => view.Format;

        public int ReadRows(Span<byte> destination, int stride, int maxRows)
        {
            if (stride == 0)
                stride = Width * Format.BytesPerPixel();
            var count = Math.Min(maxRows, Height - _row);
            for (var y = 0; y < count; y++)
                view.Row(_row + y).CopyTo(destination.Slice(y * stride, stride));
            _row += count;
            return count;
        }
    }

    private sealed class DiscardSink : IRowSink
    {
        public void WriteRows(ReadOnlySpan<byte> rows, int stride, int rowCount) { }
    }

    private sealed class EmptySource(int width, int height) : IRowSource
    {
        public int Width => width;

        public int Height => height;

        public WorkingFormat Format => WorkingFormat.Rgba8;

        public int ReadRows(Span<byte> destination, int stride, int maxRows) => 0;
    }
}
