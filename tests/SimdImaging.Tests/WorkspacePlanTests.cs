using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Geometry;
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

    private sealed class EmptySource(int width, int height) : IRowSource
    {
        public int Width => width;

        public int Height => height;

        public WorkingFormat Format => WorkingFormat.Rgba8;

        public int ReadRows(Span<byte> destination, int stride, int maxRows) => 0;
    }
}
