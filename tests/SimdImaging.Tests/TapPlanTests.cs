using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// Direct unit tests for <see cref="TapPlanBuilder"/> — until M9 it had zero direct coverage and was
/// exercised only through Resize/Warp behaviour. The frozen numbers here are the contract the other
/// chains build on: scale, filter widening, radius, tap count, the degenerate fallback and the
/// normalised weight sum.
/// </summary>
public class TapPlanTests
{
    [Theory]
    [InlineData(Resampler.Bilinear, 1.0, 1.0, 4)]   // radius 1 → ceil(2)+2
    [InlineData(Resampler.Bicubic, 1.0, 2.0, 6)]    // radius 2 → ceil(4)+2
    [InlineData(Resampler.Lanczos3, 1.0, 3.0, 8)]   // radius 3 → ceil(6)+2
    [InlineData(Resampler.Nearest, 1.0, 0.5, 3)]    // radius 0.5 → ceil(1)+2
    public void Parameters_UpscaleKeepsTheKernelAndCountsTaps(Resampler resampler,
        double expectedScale, double expectedRadius, int expectedTaps)
    {
        var (scale, filterScale, radius, taps, _) = TapPlanBuilder.Parameters(10, 10, resampler);

        scale.ShouldBe(expectedScale, 1e-12);
        filterScale.ShouldBe(1.0, 1e-12);
        radius.ShouldBe(expectedRadius, 1e-12);
        taps.ShouldBe(expectedTaps);
    }

    [Fact]
    public void Parameters_DownscaleWidensTheKernelByOneOverScale()
    {
        var (scale, filterScale, radius, _, _) = TapPlanBuilder.Parameters(20, 5, Resampler.Bilinear);

        scale.ShouldBe(0.25, 1e-12);
        filterScale.ShouldBe(4.0, 1e-12);   // 1/scale: area sampling
        radius.ShouldBe(4.0, 1e-12);        // support 1 × filterScale 4
    }

    [Fact]
    public void Parameters_NearestNeverWidens()
    {
        var (_, filterScale, radius, _, _) = TapPlanBuilder.Parameters(100, 10, Resampler.Nearest);

        filterScale.ShouldBe(1.0, 1e-12);   // point sampling at any scale
        radius.ShouldBe(0.5, 1e-12);
    }

    [Fact]
    public void FillAt_PutsTheWholeWeightOnTheNearestPixelAtAnExactCentre()
    {
        // areal centre 2.5 = pixel 2's centre: weights must be (0, 1, 0, 0) for the 4-tap window.
        Span<double> buffer = new double[4];
        var start = TapPlanBuilder.FillAt(buffer, 2.5, 10, 1.0, 1.0, ResamplerKernels.Get(Resampler.Bilinear).Kernel);

        start.ShouldBe(1);
        buffer[0].ShouldBe(0.0, 1e-12);
        buffer[1].ShouldBe(1.0, 1e-12);
        buffer[2].ShouldBe(0.0, 1e-12);
        buffer[3].ShouldBe(0.0, 1e-12);
    }

    [Fact]
    public void FillAt_NormalisesTheWeightSumToOne()
    {
        Span<double> buffer = new double[4];
        TapPlanBuilder.FillAt(buffer, 2.37, 10, 1.0, 1.0, ResamplerKernels.Get(Resampler.Bilinear).Kernel);

        var sum = 0.0;
        foreach (var w in buffer) sum += w;
        sum.ShouldBe(1.0, 1e-12);
    }

    [Fact]
    public void FillAt_FallsBackToTheNearestPixelWhenTheWindowIsEmpty()
    {
        // A centre far outside the kernel reach yields sum == 0; the fallback must pick the nearest
        // pixel (clamped) with weight 1 rather than return all zeros.
        Span<double> buffer = new double[4];
        var start = TapPlanBuilder.FillAt(buffer, 9.7, 10, 1.0, 0.5, ResamplerKernels.Get(Resampler.Nearest).Kernel);

        var sum = 0.0;
        foreach (var w in buffer) sum += w;
        sum.ShouldBe(1.0, 1e-12);
        (start >= 0).ShouldBeTrue();
    }

    [Fact]
    public void ForPoint_TruncatesWeightsToFloatWithoutChangingTheirSum()
    {
        var plan = TapPlanBuilder.ForPoint(2.37, 10, Resampler.Bilinear);

        plan.Start.ShouldBe(1);
        plan.Count.ShouldBe(4);
        var sum = 0.0;
        foreach (var w in plan.Weights)
        {
            (w >= 0f).ShouldBeTrue();
            sum += w;
        }
        sum.ShouldBe(1.0, 1e-6);
    }
}
