using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

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

        info.Count.ShouldBe(2);
        info[0].Name.ShouldBe("Invert");
        info[0].IsGeometry.ShouldBeFalse();
        info[1].IsGeometry.ShouldBeTrue();
        info[1].Width.ShouldBe(20);
        info[1].Height.ShouldBe(15);
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
