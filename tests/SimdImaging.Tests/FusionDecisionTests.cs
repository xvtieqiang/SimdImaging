using SimdImaging.Abstractions;
using SimdImaging.Processing;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// Pins which path a chain takes at the sizes that were measured. The rule itself has no timing in it, so this is a
/// behaviour table, not a performance test: it exists so that a change to the heuristic has to face the measurements
/// it was derived from rather than a single new reading.
/// </summary>
public sealed class FusionDecisionTests
{
    public static TheoryData<int, int, string, WorkingFormat, int> Table => new()
    {
        // size, chain, format, expected segments (1 = one fused segment, N = N stepwise passes).
        // The 64 MiB threshold counts an intermediate in the SOURCE's bytes per pixel, so the same chain crosses it
        // at different sizes per format: a 4K Rgba8 frame is 33 MiB, a 4K Rgba32F frame is 133 MiB.
        { 160, 120, "point", WorkingFormat.Rgba8, 1 },
        { 320, 180, "point", WorkingFormat.Rgba8, 1 },
        { 640, 360, "point", WorkingFormat.Rgba8, 1 },
        { 1920, 1080, "point", WorkingFormat.Rgba8, 1 },
        { 160, 120, "3x3", WorkingFormat.Rgba8, 1 },
        { 320, 180, "3x3", WorkingFormat.Rgba8, 1 },
        { 640, 360, "3x3", WorkingFormat.Rgba8, 2 },
        { 1920, 1080, "3x3", WorkingFormat.Rgba8, 2 },
        { 3840, 2160, "3x3", WorkingFormat.Rgba8, 2 },
        { 7680, 4320, "3x3", WorkingFormat.Rgba8, 1 },
        { 160, 120, "blur", WorkingFormat.Rgba8, 1 },
        { 320, 180, "blur", WorkingFormat.Rgba8, 1 },
        { 640, 360, "blur", WorkingFormat.Rgba8, 3 },
        { 1920, 1080, "blur", WorkingFormat.Rgba8, 3 },
        { 3840, 2160, "blur", WorkingFormat.Rgba8, 3 },   // 66.4 MB of intermediates, just under 64 MiB

        // The byte count used to be 4 whatever the format was: a 16-bit or float chain was charged a quarter or a
        // sixteenth of the traffic it really avoids. These rows are the same chains where that made the decision
        // wrong, not merely imprecise.
        { 3840, 2160, "blur", WorkingFormat.Rgba16, 1 },   // 133 MB of intermediates
        { 3840, 2160, "blur", WorkingFormat.Rgba32F, 1 },  // 265 MB
        { 1920, 1080, "blur4", WorkingFormat.Rgba32F, 1 }, // 99.5 MB, three intermediates
        { 1920, 1080, "blur4", WorkingFormat.Rgba16, 4 },  // 49.8 MB, still stepwise
        { 1920, 1080, "blur4", WorkingFormat.Rgba8, 4 },   // 24.9 MB

        // A Global step splits the chain, so only the steps on each side of it are intermediates fusion can remove.
        // Counting steps-1 credited this plan with four frames (133 MB) and fused it; the two runs of two steps
        // only avoid one frame each (66.4 MB, under the threshold), so it runs stepwise.
        { 1920, 1080, "blur-global-point", WorkingFormat.Rgba32F, 5 },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void TheChainTakesTheMeasuredPath(int width, int height, string chain, WorkingFormat format, int segments)
    {
        using var image = Image.Create(width, height, format);
        var plan = chain switch
        {
            "point" => new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.Contrast(1.1f)),
            "3x3" => new ProcessingPlan().Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel()),
            "blur4" => new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Brightness(0.05f))
                .Add(PlanOps.Contrast(1.1f)).Add(PlanOps.Brightness(0.05f)),
            "blur-global-point" => new ProcessingPlan().Add(PlanOps.GaussianBlur(1f))
                .Add(PlanOps.Brightness(0.05f)).Add(PlanOps.Equalize())
                .Add(PlanOps.Contrast(1.1f)).Add(PlanOps.Brightness(0.05f)),
            _ => new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Brightness(0.05f))
                .Add(PlanOps.Contrast(1.1f)),
        };

        using var result = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { Diagnostics = true }, out var report);

        report!.SegmentCount.ShouldBe(segments,
            $"{chain}/{format} at {width}x{height}: the decision table pins the measured path");
    }
}
