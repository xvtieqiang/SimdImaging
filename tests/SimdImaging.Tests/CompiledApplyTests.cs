using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Contracts;

namespace SimdImaging.Tests;

/// <summary>
/// The facade's <c>Apply</c> compiles the plan instead of running the old executor. These pin the two things that
/// could drift silently: the bytes and the explanation.
/// </summary>
public sealed class CompiledApplyTests
{
    [Fact]
    public void Apply_MatchesThePlanExecutor_ForTablesBlurAndResize()
    {
        using var image = TestImages.Gradient(37, 23, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan { BandRows = 5 }
            .AddPointLut("invert", InvertTable())
            .Add(PlanOps.GaussianBlur(1f))
            .Resize(20, 13, Resampler.Bilinear);

        using var applied = image.Apply(plan);
        using var executed = plan.Execute(image[0].Pixels.View);

        applied.Width.ShouldBe(20);
        applied.Height.ShouldBe(13);
        applied.WritablePixels.ToArray().ShouldBe(executed.View.Span.ToArray());
    }

    [Fact]
    public void Apply_EmptyPlan_ReturnsACopy()
    {
        using var image = TestImages.Gradient(8, 5, WorkingFormat.Rgba8);

        using var copy = image.Apply(new ProcessingPlan());

        copy.WritablePixels.ToArray().ShouldBe(image.WritablePixels.ToArray());
        copy.Width.ShouldBe(image.Width);
        copy.Height.ShouldBe(image.Height);
    }

    [Fact]
    public void Explain_DescribesTheOperatorAndTheGeometryStep()
    {
        using var image = TestImages.Gradient(40, 30, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan()
            .Add(PlanOps.Invert())
            .Resize(20, 15, Resampler.Nearest);

        var info = image.Explain(plan, new PixelRect(2, 3, 4, 5));

        info.Backend.ShouldBe(PlanBackend.Frame);
        info.Output.SizeKnown.ShouldBeTrue();
        info.Output.Width.ShouldBe(20);
        info.Output.Height.ShouldBe(15);
        info.Steps.Count.ShouldBe(2);
        info.Steps[0].Name.ShouldBe("Invert");
        info.Steps[0].IsGeometry.ShouldBeFalse();
        info.Steps[0].Requirements.Partitioning.ShouldBe(Partitioning.RowBands);
        info.Steps[1].IsGeometry.ShouldBeTrue();
        info.Steps[1].Width.ShouldBe(20);
        info.Steps[1].Height.ShouldBe(15);
        info.Sections.ShouldNotBeEmpty();
        info.Workspace.PredictedBytes.ShouldBeGreaterThan(0);
        info.Workspace.Terms.ShouldContain(term => term.Kind == PlanWorkspaceKind.Destination);
    }

    [Fact]
    public void Explain_ReportsTheRequirementsTheContractDeclared()
    {
        using var image = TestImages.Gradient(24, 18, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Noise(0.05f, seed: 7));

        var info = image.Explain(plan);

        var requirements = info.Steps[0].Requirements;
        requirements.Partitioning.ShouldBe(Partitioning.WholeImage);
        requirements.State.ShouldBe(StateScope.PerCall);
        requirements.Order.ShouldBe(ExecutionOrder.SourceOrder);
        requirements.Determinism.ShouldBe(Determinism.SeededSequence);
        info.Notes.ShouldNotBeEmpty();
    }

    private static byte[] InvertTable()
    {
        var table = new byte[4 * 256];
        for (var v = 0; v < 256; v++)
        {
            table[v] = (byte)(255 - v);
            table[256 + v] = (byte)(255 - v);
            table[512 + v] = (byte)(255 - v);
            table[768 + v] = (byte)v;
        }

        return table;
    }
}
