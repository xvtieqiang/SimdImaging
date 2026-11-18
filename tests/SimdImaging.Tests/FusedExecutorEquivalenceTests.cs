using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// M10 fused-execution equivalence: every chain must produce the same bytes with
/// <c>AllowFusion = true</c> and <c>false</c>. The oracle is the step-by-step executor itself, so these
/// tests prove the window recursion (halo accumulation, shared-origin rects, ping-pong buffers) without
/// duplicating any arithmetic. The structured test proves the rectangle contract directly, because a
/// uniform image can hide a shifted window from a byte comparison.
/// </summary>
public class FusedExecutorEquivalenceTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(2, 4)]
    [InlineData(7, 2)]
    [InlineData(64, 1)]
    public void PointChain_IsByteIdentical(int bandRows, int degree)
    {
        using var image = Ramp(11, 17, WorkingFormat.Rgba8);
        var plan = Plan(bandRows,
            ("b", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.07f)), 0, 0),
            ("i", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)), 0, 0),
            ("c", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.15f)), 0, 0));

        AssertEquivalent(plan, View(image), degree);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(3, 4)]
    public void PointThenNeighborhood_WritesTheBandFromTheLastStep(int bandRows, int degree)
    {
        using var image = Ramp(11, 13, WorkingFormat.Rgba8);
        var plan = Plan(bandRows,
            ("p", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, -0.05f)), 0, 0),
            ("blur", OpCategory.Neighborhood, Blur(3), 3, 3));

        AssertEquivalent(plan, View(image), degree);
    }

    [Fact]
    public void NeighborhoodPair_RecursesTheHalos()
    {
        using var image = Ramp(17, 17, WorkingFormat.Rgba8);
        var plan = Plan(2,
            ("blur2", OpCategory.Neighborhood, Blur(2), 2, 2),
            ("blur3", OpCategory.Neighborhood, Blur(3), 3, 3));

        AssertEquivalent(plan, View(image), 1);
    }

    [Fact]
    public void NeighborhoodPointNeighborhood_IntermediatePointWritesTheWidenedBand()
    {
        using var image = Ramp(11, 17, WorkingFormat.Rgba8);
        var plan = Plan(2,
            ("blur3", OpCategory.Neighborhood, Blur(3), 3, 3),
            ("p", OpCategory.Point, PlanSteps.Point(v => PointOps.Gamma(v, 1.7f)), 0, 0),
            ("median", OpCategory.Neighborhood, PlanSteps.Neighborhood(v => FilterOps.Median3x3(v), 1, 1), 1, 1));

        AssertEquivalent(plan, View(image), 1);
    }

    [Fact]
    public void HaloLargerThanTheBand_RunsOnAFullImageWindow()
    {
        using var image = Ramp(7, 5, WorkingFormat.Rgba8);
        var plan = Plan(2,
            ("p", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.2f)), 0, 0),
            ("blur6", OpCategory.Neighborhood, Blur(6), 6, 6));

        AssertEquivalent(plan, View(image), 1);
    }

    [Fact]
    public void GlobalStep_SplitsTheChainAndSeesTheFullImage()
    {
        using var image = Ramp(9, 9, WorkingFormat.Rgba8);
        var plan = Plan(2,
            ("blur", OpCategory.Neighborhood, Blur(1), 1, 1),
            ("equalize", OpCategory.Global, PlanSteps.Global(v => HistogramOps.Equalize(v)), 0, 0),
            ("median", OpCategory.Neighborhood, PlanSteps.Neighborhood(v => FilterOps.Median3x3(v), 1, 1), 1, 1));

        AssertEquivalent(plan, View(image), 2);
    }

    [Fact]
    public void SeparableStep_IsBandableAndFusesWithAPointStep()
    {
        using var image = Ramp(9, 9, WorkingFormat.Rgba8);
        var plan = Plan(2,
            ("separable", OpCategory.Separable, Blur(1), 1, 1),
            ("p", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)), 0, 0));

        AssertEquivalent(plan, View(image), 1);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 4)]
    public void DeconvolutionStep_DeclaresItsPsfRadiusAsHalo(int bandRows, int degree)
    {
        using var image = Ramp(13, 11, WorkingFormat.Rgba8);
        const float sigma = 1.2f;
        var halo = RichardsonLucyOps.RequiredHalo(sigma, 3);

        var plan = Plan(bandRows,
            ("rl", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => RichardsonLucyOps.RichardsonLucy(v, sigma, 3), halo, halo), halo, halo),
            ("p", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)), 0, 0));

        AssertEquivalent(plan, View(image), degree);
    }

    [Fact]
    public void MotionDeconvolutionStep_IsBandableWithItsSupportRadius()
    {
        using var image = Ramp(15, 9, WorkingFormat.Rgba8);
        var halo = RichardsonLucyOps.RequiredHaloMotion(7, 2);

        var plan = Plan(2,
            ("motion", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => RichardsonLucyOps.RichardsonLucyMotion(v, 7, 30f, 2), halo, halo),
                halo, halo));

        AssertEquivalent(plan, View(image), 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    public void NonLocalMeans_BandedMatchesTheWholeImage(int bandRows)
    {
        // Non-local means reads searchRadius + patchRadius rows around the output band, so this is the
        // proof that its declared halo is the real support — the ring buffer's seeding is inside it.
        using var image = Ramp(13, 11, WorkingFormat.Rgba8);
        var plan = Plan(bandRows,
            ("nlm", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => NonLocalMeansOps.NonLocalMeans(v, 2, 1, 0.2f), 3, 3), 3, 3));

        using var banded = plan.ExecuteWithOptions(View(image), new ExecuteOptions { AllowFusion = false });
        using var whole = NonLocalMeansOps.NonLocalMeans(View(image), 2, 1, 0.2f);

        banded.View.Span.ToArray().ShouldBe(whole.View.Span.ToArray());
    }

    [Fact]
    public void NonLocalMeans_FusesWithAPointNeighbour()
    {
        using var image = Ramp(11, 13, WorkingFormat.Rgba8);
        var plan = Plan(2,
            ("p", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.05f)), 0, 0),
            ("nlm", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => NonLocalMeansOps.NonLocalMeans(v, 2, 1, 0.2f), 3, 3), 3, 3));

        AssertEquivalent(plan, View(image), 1);
    }

    [Theory]
    [InlineData(WorkingFormat.Rgba16)]
    [InlineData(WorkingFormat.Rgba32F)]
    public void OtherFormats_AreByteIdentical(WorkingFormat format)
    {
        using var image = Ramp(13, 7, format);
        var plan = Plan(3,
            ("blur", OpCategory.Neighborhood, Blur(2), 2, 2),
            ("p", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.03f)), 0, 0));

        AssertEquivalent(plan, View(image), 2);
    }

    [Fact]
    public void FusedWindows_HandEachStepExactlyWhatThePreviousStepWrote()
    {
        var first = new ConcurrentBag<(PixelRect Dst, PixelRect Src)>();
        var second = new ConcurrentBag<(PixelRect Dst, PixelRect Src)>();

        OpKernel recordFirst = (_, destination, dst, src) =>
        {
            first.Add((dst, src));
            Paint(destination, dst);
        };
        OpKernel recordSecond = (_, destination, dst, src) =>
        {
            second.Add((dst, src));
            Paint(destination, dst);
        };

        using var image = Ramp(11, 13, WorkingFormat.Rgba8);
        var plan = Plan(3,
            ("a", OpCategory.Neighborhood, recordFirst, 2, 2),
            ("b", OpCategory.Neighborhood, recordSecond, 3, 3));

        // This test is about the fused windows, so it asks for fusion rather than relying on the heuristic.
        using var result = plan.ExecuteWithOptions(View(image),
            new ExecuteOptions { MaxDegreeOfParallelism = 2, ForceFusion = true });

        first.Count.ShouldBe(second.Count, "every band runs both steps once");
        first.Count.ShouldBeGreaterThanOrEqualTo(5); // ceil(13 / 3)

        foreach (var (dst, src) in first)
        {
            // A step reads at least what it writes, and never outside the image.
            src.Y.ShouldBeLessThanOrEqualTo(dst.Y);
            (src.Y + src.Height).ShouldBeGreaterThanOrEqualTo(dst.Y + dst.Height);
            src.Y.ShouldBeGreaterThanOrEqualTo(0);
            (src.Y + src.Height).ShouldBeLessThanOrEqualTo(image.Height);
            src.Width.ShouldBe(image.Width);
        }

        foreach (var (dst, src) in second)
        {
            dst.Height.ShouldBeLessThanOrEqualTo(3);
            src.Y.ShouldBeLessThanOrEqualTo(dst.Y);
            (src.Y + src.Height).ShouldBeGreaterThanOrEqualTo(dst.Y + dst.Height);
            first.ShouldContain(call => call.Dst.Equals(src),
                "step 2's source rect must be exactly the rect step 1 wrote");
        }
    }

    [Fact]
    public void MaxWorkingBytes_AboveTheEstimate_Throws()
    {
        using var image = Ramp(64, 64, WorkingFormat.Rgba8);
        var plan = Plan(16,
            ("a", OpCategory.Neighborhood, Blur(3), 3, 3),
            ("b", OpCategory.Neighborhood, Blur(3), 3, 3));

        Should.Throw<InvalidOperationException>(() => plan.ExecuteWithOptions(View(image),
            new ExecuteOptions { MaxWorkingBytes = 1024 }));

        // A ceiling above the estimate must not throw. These steps declare no scratch, so the estimate is a lower
        // bound and the executor refuses to pretend otherwise unless the caller says a lower bound is enough.
        Should.Throw<InvalidOperationException>(() => plan.ExecuteWithOptions(View(image),
            new ExecuteOptions { MaxWorkingBytes = 1L << 30 }));

        using var result = plan.ExecuteWithOptions(View(image),
            new ExecuteOptions { MaxWorkingBytes = 1L << 30, AllowUnestimatedScratch = true });
        result.Height.ShouldBe(64);
    }

    /// <summary>
    /// An undeclared operator is not a free one. The ceiling cannot be enforced, so the default is to say so
    /// rather than to report a working set the plan may well exceed.
    /// </summary>
    [Fact]
    public void MaxWorkingBytes_WithUndeclaredScratch_RefusesInsteadOfGuessing()
    {
        using var image = Ramp(64, 64, WorkingFormat.Rgba8);
        var plan = Plan(16, ("a", OpCategory.Neighborhood, Blur(3), 3, 3));

        var error = Should.Throw<InvalidOperationException>(() => plan.ExecuteWithOptions(View(image),
            new ExecuteOptions { MaxWorkingBytes = 1L << 30 }));

        error.Message.ShouldContain("declare no scratch");
    }

    [Fact]
    public void CancelledToken_ThrowsBeforeWriting()
    {
        using var image = Ramp(16, 16, WorkingFormat.Rgba8);
        var plan = Plan(4,
            ("a", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)), 0, 0),
            ("b", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)), 0, 0));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(() => plan.ExecuteWithOptions(View(image),
            new ExecuteOptions { CancellationToken = cancelled.Token }));
    }

    [Fact]
    public void Diagnostics_ReportsSegmentsAndFallbacks()
    {
        using var image = Ramp(9, 9, WorkingFormat.Rgba8);
        var plan = Plan(2,
            ("blur", OpCategory.Neighborhood, Blur(1), 1, 1),
            ("p", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)), 0, 0),
            ("global", OpCategory.Global, PlanSteps.Global(v => HistogramOps.Equalize(v)), 0, 0));

        plan.ExecuteWithOptions(View(image), new ExecuteOptions { Diagnostics = true }, out var report);
        report!.SegmentCount.ShouldBe(2);
        report.Segments[0].StepCount.ShouldBe(2);
        report.Segments[0].Fused.ShouldBeTrue();
        report.Segments[1].Fused.ShouldBeFalse();
        report.FallbackReason.ShouldBe(FusionFallbackReason.CategoryBoundary);
        report.BandCount.ShouldBe(5 + 1); // five bands for the fused pair, one whole-image step

        using var observed = plan.ExecuteWithOptions(View(image), new ExecuteOptions
        {
            Diagnostics = true,
            AllowFusion = true,
        }, out var withObserver, observe: (_, _, _) => { });

        withObserver!.FallbackReason.ShouldBe(FusionFallbackReason.ObserverPresent);
        withObserver.Segments.ShouldAllBe(s => !s.Fused);
    }

    [Fact]
    public void LocalLaplacian_IsAGlobalBoundaryAndMatchesTheDirectCall()
    {
        using var image = Ramp(24, 16, WorkingFormat.Rgba8);
        var plan = Plan(2,
            ("ll", OpCategory.Global,
                PlanSteps.Global(v => LocalLaplacianOps.LocalLaplacian(v, levels: 3, samples: 8, alpha: 0.3f)), 0, 0));

        using var throughPlan = plan.ExecuteWithOptions(View(image),
            new ExecuteOptions { Diagnostics = true }, out var report);
        using var direct = LocalLaplacianOps.LocalLaplacian(View(image), levels: 3, samples: 8, alpha: 0.3f);

        throughPlan.View.Span.ToArray().ShouldBe(direct.View.Span.ToArray());
        report!.FallbackReason.ShouldBe(FusionFallbackReason.CategoryBoundary);
        report.Segments.ShouldAllBe(segment => !segment.Fused);
    }

    private static ProcessingPlan Plan(int bandRows,
        params (string Name, OpCategory Category, OpKernel Kernel, int HaloX, int HaloY)[] steps)
    {
        var plan = new ProcessingPlan { BandRows = bandRows };
        foreach (var (name, category, kernel, haloX, haloY) in steps)
            plan.Add(name, category, kernel, haloX, haloY);

        return plan;
    }

    private static OpKernel Blur(int radius) =>
        PlanSteps.Neighborhood(view => FilterOps.BoxBlur(view, radius), radius, radius);

    private static void AssertEquivalent(ProcessingPlan plan, PixelView view, int degree)
    {
        using var fused = plan.ExecuteWithOptions(view,
            new ExecuteOptions { AllowFusion = true, MaxDegreeOfParallelism = degree });
        using var plain = plan.ExecuteWithOptions(view,
            new ExecuteOptions { AllowFusion = false, MaxDegreeOfParallelism = degree });

        fused.View.Span.ToArray().ShouldBe(plain.View.Span.ToArray());
    }

    private static void Paint(PixelView destination, PixelRect rect)
    {
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            destination.Row(y).Slice(rect.X * 4, rect.Width * 4).Clear();
    }

    private static PixelView View(Image image) => image[0].Pixels.View;

    private static Image Ramp(int width, int height, WorkingFormat format)
    {        var image = Image.Create(width, height, format);
        var pixels = image.WritablePixels;
        if (format == WorkingFormat.Rgba32F)
        {
            var floats = MemoryMarshal.Cast<byte, float>(pixels);
            for (var i = 0; i < floats.Length; i++)
                floats[i] = (i * 37 % 101) / 101f;
        }
        else
        {
            new Random(20261114).NextBytes(pixels);
        }

        return image;
    }
}
