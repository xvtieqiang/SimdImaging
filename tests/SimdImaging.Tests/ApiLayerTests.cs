using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>Two layers, one result: the fluent pipeline and the plan it compiles to must not drift.</summary>
public class ApiLayerTests
{
    [Fact]
    public void PipelineResize_AndPlanResize_ProduceTheSameBytes()
    {
        using var image = TestImages.Gradient(320, 200, WorkingFormat.Rgba8);

        using var fluent = image.Pipe().GaussianBlur(1f).Contrast(1.1f).Resize(97, 61).ToImage();
        using var planned = image.Apply(new ProcessingPlan().Add(PlanOps.GaussianBlur(1f))
            .Add(PlanOps.Contrast(1.1f)).Resize(97, 61));

        planned.Width.ShouldBe(fluent.Width);
        planned.Height.ShouldBe(fluent.Height);
        planned.WritablePixels.ToArray().ShouldBe(fluent.WritablePixels.ToArray());
    }
}
