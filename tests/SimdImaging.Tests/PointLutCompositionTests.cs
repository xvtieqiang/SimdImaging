using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// M10 point-chain folding: consecutive <see cref="ProcessingPlan.AddPointLut"/> steps compose into one
/// 4×256 table and run in a single pass. The oracle is the same operators applied one after another
/// through their own byte-LUT path, so the test proves composition is constructively exact rather than
/// "close enough" — a float-domain composition would drift on the intermediate quantisation.
/// </summary>
public class PointLutCompositionTests
{
    [Fact]
    public void ComposedTables_MatchSequentialApplication()
    {
        using var ramp = Ramp();
        using var afterBrightness = PointOps.Brightness(ramp.View, 0.11f);
        using var afterContrast = PointOps.Contrast(afterBrightness.View, 1.35f);
        var expected = afterContrast.View.Span.ToArray();

        var plan = new ProcessingPlan { BandRows = 4 }
            .AddPointLut("brightness", TableOf(ramp, view => PointOps.Brightness(view, 0.11f)))
            .AddPointLut("contrast", TableOf(ramp, view => PointOps.Contrast(view, 1.35f)));

        using var fused = plan.Execute(ramp.View);
        fused.View.Span.ToArray().ShouldBe(expected);

        using var stepByStep = plan.ExecuteWithOptions(ramp.View, new ExecuteOptions { AllowFusion = false });
        stepByStep.View.Span.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void ThreeTables_FoldIntoOnePassAndReportAsTwoOriginalSteps()
    {
        using var ramp = Ramp();
        using var a = PointOps.Brightness(ramp.View, -0.07f);
        using var b = PointOps.Contrast(a.View, 0.8f);
        using var c = PointOps.Gamma(b.View, 2.2f);
        var expected = c.View.Span.ToArray();

        var plan = new ProcessingPlan()
            .AddPointLut("brightness", TableOf(ramp, view => PointOps.Brightness(view, -0.07f)))
            .AddPointLut("contrast", TableOf(ramp, view => PointOps.Contrast(view, 0.8f)))
            .AddPointLut("gamma", TableOf(ramp, view => PointOps.Gamma(view, 2.2f)));

        // Three tables folding into one pass is what fusion does; the heuristic routes a short point chain
        // stepwise, so the folding is asked for here.
        using var fused = plan.ExecuteWithOptions(ramp.View,
            new ExecuteOptions { Diagnostics = true, ForceFusion = true }, out var report);

        fused.View.Span.ToArray().ShouldBe(expected);
        report!.SegmentCount.ShouldBe(1);
        report.Segments[0].StepCount.ShouldBe(3, "the segment still describes the three original steps");
        report.Segments[0].Name.ShouldContain("brightness");
        report.Segments[0].Name.ShouldContain("gamma");
        report.BandCount.ShouldBe(1, "one 256×1 image is a single band");
    }

    [Fact]
    public void FoldingIsSkippedWhenTheObserverNeedsEveryStep()
    {
        using var ramp = Ramp();
        var plan = new ProcessingPlan()
            .AddPointLut("invert", InvertTable())
            .AddPointLut("invert-again", InvertTable());

        var observed = new List<string>();
        using var result = plan.Execute(ramp.View, observe: (_, name, _) => observed.Add(name));

        observed.ShouldBe(["invert", "invert-again"]);
        // Two inversions are the identity, which also proves the composition order is not reversed.
        result.View.Span.ToArray().ShouldBe(ramp.View.Span.ToArray());
    }

    [Fact]
    public void NonRgba8_IsRejectedLoudly()
    {
        using var floated = WorkingBuffer.Rent(8, 4, WorkingFormat.Rgba32F);
        var plan = new ProcessingPlan().AddPointLut("invert", InvertTable());

        Should.Throw<NotSupportedException>(() => plan.Execute(floated.View));
    }

    private static WorkingBuffer Ramp()
    {
        var ramp = WorkingBuffer.Rent(256, 1, WorkingFormat.Rgba8);
        for (var v = 0; v < 256; v++)
        {
            // The internal byte-LUT builder probes with all four channels equal to v; the probe table
            // below must use the same ramp or it would measure a different function.
            ramp.View.Span[v * 4] = (byte)v;
            ramp.View.Span[v * 4 + 1] = (byte)v;
            ramp.View.Span[v * 4 + 2] = (byte)v;
            ramp.View.Span[v * 4 + 3] = (byte)v;
        }

        return ramp;
    }

    /// <summary>Runs one operator over the 256-entry ramp and reads back its 4×256 byte table.</summary>
    private static byte[] TableOf(WorkingBuffer ramp, Func<PixelView, WorkingBuffer> op)
    {
        using var result = op(ramp.View);
        var table = new byte[4 * 256];
        for (var v = 0; v < 256; v++)
        {
            for (var channel = 0; channel < 4; channel++)
                table[channel * 256 + v] = result.View.Span[v * 4 + channel];
        }

        return table;
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
