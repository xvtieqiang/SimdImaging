using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;

namespace SimdImaging.Tests;

/// <summary>
/// The planner's decision matrix, exercised through the real compiler and workspace estimator. Four entry points
/// consume it — <c>ProcessingPlan.TryExecuteTo</c>, <c>ImageTranscode</c> and the two tile entry points in
/// <c>ImageTiles</c> — and each executes the plan the planner returned, so these cases decide what those callers run.
/// </summary>
public sealed class ExecutionPlannerTests
{
    private const int Width = 256;
    private const int Height = 192;

    [Fact]
    public void AutoWithTilePreference_FallsBackToFrameWhenTheStepIsGlobal()
    {
        var decision = Plan(Request(Recipe(PlanOps.Equalize()), preferred: ExecutionBackend.Tile,
            tileRequested: true, sink: SinkCapabilities.RandomRegionWrite,
            source: SourceCapabilities.Seekable | SourceCapabilities.RandomRegion |
                    SourceCapabilities.StableContent));

        decision.Succeeded.ShouldBeTrue();
        decision.SelectedBackend.ShouldBe(ExecutionBackend.Frame);
        decision.FallbackFrom!.Backend.ShouldBe(ExecutionBackend.Tile);
        decision.FallbackFrom.Reason.ShouldBe(ExecutionRefusalReason.Capability);
        decision.FallbackFrom.Message.ShouldNotBeNull().ShouldContain("Global");
        decision.Candidates.Count.ShouldBe(2);
    }

    [Fact]
    public void RequireTiled_RefusesAGlobalStepInsteadOfFallingBack()
    {
        var decision = Plan(Request(Recipe(PlanOps.Equalize()), preferred: ExecutionBackend.Tile,
            tileRequested: true, policy: ExecutionPolicy.RequireTiled,
            sink: SinkCapabilities.RandomRegionWrite,
            source: SourceCapabilities.Seekable | SourceCapabilities.RandomRegion |
                    SourceCapabilities.StableContent));

        decision.Succeeded.ShouldBeFalse();
        decision.Reason.ShouldBe(ExecutionRefusalReason.Capability);
        decision.Refusal!.Backend.ShouldBe(ExecutionBackend.Tile);
        decision.Refusal.Message.ShouldNotBeNull().ShouldContain("Global");
        decision.Candidates.Count.ShouldBe(1);
    }

    [Fact]
    public void RequireStreaming_RefusesAGlobalStep()
    {
        var decision = Plan(Request(Recipe(PlanOps.Equalize()), preferred: ExecutionBackend.Stream,
            policy: ExecutionPolicy.RequireStreaming, sink: SinkCapabilities.RowWritable));

        decision.Succeeded.ShouldBeFalse();
        decision.Reason.ShouldBe(ExecutionRefusalReason.Capability);
        decision.Refusal!.Backend.ShouldBe(ExecutionBackend.Stream);
        decision.Refusal.Message.ShouldNotBeNull().ShouldContain("Global");
    }

    [Fact]
    public void AutoStreamCapable_SelectsStream()
    {
        var decision = Plan(Request(Recipe(PlanOps.Invert(), PlanOps.GaussianBlur(1f)),
            preferred: ExecutionBackend.Stream, sink: SinkCapabilities.RowWritable));

        decision.Succeeded.ShouldBeTrue();
        decision.SelectedBackend.ShouldBe(ExecutionBackend.Stream);
        decision.FallbackFrom.ShouldBeNull();
    }

    /// <summary>
    /// Planning cost is part of end-to-end time (§7: <c>T_total = T_planning + T_execution</c>), so the decision
    /// has to carry it and the report has to expose it. A planner that costs nothing measurable would be a
    /// different finding; this asserts the reading is taken, not that it is small.
    /// </summary>
    [Fact]
    public void PlanningTimeIsRecorded()
    {
        var decision = Plan(Request(Recipe(PlanOps.Invert(), PlanOps.GaussianBlur(1f)),
            preferred: ExecutionBackend.Stream, sink: SinkCapabilities.RowWritable));

        decision.Succeeded.ShouldBeTrue();
        decision.PlanningTicks.ShouldBeGreaterThan(0, "the planner always takes some time; 0 means it was not read");
        decision.ToReport(OutputStrategy.Banded).PlanningTicks.ShouldBe(decision.PlanningTicks,
            "the report must expose the same reading the decision took");
    }

    [Fact]
    public void Region_IsFixedAndNeverFallsBack()
    {
        var rect = new PixelRect(16, 12, 96, 72);
        var streamable = Plan(Request(Recipe(PlanOps.Invert()), region: rect,
            source: SourceCapabilities.Seekable | SourceCapabilities.RandomRegion |
                    SourceCapabilities.StableContent));
        streamable.Succeeded.ShouldBeTrue();
        streamable.SelectedBackend.ShouldBe(ExecutionBackend.Region);

        var global = Plan(Request(Recipe(PlanOps.Equalize()), region: rect,
            source: SourceCapabilities.Seekable | SourceCapabilities.RandomRegion |
                    SourceCapabilities.StableContent));
        global.Succeeded.ShouldBeFalse();
        global.Reason.ShouldBe(ExecutionRefusalReason.Capability);
        global.Refusal!.Backend.ShouldBe(ExecutionBackend.Region);
        global.Candidates.Count.ShouldBe(1);
    }

    [Fact]
    public void BudgetTooLow_RefusesAndReportsTheRequiredBytes()
    {
        var request = Request(Recipe(PlanOps.Invert()), width: 4096, height: 2160,
            preferred: ExecutionBackend.Frame) with
        {
            Execute = new ExecuteOptions { MaxWorkingBytes = 1024 },
        };

        var decision = Plan(request);

        decision.Succeeded.ShouldBeFalse();
        decision.Reason.ShouldBe(ExecutionRefusalReason.Budget);
        decision.Refusal!.RequiredBytes.ShouldBeGreaterThan(1024);
        decision.Refusal.Message.ShouldNotBeNull().ShouldContain("MaxWorkingBytes");
    }

    [Fact]
    public void AutoPrimaryReason_IsThePreferredCandidatesBlocker_NotTheSmallestBudget()
    {
        // Tile is blocked by the Global step; the materialised fallback is blocked by the budget. Reporting only
        // "needs more bytes" would hide the real reason the preferred backend cannot run.
        var request = Request(Recipe(PlanOps.Equalize()), preferred: ExecutionBackend.Tile,
            tileRequested: true, sink: SinkCapabilities.RandomRegionWrite,
            source: SourceCapabilities.Seekable | SourceCapabilities.RandomRegion |
                    SourceCapabilities.StableContent) with
        {
            Execute = new ExecuteOptions { MaxWorkingBytes = 1024 },
        };

        var decision = Plan(request);

        decision.Succeeded.ShouldBeFalse();
        decision.Refusal!.Backend.ShouldBe(ExecutionBackend.Tile);
        decision.Refusal.Reason.ShouldBe(ExecutionRefusalReason.Capability);
        decision.Candidates.Single(candidate => candidate.Backend == ExecutionBackend.Frame)
            .Reason.ShouldBe(ExecutionRefusalReason.Budget);
    }

    [Fact]
    public void UndeclaredScratch_IsUnverifiableByDefault_AndBestEffortWhenAllowed()
    {
        var request = Request(Recipe(UndeclaredPoint()), preferred: ExecutionBackend.Frame) with
        {
            Execute = new ExecuteOptions { MaxWorkingBytes = 1L << 20 },
        };

        var strict = Plan(request);
        strict.Succeeded.ShouldBeFalse();
        strict.Reason.ShouldBe(ExecutionRefusalReason.BudgetUnverifiable);
        strict.Refusal!.IsLowerBound.ShouldBeTrue();

        var bestEffort = Plan(request with
        {
            Execute = request.Execute with { AllowUnestimatedScratch = true },
        });
        bestEffort.Succeeded.ShouldBeTrue();
        bestEffort.BudgetBestEffort.ShouldBeTrue();
    }

    [Fact]
    public void TileWithoutRandomRegionSource_FallsBackToFrame()
    {
        var decision = Plan(Request(Recipe(PlanOps.Invert()), preferred: ExecutionBackend.Tile,
            tileRequested: true, sink: SinkCapabilities.RandomRegionWrite));

        decision.Succeeded.ShouldBeTrue();
        decision.SelectedBackend.ShouldBe(ExecutionBackend.Frame);
        decision.FallbackFrom!.Reason.ShouldBe(ExecutionRefusalReason.Capability);
        decision.FallbackFrom.Message.ShouldNotBeNull().ShouldContain("random regions");
    }

    [Fact]
    public void APolicyThatContradictsTheEntryPreference_IsAnInvalidRequest()
    {
        var decision = Plan(Request(Recipe(PlanOps.Invert()), preferred: ExecutionBackend.Stream,
            policy: ExecutionPolicy.RequireTiled));

        decision.Succeeded.ShouldBeFalse();
        decision.Reason.ShouldBe(ExecutionRefusalReason.InvalidRequest);
        decision.Message.ShouldNotBeNull().ShouldContain("conflicts");
    }

    private static ExecutionDecision Plan(ExecutionRequest request) => ExecutionPlanner.Plan(request);

    private static ExecutionRequest Request(PipelineRecipe recipe, int width = Width, int height = Height,
        ExecutionBackend? preferred = null, ExecutionPolicy policy = ExecutionPolicy.Auto,
        SinkCapabilities sink = SinkCapabilities.None, PixelRect? region = null,
        bool tileRequested = false,
        SourceCapabilities source = SourceCapabilities.Seekable | SourceCapabilities.StableContent) =>
        new()
        {
            Recipe = recipe,
            Width = width,
            Height = height,
            Format = WorkingFormat.Rgba8,
            Source = source,
            Sink = sink,
            Region = region,
            TileRequested = tileRequested,
            TileWidth = 64,
            TileHeight = 64,
            Preferred = preferred,
            Policy = policy,
            Execute = new ExecuteOptions(),
            BandRows = 64,
        };

    private static PipelineRecipe Recipe(params ImageOp[] ops)
    {
        var builder = new PipelineRecipeBuilder();
        foreach (var op in ops)
            builder.Append(op);
        return builder.Build();
    }

    private static ImageOp UndeclaredPoint()
    {
        var contract = OperatorContract.Create("undeclared-point", OpCategory.Point,
            WorkingFormatMask.All, IdentityFootprint.Instance, WorkspaceModel.Undeclared);

        return new ImageOp(contract, new KernelImplementations((source, destination, rect, _) =>
        {
            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
                source.Row(y).CopyTo(destination.Row(y));
        }));
    }
}
