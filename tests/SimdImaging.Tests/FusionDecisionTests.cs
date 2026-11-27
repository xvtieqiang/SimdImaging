using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Fusion;
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

        // A Global step splits the chain, and each side is now decided on its own: the blur pair avoids one
        // frame (33.2 MB, under the threshold) so it runs stepwise, while the trailing halo-free point pair
        // fuses because a zero-halo chain costs nothing to fuse. That is four segments (blur, brightness,
        // equalize, fused point pair), not the five a plan-wide decision produced when the blur's halo
        // dragged the point pair down with it.
        { 1920, 1080, "blur-global-point", WorkingFormat.Rgba32F, 4 },
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

    [Fact]
    public void AHaloPairIsSplitForAFrameAndFusedForAPump()
    {
        // The same two steps, priced for the executor that would run them. On the whole-image path a boundary is a
        // materialised frame and avoiding one is 7.9 MB at 1080p — under the 64 MiB the table asks for, so it runs
        // them stepwise. On the pump a boundary is one more stage: two band windows held for the life of the chain
        // plus one more copy of every row. Measured on exactly this chain (decided vs forced, bytes identical):
        //   1080p  303.3 ms / 36.7 MiB  ->  298.6 ms / 30.7 MiB
        //   4K     691.0 ms / 55.1 MiB  ->  692.9 ms / 50.4 MiB      (benchmarks/SimdImaging.Comparison fusionab)
        // so the pump keeps one segment and the frame path keeps two.
        var plan = new ProcessingPlan()
            .Add("blur", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1.5f), 4, 4), 4, 4)
            .Add("invert", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)));

        var steps = plan.Steps;
        FusionDecision.PlanSegments(steps, 1920, 1080, 4, allowFusion: true, forceFusion: false)
            .ShouldBe([(0, 1), (1, 1)], "a frame boundary is not worth fusing for one saved frame");
        FusionDecision.PlanSegments(steps, 1920, 1080, 4, allowFusion: true, forceFusion: false,
                FusionBoundary.RowPump)
            .ShouldBe([(0, 2)], "a stage boundary is, and the two were measured");

        // The halo budget is the pump's remaining refusal: 9 × 4 = 36 rows is past it, so the run is cut at 32 even
        // though a stage boundary is cheap — beyond the budget every band re-runs the chain over more than 32 rows of
        // overlap, which no boundary price pays for.
        var longPlan = new ProcessingPlan();
        for (var i = 0; i < 9; i++)
            longPlan.Add($"blur{i}", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1f), 4, 4), 4, 4);

        FusionDecision.PlanSegments(longPlan.Steps, 1920, 1080, 4, allowFusion: true, forceFusion: false,
                FusionBoundary.RowPump)
            .ShouldBe([(0, 8), (8, 1)], "Σhalo = 36 is past the budget, so the pump splits at 32");
    }

    [Fact]
    public void AChainPastTheHaloBudgetFusesItsCheapPrefix()
    {
        // Eleven radius-3 steps stack to 33 halo rows: one row past the budget, so the run as a whole is refused.
        // Splitting at the budget keeps the first ten (Σ = 30) and leaves the last step on its own — two segments
        // instead of eleven. The prefix is fused on the same terms the run was judged on: at 1080p its nine
        // avoided frames are 74.6 MB, above the 64 MiB threshold.
        var plan = new ProcessingPlan { BandRows = 64 };
        for (var i = 0; i < 11; i++)
            plan.Add($"box{i}", OpCategory.Neighborhood, PlanSteps.Neighborhood(v => FilterOps.BoxBlur(v, 3), 3, 3), 3, 3);

        using var image = Image.Create(1920, 1080, WorkingFormat.Rgba8);
        using var result = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { Diagnostics = true }, out var report);

        var shape = report!.Segments.Select(segment => (segment.FirstStep, segment.StepCount, segment.Fused)).ToArray();
        shape.ShouldBe([(0, 10, true), (10, 1, false)]);
    }

    [Fact]
    public void TheSegmentedChainIsByteIdenticalToStepwise()
    {
        // The same split as above, compared against the path it replaced: a segmentation that chooses a different
        // window for the same steps is only a performance decision if the bytes agree.
        var plan = new ProcessingPlan { BandRows = 64 };
        for (var i = 0; i < 11; i++)
            plan.Add($"box{i}", OpCategory.Neighborhood, PlanSteps.Neighborhood(v => FilterOps.BoxBlur(v, 3), 3, 3), 3, 3);

        using var image = Image.Create(1920, 1080, WorkingFormat.Rgba8);
        new Random(20261128).NextBytes(image.WritablePixels);

        using var segmented = plan.ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions());
        using var stepwise = plan.ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions { AllowFusion = false });

        segmented.View.Span.SequenceEqual(stepwise.View.Span).ShouldBeTrue(
            "a sub-segment boundary is a full frame, so it cannot change a byte");
    }

    [Theory]
    [InlineData(1920, 1080, 52, 67, 208)]
    [InlineData(1920, 400, 52, 25, 100)]
    [InlineData(1920, 400, 28, 25, 100)]
    [InlineData(640, 64, 32, 8, 16)]
    public void AHaloBandNeverLeavesFewerThanFourBands(int width, int height, int halo, int policyBand, int expected)
    {
        // The band count is the parallelism: ForBands starts min(degree, bands) workers, so a 4×Σhalo band on a short
        // image leaves most of the machine idle. The segments are stated rather than derived because the point is the
        // band rule, not the decision that gets there — and on a short image the traffic rule usually declines to
        // fuse anyway, so this only has to be right when something forces it.
        var plan = new ProcessingPlan();
        for (var i = 0; i < 2; i++)
        {
            plan.Add($"blur{i}", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1f), halo / 2, halo / 2), halo / 2, halo / 2);
        }

        var segments = new List<(int First, int Count)> { (0, 2) };
        var bandRows = FusionDecision.ExecBandRows(plan.Steps, segments, width, height, 4, policyBand);

        bandRows.ShouldBe(expected);
        ((height + bandRows - 1) / bandRows).ShouldBeGreaterThanOrEqualTo(Math.Min(4, height),
            "a fused halo segment must never collapse to one or two bands");
    }

    [Fact]
    public void AHaloBudgetKeepsATallChainStepwiseEvenWhenItWouldRemoveFrames()
    {
        // Six ±64-row steps stack to 384 halo rows on a 64-row band: every band would re-run all six steps over a
        // 768-row overlap. The 4K chain removes five 33 MB frames (166 MB, above the 64 MiB traffic rule), so the
        // traffic rule alone would still fuse it — the halo budget is what keeps it stepwise, and the measurement
        // behind the budget is 252 ms fused versus 157 ms step by step at 1080p (986 vs 607 at 4K).
        var plan = new ProcessingPlan { BandRows = 64 };
        for (var i = 0; i < 6; i++)
        {
            plan.Add($"blur{i}", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1.1f), 64, 64), 64, 64);
        }

        using var image = Image.Create(3840, 2160, WorkingFormat.Rgba8);
        using var result = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { Diagnostics = true }, out var report);

        report!.SegmentCount.ShouldBe(6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompiledSectionsAndTheRuntimeReportShareThePerRunDecision(bool forceFusion)
    {
        // A halo pair (stepwise at 640x360), a Global boundary, and a zero-halo pair (always worth fusing).
        // The compiler and the direct executor must cut this into the same segments with the same decisions.
        var ops = new[]
        {
            PlanOps.GaussianBlur(1f), PlanOps.GaussianBlur(1f), PlanOps.Equalize(),
            PlanOps.Invert(), PlanOps.Invert(),
        };
        var plan = new ProcessingPlan { BandRows = 64 };
        foreach (var op in ops)
            plan.Add(op);

        var builder = new PipelineRecipeBuilder();
        foreach (var op in ops)
            builder.Append(op);
        var compileOptions = new CompileOptions
        {
            SourceWidth = 640,
            SourceHeight = 360,
            SourceFormat = WorkingFormat.Rgba8,
            Sink = SinkCapabilities.None,
            Execute = new ExecuteOptions { ForceFusion = forceFusion },
        };
        var compiled = PipelineCompiler.Compile(builder.Build(), compileOptions);
        var compiledShape = compiled.Sections
            .Where(section => section.Kind == CompiledSectionKind.OperatorRun)
            .Select(section => (section.StepCount, section.Fused))
            .ToArray();

        using var image = Image.Create(640, 360, WorkingFormat.Rgba8);
        using var result = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { Diagnostics = true, ForceFusion = forceFusion }, out var report);
        var runtimeShape = report!.Segments.Select(segment => (segment.StepCount, segment.Fused)).ToArray();

        runtimeShape.ShouldBe(compiledShape,
            $"compiled and direct execution disagreed (forceFusion={forceFusion})");

        if (forceFusion)
            compiledShape.ShouldBe([(2, true), (1, false), (2, true)]);
        else
            compiledShape.ShouldBe([(1, false), (1, false), (1, false), (2, true)]);
    }
}
