using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Geometry;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// The compiler derives each step's source regions backwards from the requested output rectangle. These tests pin
/// the mapping for a resize, a crop, a block-aligned step, a whole-source convolution and a two-input composite,
/// and the region-cost refusal.
/// </summary>
public sealed class RegionPlanningTests
{
    [Fact]
    public void BlurThenResize_MapsTheRegionBackwards()
    {
        var recipe = Recipe(PlanOps.GaussianBlur(1f),
            GeometrySteps.Resize(50, 50, Resampler.Bicubic));
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 100,
            SourceHeight = 100,
            SourceFormat = WorkingFormat.Rgba8,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion,
            Region = new PixelRect(10, 10, 10, 10),
        });

        plan.StepInfo.Count.ShouldBe(2);
        var blur = plan.StepInfo[0];
        blur.Name.ShouldBe("GaussianBlur");
        blur.SourceRegions[0].Region.ShouldBe(new PixelRect(15, 15, 30, 30));

        var resize = plan.StepInfo[1];
        resize.IsGeometry.ShouldBeTrue();
        resize.SourceRegions[0].Region.ShouldBe(new PixelRect(18, 18, 24, 24));

        plan.RegionCost.OutputArea.ShouldBe(100);
        plan.RegionCost.SourceArea.ShouldBe(900);
        plan.RegionCost.Amplification.ShouldBe(9.0, 1e-9);
    }

    [Fact]
    public void Crop_OffsetsTheRegion()
    {
        var recipe = Recipe(GeometrySteps.Crop(new PixelRect(8, 6, 64, 48)));
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 96,
            SourceHeight = 72,
            SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(5, 4, 10, 10),
        });

        plan.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(13, 10, 10, 10));
    }

    [Fact]
    public void Rotate90_MapsTheRegionToTheOtherAxis()
    {
        var recipe = Recipe(GeometrySteps.Rotate90(clockwise: true));
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 40,
            SourceHeight = 30,
            SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(5, 4, 10, 8),
        });

        plan.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(4, 15, 8, 10));
    }

    [Fact]
    public void Rotate180AndFlipVertical_MirrorBothOrOneAxis()
    {
        var rotate = Compile(Recipe(GeometrySteps.Rotate180()), new CompileOptions
        {
            SourceWidth = 40, SourceHeight = 30, SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(5, 4, 10, 8),
        });
        rotate.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(25, 18, 10, 8));

        var flip = Compile(Recipe(GeometrySteps.FlipVertical()), new CompileOptions
        {
            SourceWidth = 40, SourceHeight = 30, SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(5, 4, 10, 8),
        });
        flip.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(5, 18, 10, 8));
    }

    [Fact]
    public void Transpose_MapsTheRegionToTheOtherAxis()
    {
        var normal = Compile(Recipe(GeometrySteps.Transpose(anti: false)), new CompileOptions
        {
            SourceWidth = 40, SourceHeight = 30, SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(5, 4, 10, 8),
        });
        normal.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(4, 5, 8, 10));

        var anti = Compile(Recipe(GeometrySteps.Transpose(anti: true)), new CompileOptions
        {
            SourceWidth = 40, SourceHeight = 30, SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(5, 4, 10, 8),
        });
        anti.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(28, 15, 8, 10));
    }

    [Theory]
    [InlineData(0, false, 5, 4, 10, 8)]
    [InlineData(0, true, 25, 4, 10, 8)]
    [InlineData(1, false, 4, 15, 8, 10)]
    [InlineData(1, true, 4, 5, 8, 10)]
    [InlineData(2, false, 25, 18, 10, 8)]
    [InlineData(2, true, 5, 18, 10, 8)]
    [InlineData(3, false, 28, 5, 8, 10)]
    [InlineData(3, true, 28, 15, 8, 10)]
    public void RotateFlip_MapsEveryCombination(int turns, bool flip, int x, int y, int width, int height)
    {
        var plan = Compile(Recipe(GeometrySteps.RotateFlip(turns, flip)), new CompileOptions
        {
            SourceWidth = 40, SourceHeight = 30, SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(5, 4, 10, 8),
        });

        plan.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(x, y, width, height));
    }

    [Fact]
    public void Affine_MapsTheCornersAndWidensByTheKernelSupport()
    {
        var identity = Compile(Recipe(GeometrySteps.Affine(AffineMatrix.Identity, 40, 30, default)),
            new CompileOptions
            {
                SourceWidth = 40, SourceHeight = 30, SourceFormat = WorkingFormat.Rgba8,
                Region = new PixelRect(5, 4, 10, 8),
            });
        identity.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(4, 3, 12, 10));

        var shifted = Compile(Recipe(GeometrySteps.Affine(AffineMatrix.Translation(3, 2), 40, 30, default)),
            new CompileOptions
            {
                SourceWidth = 40, SourceHeight = 30, SourceFormat = WorkingFormat.Rgba8,
                Region = new PixelRect(5, 4, 10, 8),
            });
        shifted.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(1, 1, 12, 10));
    }

    [Fact]
    public void Rotate_QuarterTurn_StaysCloseToTheExactRotate90Mapping()
    {
        var plan = Compile(Recipe(GeometrySteps.Rotate(90f, default)), new CompileOptions
        {
            SourceWidth = 40, SourceHeight = 30, SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(5, 4, 10, 8),
        });

        // The exact image of the rectangle under Rotate90 is (4, 15, 8, 10); the general affine path adds the
        // bilinear support on each side plus the float error of the 90° matrix, so the region is a superset of
        // that and still far smaller than the 40x30 source.
        var region = plan.StepInfo[0].SourceRegions[0].Region;
        region.X.ShouldBeLessThanOrEqualTo(4);
        region.Y.ShouldBeLessThanOrEqualTo(15);
        (region.X + region.Width).ShouldBeGreaterThanOrEqualTo(12);
        (region.Y + region.Height).ShouldBeGreaterThanOrEqualTo(25);
        region.Width.ShouldBeLessThan(20);
        region.Height.ShouldBeLessThan(20);
    }

    [Fact]
    public void Pixelate_ReadsWholeBlocks()
    {
        var recipe = Recipe(PlanOps.Pixelate(8));
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 64,
            SourceHeight = 48,
            SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(9, 9, 2, 2),
        });

        plan.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(8, 8, 8, 8));
    }

    [Fact]
    public void AWholeSourceStep_ReadsTheWholeImage()
    {
        var recipe = Recipe(PlanOps.Convolve(new float[9], 1, BorderMode.Mirror));
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 64,
            SourceHeight = 48,
            SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(5, 5, 10, 10),
        });

        plan.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(0, 0, 64, 48));
    }

    [Fact]
    public void AComposite_ReportsBothPorts()
    {
        using var overlay = TestImages.Gradient(8, 6, WorkingFormat.Rgba8);
        var recipe = Recipe(PlanOps.Composite(overlay[0].Pixels.View, 2, 3));
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 32,
            SourceHeight = 32,
            SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(10, 10, 4, 4),
        });

        var regions = plan.StepInfo[0].SourceRegions;
        regions.ShouldContain(new PortRegion(0, new PixelRect(0, 0, 32, 32)));
        regions.ShouldContain(new PortRegion(1, new PixelRect(2, 3, 8, 6)));
    }

    [Fact]
    public void ARegionCostLimit_RefusesTheRegion()
    {
        var recipe = Recipe(PlanOps.GaussianBlur(1f));
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 64,
            SourceHeight = 64,
            SourceFormat = WorkingFormat.Rgba8,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion,
            Region = new PixelRect(10, 10, 4, 4),
            MaxRegionReadAmplification = 2.0,
        });

        plan.RegionRefusal.ShouldBe(RegionRefusal.RegionCost);
        plan.Backend.ShouldBe(ExecutionBackend.Frame);
    }

    [Fact]
    public void TilePlanning_UsesTheTileRectangle()
    {
        var recipe = Recipe(PlanOps.GaussianBlur(1f));
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 64,
            SourceHeight = 48,
            SourceFormat = WorkingFormat.Rgba8,
            Sink = SinkCapabilities.RandomRegionWrite,
            TileRequested = true,
            TileWidth = 16,
            TileHeight = 12,
        });

        plan.StepInfo[0].SourceRegions[0].Region.ShouldBe(new PixelRect(0, 0, 19, 15));
        plan.RegionCost.OutputArea.ShouldBe(16 * 12);
    }

    [Fact]
    public void TheExplanation_NamesTheSourceRegion()
    {
        var plan = Compile(Recipe(PlanOps.GaussianBlur(1f)), new CompileOptions
        {
            SourceWidth = 64,
            SourceHeight = 48,
            SourceFormat = WorkingFormat.Rgba8,
            Region = new PixelRect(10, 10, 8, 8),
        });

        plan.Explanation.ShouldContain(line => line.Contains("source port 0 7,7 14x14"));
    }

    [Fact]
    public void ThePublicExplanation_ReportsTheRegionAndAmplification()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.GaussianBlur(1f));

        var info = image.Explain(plan, new PixelRect(10, 10, 8, 8));

        info.Count.ShouldBe(1);
        info[0].Name.ShouldBe("GaussianBlur");
        info[0].SourceRegions[0].Region.ShouldBe(new PixelRect(7, 7, 14, 14));
        info[0].Amplification.ShouldBe(196.0 / 64.0, 1e-9);
    }

    private static PipelineRecipe Recipe(params object[] steps)
    {
        var builder = new PipelineRecipeBuilder();
        foreach (var step in steps)
        {
            if (step is ImageOp op)
                builder.Append(op);
            else
                builder.Append((GeometryDescriptor)step);
        }

        return builder.Build();
    }

    private static CompiledExecutionPlan Compile(PipelineRecipe recipe, CompileOptions options) =>
        PipelineCompiler.Compile(recipe, options);
}
