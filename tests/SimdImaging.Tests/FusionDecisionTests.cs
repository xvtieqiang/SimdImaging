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
    public static TheoryData<int, int, string, int> Table => new()
    {
        // size, chain, expected segments (1 = one fused segment, N = N stepwise passes).
        // An intermediate is an Rgba8 frame, 4 bytes a pixel, so the 64 MiB branch is reached at about 16.8 MP.
        { 160, 120, "point", 1 },
        { 320, 180, "point", 1 },
        { 640, 360, "point", 1 },
        { 1920, 1080, "point", 1 },
        { 160, 120, "3x3", 1 },
        { 320, 180, "3x3", 1 },
        { 640, 360, "3x3", 2 },
        { 1920, 1080, "3x3", 2 },
        { 3840, 2160, "3x3", 2 },
        { 7680, 4320, "3x3", 1 },
        { 160, 120, "blur", 1 },
        { 320, 180, "blur", 1 },
        { 640, 360, "blur", 3 },
        { 1920, 1080, "blur", 3 },
        { 3840, 2160, "blur", 3 },   // 66.4 MB of intermediates, just under 64 MiB
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void TheChainTakesTheMeasuredPath(int width, int height, string chain, int segments)
    {
        using var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var plan = chain switch
        {
            "point" => new ProcessingPlan().Add(PlanOps.Brightness(0.05f)).Add(PlanOps.Contrast(1.1f)),
            "3x3" => new ProcessingPlan().Add(PlanOps.Emboss(0.6f)).Add(PlanOps.Sobel()),
            _ => new ProcessingPlan().Add(PlanOps.GaussianBlur(1f)).Add(PlanOps.Brightness(0.05f))
                .Add(PlanOps.Contrast(1.1f)),
        };

        using var result = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { Diagnostics = true }, out var report);

        report!.SegmentCount.ShouldBe(segments,
            $"{chain} at {width}x{height}: the decision table pins the measured path");
    }
}
