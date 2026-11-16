using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The estimate against reality. The measured side is <see cref="CountingPool"/>'s peak: it sits where the
/// executor rents its buffers, so it sees the plan-owned working set exactly, with no sampling and no GC
/// guesswork.
/// </summary>
public class MemoryEstimateTests : IDisposable
{
    public MemoryEstimateTests() => MemoryBudget.ResetForTests();

    public void Dispose() => MemoryBudget.ResetForTests();

    private static ProcessingPlan ThreeSteps(int bandRows = 64) => new ProcessingPlan { BandRows = bandRows }
        .Add("invert", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)))
        .Add("blur", OpCategory.Neighborhood, PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1.4f), 5, 5), 5, 5)
        .Add("gamma", OpCategory.Point, PlanSteps.Point(v => PointOps.Gamma(v, 1.8f)));

    [Theory]
    [InlineData(64, 48)]
    [InlineData(1920, 1080)]
    [InlineData(200, 1080)]
    public void Estimate_NeverReportsLessThanTheExecutorActuallyRents(int width, int height)
    {
        using var source = Image.Create(width, height, WorkingFormat.Rgba8);
        var plan = ThreeSteps();

        var estimate = MemoryEstimator.Estimate(source[0].Pixels.View, plan);

        // The test suite defines its own CountingPool in another file; this is the production one.
        var pool = new SimdImaging.Processing.Fusion.CountingPool(ArrayPixelPool.Shared);
        using var result = plan.Execute(source[0].Pixels.View, pool);

        // The pool never sees the source (it comes from the caller), so the comparable figure is the
        // output plus the plan's own scratch.
        pool.PeakBytes.ShouldBeLessThanOrEqualTo(estimate.OutputBytes + estimate.PlanScratchBytes,
            $"{width}x{height}: rented {pool.PeakBytes}, estimated {estimate.OutputBytes + estimate.PlanScratchBytes}");
    }

    [Fact]
    public void Estimate_CountsDeclaredOperatorScratch()
    {
        var plan = new ProcessingPlan()
            .Add("rl", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => RichardsonLucyOps.RichardsonLucy(v, 1.2f, 2), 4, 4), 4, 4,
                (w, _, rows, f) => RichardsonLucyOps.ScratchBytes(w, rows, f));

        var estimate = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba8, plan.Steps, degree: 4);

        estimate.HasEstimatedTerms.ShouldBeTrue();
        estimate.Workers.ShouldBe(4);

        // A single neighbourhood step is still banded: ProcessingPlan.ExecuteWithOptions only checks the category,
        // never the step count. The estimator used to assume otherwise and charge the operator for the whole
        // image, which over-reports by the band ratio.
        var bandRows = ProcessingPlan.DefaultBandRows;
        var rows = Math.Min(1080, bandRows + (2 * 4));
        estimate.PerWorkerBytes.ShouldBe(RichardsonLucyOps.ScratchBytes(1920, rows, WorkingFormat.Rgba8));

        estimate.PeakBytes.ShouldBe(estimate.OutputBytes * 2 + (estimate.PerWorkerBytes * 4));
        estimate.UndeclaredSteps.ShouldBe(0);
        estimate.HasEstimatedTerms.ShouldBeTrue();
    }

    /// <summary>
    /// Every factory <see cref="PlanOps"/> exposes declares its scratch, with one named exception. Without this,
    /// a factory added without a declaration silently reads as "free" to the budget, which is how the whole
    /// scratch term went missing in the first place.
    /// </summary>
    [Fact]
    public void EveryPlanOpsFactory_DeclaresScratch_ExceptLocalLaplacianByName()
    {
        var ops = new (string Name, ImageOp Op)[]
        {
            ("brightness", PlanOps.Brightness(0.1f)),
            ("contrast", PlanOps.Contrast(0.1f)),
            ("gaussian", PlanOps.GaussianBlur(1f)),
            ("box", PlanOps.BoxBlur(2)),
            ("sharpen", PlanOps.Sharpen()),
            ("median", PlanOps.Median3x3()),
            ("sobel", PlanOps.Sobel()),
            ("emboss", PlanOps.Emboss()),
            ("open", PlanOps.Open3x3()),
            ("bilateral", PlanOps.Bilateral(2)),
            ("motion", PlanOps.MotionBlur(5)),
            ("bokeh", PlanOps.BokehBlur(3)),
            ("oilpaint", PlanOps.OilPaint(2)),
            ("guided", PlanOps.GuidedFilter(3)),
            ("nlm", PlanOps.NonLocalMeans(2)),
            ("dehaze", PlanOps.Dehaze()),
            ("clahe", PlanOps.Clahe()),
            ("equalize", PlanOps.Equalize()),
            ("normalize", PlanOps.Normalize()),
            ("adaptive", PlanOps.AdaptiveThreshold()),
            ("pixelate", PlanOps.Pixelate(5)),
            ("vignette", PlanOps.Vignette()),
            ("local-laplacian", PlanOps.LocalLaplacian()),
        };

        var undeclared = ops.Where(x => x.Op.Scratch is null).Select(x => x.Name).ToArray();

        // No exceptions. LocalLaplacian used to be the one factory without a declaration - its pyramid footprint
        // depends on level count and sample grid - which meant a plan containing it could never be given a
        // MaxWorkingBytes ceiling. It now derives the number from the same level clamp the operator uses.
        undeclared.ShouldBeEmpty("every factory must declare its scratch");
    }

    [Fact]
    public void Estimate_WithoutDeclarationsSaysSoInsteadOfGuessing()
    {
        var plan = ThreeSteps();
        var estimate = MemoryEstimator.Estimate(320, 240, WorkingFormat.Rgba8, plan.Steps);

        estimate.HasEstimatedTerms.ShouldBeFalse();
        estimate.PerWorkerBytes.ShouldBe(0);

        // Without any declaration the peak is the source, the destination and whatever band windows the
        // fusion pass added — never less than the two images.
        estimate.PeakBytes.ShouldBeGreaterThanOrEqualTo(estimate.OutputBytes * 2);
        estimate.PeakBytes.ShouldBe(estimate.BufferBytes);
    }

    [Fact]
    public void Estimate_ReportsBandWindowsWhenTheHaloIsTallerThanTheBand()
    {
        // Six steps of ±64 rows stack into a 640-row window on a 64-row band: the executor then holds more
        // than two images, which is exactly what the working-set ceiling exists to catch.
        var plan = new ProcessingPlan { BandRows = 64 };
        for (var i = 0; i < 6; i++)
        {
            plan.Add($"blur{i}", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1.1f), 64, 64), 64, 64);
        }

        var fused = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba8, plan.Steps, allowFusion: true, degree: 4);
        var unfused = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba8, plan.Steps, allowFusion: false, degree: 4);

        fused.PlanScratchBytes.ShouldBeGreaterThan(0);
        fused.PlanScratchBytes.ShouldBeGreaterThan(unfused.PlanScratchBytes);
        fused.PeakBytes.ShouldBeGreaterThan(unfused.PeakBytes);
    }

    [Fact]
    public void Estimate_LocksTheFusedArithmeticForATwoPointPlan()
    {
        // Fusion is a speed trade, not a saving: two band windows per worker of a 64-row band cost more
        // than the single full intermediate the step-by-step path holds. The model has to say so, or a
        // working-set ceiling would let exactly the plans that need headroom through.
        var plan = new ProcessingPlan { BandRows = 64 }
            .Add("a", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)))
            .Add("b", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.2f)));

        var fused = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba8, plan.Steps, bandRows: 64, allowFusion: true, degree: 4);
        var unfused = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba8, plan.Steps, bandRows: 64, allowFusion: false, degree: 4);

        const long full = 1920L * 1080 * 4;          // 8_294_400
        const long window = 1920L * 64 * 4;          // 491_520, one band
        const long workers = 4;

        unfused.PlanScratchBytes.ShouldBe(0);
        unfused.PeakBytes.ShouldBe(2 * full);

        fused.PlanScratchBytes.ShouldBe(2 * workers * window);
        fused.PeakBytes.ShouldBe(full + full + 2 * workers * window);
        fused.PeakBytes.ShouldBeGreaterThan(unfused.PeakBytes);
    }

    [Fact]
    public void Estimate_WorkerCountFollowsTheBandRule()
    {
        // 1080 rows in 128-row bands is 9 bands, so nine workers can be useful however many cores exist.
        var plan = ThreeSteps(bandRows: 128);
        MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba16, plan.Steps, degree: 32)
            .Workers.ShouldBe(9);

        MemoryEstimator.Estimate(1920, 300, WorkingFormat.Rgba16, plan.Steps, bandRows: 128, degree: 32)
            .Workers.ShouldBe(3);

        MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba16, plan.Steps, degree: 2)
            .Workers.ShouldBe(2);
    }

    [Fact]
    public void Estimate_OverflowsLoudlyRatherThanWrapping()
    {
        Should.Throw<OverflowException>(() =>
            MemoryEstimator.Estimate(int.MaxValue, int.MaxValue, WorkingFormat.Rgba32F, []));
    }

    [Fact]
    public void Estimate_RejectsNonPositiveGeometry()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MemoryEstimator.Estimate(0, 10, WorkingFormat.Rgba8, []));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MemoryEstimator.Estimate(10, 10, WorkingFormat.Rgba8, [], bandRows: 0));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MemoryEstimator.Estimate(10, 10, WorkingFormat.Rgba8, [], degree: -1));
    }
}
