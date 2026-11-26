using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;
using SimdImaging.Processing.Contracts;

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

    [Theory]
    [InlineData(4, 2048)]
    [InlineData(300, 2400)]
    public void ResizeEstimate_IncludesVerticalTapScratch(int tapsY, long expectedTapScratch)
    {
        using var source = WorkingBuffer.Rent(64, 64, WorkingFormat.Rgba8);
        const int width = 32;
        const int height = 32;
        const int degree = 1;
        const long input = 64L * 64 * 4;
        const long output = width * (long)height * 4;
        const long stride = width * 4;
        var ringRows = Math.Min(64, tapsY + 2);
        var rowScratch = (64L * 4 + stride + stride * ringRows) * sizeof(float);

        var estimate = Resample.EstimateWorkingBytes(source.View, width, height, tapsY, nearest: false, degree);

        estimate.ShouldBe(input + output + rowScratch + expectedTapScratch);
    }

    [Fact]
    public void ResizeWorkspaceRefusal_HappensBeforeDestinationRent()
    {
        using var source = WorkingBuffer.Rent(64, 64, WorkingFormat.Rgba8);
        var pool = new SimdImaging.Processing.Fusion.CountingPool(ArrayPixelPool.Shared);
        var options = new ExecuteOptions { Workspace = new WorkspaceManager(limitBytes: 1) };

        Should.Throw<InvalidOperationException>(() =>
            Resample.Resize(source.View, 32, 32, Resampler.Bicubic, pool, options));

        pool.Rents.ShouldBe(0);
    }

    [Theory]
    [InlineData(3, 3, false)]
    [InlineData(16, 8, false)]
    [InlineData(16, 8, true)]
    public void SeparableFilterWorkspace_CoversActiveFloatScratch(int width, int height, bool sharpen)
    {
        using var source = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba32F);
        var op = sharpen ? PlanOps.Sharpen(sigma: 1f) : PlanOps.GaussianBlur(1f);
        var declared = op.Scratch!(width, height, height, WorkingFormat.Rgba32F);
        var previousDegree = RowParallel.DegreeOverride;
        try
        {
            RowParallel.DegreeOverride = 1;
            MemoryBudget.Trim();
            FloatScratch.ResetForTests();

            using var result = new ProcessingPlan { BandRows = height }.Add(op).Execute(source.View);

            declared.ShouldBeGreaterThanOrEqualTo(FloatScratch.PeakLiveBytes,
                $"declared {declared}, active FloatScratch peak {FloatScratch.PeakLiveBytes}");
        }
        finally
        {
            RowParallel.DegreeOverride = previousDegree;
            MemoryBudget.ResetForTests();
        }
    }

    [Theory]
    [InlineData(2, 160, 96, WorkingFormat.Rgba16)]
    [InlineData(4, 96, 64, WorkingFormat.Rgba32F)]
    public void MorphWorkspace_AccountsForLiveFramesAndWorkingFormat(
        int radius, int width, int rows, WorkingFormat format)
    {
        var frameBytes = (long)width * rows * format.BytesPerPixel();
        var primitive = PlanOps.Morph(radius).Scratch!(width, rows, rows, format);
        primitive.ShouldBeGreaterThanOrEqualTo(frameBytes * 4);

        var opening = PlanOps.Morph(radius, SimdImaging.Abstractions.Morphology.Open)
            .Scratch!(width, rows, rows, format);
        opening.ShouldBeGreaterThanOrEqualTo(frameBytes * 5);
    }

    [Fact]
    public void MorphTopHat_DifferencePeakCountsThreeWorkerRowsNotTwelve()
    {
        const int width = 8;
        const int rows = 1;
        const int radius = 2;
        const long frameBytes = (long)width * rows * WorkingFormat.Rgba16.BytesPerPixel();
        const int window = (radius * 2) + 1;
        const int paddedWidth = width + (radius * 2);
        const int blocks = ((paddedWidth + window - 1) / window) * window;
        var slideScratch = ((8L * width) + (12L * blocks)) * sizeof(float);
        var expectedOpeningPeak = (5L * frameBytes) + slideScratch;
        var expectedDifferencePeak = (2L * frameBytes) + (3L * width * 4 * sizeof(float));

        var declared = FilterOps.MorphPlanWorkspaceBytes(width, rows, WorkingFormat.Rgba16, radius,
            SimdImaging.Abstractions.Morphology.TopHat);

        declared.ShouldBe(Math.Max(expectedOpeningPeak, expectedDifferencePeak));
    }

    [Fact]
    public void MorphRadiusOne_AccountsForTheThreeRowFloatRing()
    {
        const int width = 48;
        const int rows = 24;
        const int radius = 1;
        var frameBytes = (long)width * rows * WorkingFormat.Rgba8.BytesPerPixel();
        var declared = PlanOps.Morph(radius).Scratch!(width, rows, rows, WorkingFormat.Rgba8);
        var perWorkerFloatBytes = 5L * width * 4 * sizeof(float);

        declared.ShouldBeGreaterThanOrEqualTo(frameBytes + perWorkerFloatBytes);
    }

    [Fact]
    public void GuidedByWorkspace_CoversTheCrossGuidedWorkerPlanes()
    {
        const int width = 96;
        const int height = 96;
        const int radius = 3;
        const int bandRows = 32;
        using var source = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8);
        using var guide = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8);
        var pool = new SimdImaging.Processing.Fusion.CountingPool(ArrayPixelPool.Shared);
        var op = PlanOps.GuidedBy(radius, bandRows: bandRows);
        var declared = op.Scratch!(width, height, height, WorkingFormat.Rgba8);
        var previousDegree = RowParallel.DegreeOverride;

        try
        {
            RowParallel.DegreeOverride = 1;
            MemoryBudget.Trim();
            FloatScratch.ResetForTests();

            using var result = GuidedFilterOps.GuidedFilter(source.View, guide.View, radius,
                bandRows: bandRows, pool: pool);

            declared.ShouldBeGreaterThanOrEqualTo(pool.PeakBytes + FloatScratch.PeakLiveBytes,
                $"declared {declared}, destination {pool.PeakBytes}, active scratch {FloatScratch.PeakLiveBytes}");
        }
        finally
        {
            RowParallel.DegreeOverride = previousDegree;
            MemoryBudget.ResetForTests();
        }
    }

    [Fact]
    public void DehazeWorkspace_IncludesItsReturnedFrame()
    {
        const int width = 96;
        const int height = 64;
        using var source = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba16);
        var pool = new SimdImaging.Processing.Fusion.CountingPool(ArrayPixelPool.Shared);
        var op = PlanOps.Dehaze(patchRadius: 2, refineRadius: 0);
        var declared = op.Scratch!(width, height, height, WorkingFormat.Rgba16);
        var previousDegree = RowParallel.DegreeOverride;

        try
        {
            RowParallel.DegreeOverride = 1;
            MemoryBudget.Trim();
            FloatScratch.ResetForTests();

            using var result = DehazeOps.Dehaze(source.View, patchRadius: 2, refineRadius: 0, pool: pool);

            declared.ShouldBeGreaterThanOrEqualTo(pool.PeakBytes + FloatScratch.PeakLiveBytes,
                $"declared {declared}, destination {pool.PeakBytes}, active scratch {FloatScratch.PeakLiveBytes}");
        }
        finally
        {
            RowParallel.DegreeOverride = previousDegree;
            MemoryBudget.ResetForTests();
        }
    }

    [Fact]
    public void LocalLaplacianWorkspace_IsFormatAwareAndIncludesTheRgba16LookupTable()
    {
        const int width = 64;
        const int height = 48;
        const int levels = 3;
        const int samples = 32;

        var rgba8 = LocalLaplacianOps.ScratchBytes(width, height, WorkingFormat.Rgba8, levels, samples);
        var rgba16 = LocalLaplacianOps.ScratchBytes(width, height, WorkingFormat.Rgba16, levels, samples);
        var expectedDifference =
            ((long)samples * (65536 - 256) * sizeof(float)) +
            ((long)width * height * (WorkingFormat.Rgba16.BytesPerPixel() -
                WorkingFormat.Rgba8.BytesPerPixel()));

        (rgba16 - rgba8).ShouldBe(expectedDifference);
        rgba16.ShouldBeGreaterThan(rgba8);
    }

    [Fact]
    public void LocalLaplacianWorkspace_CoversPyramidsAssignmentsCollapseAndOutput()
    {
        const int width = 64;
        const int height = 48;
        const int levels = 3;
        const int samples = 32;
        const long basePixels = (long)width * height;
        long allLevelPixels = 0;
        long laplacianPixels = 0;
        long sampleScratchPixels = 0;
        long collapseScratchPixels = 0;
        long collapseOutputPixels = 0;
        var widths = new[] { 64, 32, 16, 8 };
        var heights = new[] { 48, 24, 12, 6 };

        for (var level = 0; level <= levels; level++)
        {
            var pixels = (long)widths[level] * heights[level];
            allLevelPixels += pixels;
            if (level < levels)
                laplacianPixels += pixels;
            if (level < levels)
                sampleScratchPixels = Math.Max(sampleScratchPixels,
                    Math.Max((long)widths[level] * heights[level + 1], (long)widths[level + 1] * heights[level]));
            if (level < levels - 1)
            {
                collapseScratchPixels = Math.Max(collapseScratchPixels, (long)widths[level] * heights[level + 1]);
                collapseOutputPixels = Math.Max(collapseOutputPixels, pixels);
            }
        }

        var floatPixels = (3L * allLevelPixels) + (3L * laplacianPixels) + (6L * basePixels)
            + (3L * widths[1] * heights[1]) + sampleScratchPixels + collapseScratchPixels
            + collapseOutputPixels + (8L * width);
        var expected = (floatPixels * sizeof(float))
            + (basePixels * WorkingFormat.Rgba16.BytesPerPixel())
            + ((long)samples * 65536 * sizeof(float))
            + (3L * allLevelPixels)
            + (3L * (levels + 1) * sizeof(int));

        LocalLaplacianOps.ScratchBytes(width, height, WorkingFormat.Rgba16, levels, samples)
            .ShouldBe(expected);
    }

    [Fact]
    public void PointAdapterWorkspace_IncludesItsMaterialisedBandResult()
    {
        const int width = 80;
        const int rows = 24;
        const long resultBytes = (long)width * rows * WorkingFormat.Rgba32F.BytesPerPixel();
        var declared = PlanOps.Grayscale().Scratch!(width, rows, rows, WorkingFormat.Rgba32F);

        declared.ShouldBeGreaterThanOrEqualTo(resultBytes + (width * 4L * sizeof(float)));
    }

    [Fact]
    public void PointChainWorkspace_IncludesItsMaterialisedBandResult()
    {
        const int width = 80;
        const int rows = 24;
        var declared = PlanOps.PointChain(PointOps.InvertRow(), PointOps.BrightnessRow(0.1f))
            .Scratch!(width, rows, rows, WorkingFormat.Rgba8);

        declared.ShouldBe((long)width * rows * WorkingFormat.Rgba8.BytesPerPixel());
    }

    [Fact]
    public void GlobalWorkspace_IncludesTheResultBufferReturnedToItsAdapter()
    {
        const int width = 80;
        const int height = 40;
        var declared = PlanOps.Vignette().Scratch!(width, height, height, WorkingFormat.Rgba16);

        declared.ShouldBe((long)width * height * WorkingFormat.Rgba16.BytesPerPixel());
    }

    [Fact]
    public void MedianWorkspace_CoversTheSmallFloatRingAndReturnedImage()
    {
        const int width = 24;
        const int height = 12;
        const int radius = 8;
        using var source = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba32F);
        var pool = new SimdImaging.Processing.Fusion.CountingPool(ArrayPixelPool.Shared);
        var op = PlanOps.Median(radius);
        var declared = op.Scratch!(width, height, height, WorkingFormat.Rgba32F);
        var previousDegree = RowParallel.DegreeOverride;

        try
        {
            RowParallel.DegreeOverride = 1;
            MemoryBudget.Trim();
            FloatScratch.ResetForTests();

            using var result = FilterOps.Median(source.View, radius, pool);

            var loadMarkers = (2L * radius + 1) * sizeof(int);
            declared.ShouldBeGreaterThanOrEqualTo(pool.PeakBytes + FloatScratch.PeakLiveBytes + loadMarkers,
                $"declared {declared}, pooled output {pool.PeakBytes}, FloatScratch {FloatScratch.PeakLiveBytes}, markers {loadMarkers}");
        }
        finally
        {
            RowParallel.DegreeOverride = previousDegree;
            MemoryBudget.ResetForTests();
        }
    }

    [Fact]
    public void OilPaintWorkspace_CoversItsSmallWindowScratchAndResult()
    {
        const int width = 40;
        const int height = 16;
        const int radius = 5;
        const int levels = 32;
        using var source = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8);
        var pool = new SimdImaging.Processing.Fusion.CountingPool(ArrayPixelPool.Shared);
        var op = PlanOps.OilPaint(radius, levels);
        var declared = op.Scratch!(width, height, height, WorkingFormat.Rgba8);
        var previousDegree = RowParallel.DegreeOverride;

        try
        {
            RowParallel.DegreeOverride = 1;
            MemoryBudget.Trim();
            FloatScratch.ResetForTests();

            using var result = OilPaintOps.OilPaint(source.View, radius, levels, pool);

            var windowPixels = (long)(2 * radius + 1) * width;
            declared.ShouldBeGreaterThanOrEqualTo(pool.PeakBytes + FloatScratch.PeakLiveBytes
                + (windowPixels * sizeof(byte)) + (4L * levels * sizeof(int))
                + ((long)(2 * radius + 1) * sizeof(int)));
        }
        finally
        {
            RowParallel.DegreeOverride = previousDegree;
            MemoryBudget.ResetForTests();
        }
    }

    [Fact]
    public void WholeImageWorkerScratchUsesExecuteDegreeNotPlanBandHeight()
    {
        const int width = 256;
        const int height = 256;
        const int degree = 2;
        var op = PlanOps.Clahe(tilesX: 4, tilesY: 8);
        var plan = new ProcessingPlan { BandRows = height }.Add(op);
        var estimate = MemoryEstimator.Estimate(width, height, WorkingFormat.Rgba8, plan.Steps,
            bandRows: height, allowFusion: false, degree: degree);
        var fullBytes = (long)width * height * WorkingFormat.Rgba8.BytesPerPixel();
        var perWorker = op.Contract.Workspace.EstimatePerWorker(width, height, height, WorkingFormat.Rgba8);
        var wholeImage = op.Contract.Workspace.EstimateWholeImage(width, height, WorkingFormat.Rgba8);

        estimate.Workers.ShouldBe(degree);
        estimate.PerWorkerBytes.ShouldBe(perWorker);
        estimate.WholeImageScratchBytes.ShouldBe(wholeImage);
        estimate.PeakBytes.ShouldBe((2 * fullBytes) + (perWorker * degree) + wholeImage);
    }
    [Fact]
    public void SingleThreadContract_EstimatesFullImageScratchOnce()
    {
        const int width = 32;
        const int height = 64;
        var contract = OperatorContract.Create("single-thread-estimate", OpCategory.Neighborhood,
            WorkingFormatMask.All, IdentityFootprint.Instance,
            WorkspaceModel.Of((w, h, rows, format) => (long)w * rows * format.BytesPerPixel()),
            requirements: ExecutionRequirements.RowBands with { Threading = ThreadSafety.SingleThread });
        var op = new ImageOp(contract, new KernelImplementations((_, _, _, _) => { }));
        var plan = new ProcessingPlan { BandRows = 8 }.Add(op);
        var estimate = MemoryEstimator.Estimate(width, height, WorkingFormat.Rgba8, plan.Steps,
            bandRows: 8, allowFusion: false, degree: 4);
        var fullBytes = (long)width * height * WorkingFormat.Rgba8.BytesPerPixel();

        estimate.Workers.ShouldBe(1);
        estimate.PerWorkerBytes.ShouldBe(fullBytes);
        estimate.PeakBytes.ShouldBe((2 * fullBytes) + fullBytes);
    }
    [Fact]
    public void PixelViewEstimatorUsesTheSameFusionThresholdAsExecution()
    {
        using var source = WorkingBuffer.Rent(640, 360, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan { BandRows = 64 }
            .Add(PlanOps.GaussianBlur(1f))
            .Add(PlanOps.GaussianBlur(1f));

        var publicEstimate = MemoryEstimator.Estimate(source.View, plan, degree: 1);
        var stepwiseEstimate = MemoryEstimator.Estimate(source.Width, source.Height, source.Format, plan.Steps,
            plan.BandRows, allowFusion: false, degree: 1);

        publicEstimate.PlanScratchBytes.ShouldBe(stepwiseEstimate.PlanScratchBytes);
        publicEstimate.PeakBytes.ShouldBe(stepwiseEstimate.PeakBytes);
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
    public void Estimate_GlobalBoundaryCountsPreviousOwnedFrameDuringNextStep()
    {
        const int width = 256;
        const int height = 256;
        var plan = new ProcessingPlan { BandRows = height }
            .Add(PlanOps.Invert())
            .Add(PlanOps.Clahe(tilesX: 4, tilesY: 8));
        var estimate = MemoryEstimator.Estimate(width, height, WorkingFormat.Rgba8, plan.Steps,
            bandRows: height, allowFusion: true, degree: 2);
        var fullBytes = (long)width * height * WorkingFormat.Rgba8.BytesPerPixel();
        var operatorScratch = PlanOps.Clahe(tilesX: 4, tilesY: 8).Contract.Workspace;
        var perWorker = operatorScratch.EstimatePerWorker(width, height, height, WorkingFormat.Rgba8);
        var wholeImage = operatorScratch.EstimateWholeImage(width, height, WorkingFormat.Rgba8);

        estimate.PlanScratchBytes.ShouldBe(fullBytes);
        estimate.PeakBytes.ShouldBe((3 * fullBytes) + (perWorker * 2) + wholeImage);
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
    public void FusedWorkspaceCountsDownstreamHaloInEarlierOperatorAdapterWindows()
    {
        const int width = 64;
        const int height = 256;
        const int bandRows = 32;
        const int halo = 3; // GaussianBlur(1) derives ceil(3 * sigma).
        var first = PlanOps.GaussianBlur(1f);
        var second = PlanOps.GaussianBlur(1f);
        var third = PlanOps.GaussianBlur(1f);
        var plan = new ProcessingPlan { BandRows = bandRows }.Add(first).Add(second).Add(third);
        var estimate = MemoryEstimator.Estimate(width, height, WorkingFormat.Rgba8, plan.Steps,
            bandRows, allowFusion: true, degree: 1);
        var firstWindowRows = bandRows + (2 * halo * 3);
        var secondWindowRows = bandRows + (2 * halo * 2);
        var thirdWindowRows = bandRows + (2 * halo);
        var expectedPerWorker = Math.Max(
            first.Scratch!(width, height, firstWindowRows, WorkingFormat.Rgba8),
            Math.Max(second.Scratch!(width, height, secondWindowRows, WorkingFormat.Rgba8),
                third.Scratch!(width, height, thirdWindowRows, WorkingFormat.Rgba8)));

        estimate.PerWorkerBytes.ShouldBe(expectedPerWorker);
    }
    [Fact]
    public void Estimate_LocksTheFusedArithmeticForATwoPointPlan()
    {
        // Stepwise execution keeps the prior full-frame result alive while the next destination is being written.
        // Fusion instead keeps two bounded windows per worker. Both simultaneous lifetimes must be represented.
        var plan = new ProcessingPlan { BandRows = 64 }
            .Add("a", OpCategory.Point, PlanSteps.Point(v => PointOps.Invert(v)))
            .Add("b", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.2f)));

        var fused = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba8, plan.Steps,
            bandRows: 64, allowFusion: true, degree: 4);
        var unfused = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba8, plan.Steps,
            bandRows: 64, allowFusion: false, degree: 4);

        const long full = 1920L * 1080 * 4;
        const long window = 1920L * 64 * 4;
        const long workers = 4;

        unfused.PlanScratchBytes.ShouldBe(full);
        unfused.PeakBytes.ShouldBe(3 * full);

        fused.PlanScratchBytes.ShouldBe(2 * workers * window);
        fused.PeakBytes.ShouldBe((2 * full) + (2 * workers * window));
        fused.PeakBytes.ShouldBeLessThan(unfused.PeakBytes);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(20, 10)]
    public void Dehaze_DeclaresAtLeastWhatItRents(int refineRadius, int planes)
    {
        // The declaration is the only thing standing between MaxWorkingBytes and a lie, and nothing checked it
        // against the rentals. Dehaze's said three planes while its refinement held ten, so a 1080p plan under a
        // 64 MiB ceiling could pass the estimate and then use 58 MiB more than it promised.
        const int width = 160;
        const int height = 120;

        using var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var op = PlanOps.Dehaze(refineRadius: refineRadius);
        var declared = op.Scratch!(width, height, height, WorkingFormat.Rgba8);
        declared.ShouldBeGreaterThanOrEqualTo(planes * (long)width * height * sizeof(float));

        // One thread and one pool: FloatScratch is thread-local, so a worker's rent is invisible to this reading.
        var previousDegree = RowParallel.DegreeOverride;
        RowParallel.DegreeOverride = 1;
        try
        {
            MemoryBudget.Trim();
            FloatScratch.ResetForTests();
            using var result = new ProcessingPlan().Add(op).Execute(image[0].Pixels.View);

            FloatScratch.PeakLiveBytes.ShouldBeGreaterThanOrEqualTo(planes * (long)width * height * sizeof(float));
            declared.ShouldBeGreaterThanOrEqualTo(FloatScratch.PeakLiveBytes,
                $"declared {declared}, rented {FloatScratch.PeakLiveBytes}");
        }
        finally
        {
            RowParallel.DegreeOverride = previousDegree;
            MemoryBudget.ResetForTests();
        }
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
    public void Estimate_DefaultDegreeHonorsTheSameOverrideAsExecution()
    {
        var previous = RowParallel.DegreeOverride;
        try
        {
            RowParallel.DegreeOverride = 2;
            var estimate = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba8,
                [new PlanStep(PlanOps.Invert())], bandRows: 64, allowFusion: false, degree: 0);

            estimate.Workers.ShouldBe(2);
        }
        finally
        {
            RowParallel.DegreeOverride = previous;
        }
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
