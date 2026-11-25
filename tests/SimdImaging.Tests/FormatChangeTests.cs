using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// A step that changes the working format cannot be a same-format plan step: the executor's destination has the
/// source format. The compiler turns it into its own section, the Frame executor runs its whole-image form, and the
/// next section starts in the new format. Stream, Region and Tile refuse it with a named reason.
/// </summary>
public sealed class FormatChangeTests
{
    public static TheoryData<WorkingFormat> Formats => new()
    {
        WorkingFormat.Rgba8,
        WorkingFormat.Rgba16,
        WorkingFormat.Rgba32F,
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void DistanceTransform_ChangesFormat_AndMatchesTheDirectChain(WorkingFormat format)
    {
        using var image = TestImages.Gradient(64, 48, format);
        using var inverted = image.Pipe().Invert().ToImage();
        using var distances = DistanceTransformOps.SquaredEuclidean(inverted[0].Pixels.View, 0.5f);
        using var expected = PointOps.Invert(distances.View);

        using var actual = image.Pipe().Invert().DistanceTransform().Invert().ToImage();

        actual.Format.ShouldBe(WorkingFormat.Rgba32F);
        Rows(actual[0].Pixels.View).ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void ConvertColorSpace_ReportsRgba32F_AndMatchesTheDirectCall()
    {
        using var image = TestImages.Gradient(48, 40, WorkingFormat.Rgba8);
        using var expected = ColorConversionOps.Convert(image[0].Pixels.View, ColorSpace.Lab);
        using var actual = image.Pipe().ConvertColorSpace(ColorSpace.Lab).ToImage();

        actual.Format.ShouldBe(WorkingFormat.Rgba32F);
        Rows(actual[0].Pixels.View).ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void CompilerSplitsAtAFormatChange_AndRefusesEveryBackendThatCannotCarryIt()
    {
        var recipe = Recipe(PlanOps.Invert(), PlanOps.DistanceTransform(), PlanOps.Invert());
        var frame = Compile(recipe, StreamOptions());
        frame.Backend.ShouldBe(ExecutionBackend.Frame);
        frame.StreamRefusal.ShouldBe(StreamRefusal.FormatChange);
        frame.Sections.Select(section => section.Kind)
            .ShouldBe(new[] { CompiledSectionKind.OperatorRun, CompiledSectionKind.Format, CompiledSectionKind.OperatorRun });
        frame.Steps.Count.ShouldBe(2);
        frame.Workspace.Terms.ShouldContain(term => term.Kind == WorkspaceTermKind.FormatStep && term.Bytes > 0);

        var region = Compile(recipe, RegionOptions());
        region.RegionRefusal.ShouldBe(RegionRefusal.FormatChange);

        var tile = Compile(recipe, StreamOptions() with
        {
            TileRequested = true,
            Sink = SinkCapabilities.RandomRegionWrite,
        });
        tile.TileRefusal.ShouldBe(TileRefusal.FormatChange);
    }

    [Fact]
    public void ACompiledFormatSection_MatchesTheDirectChain()
    {
        var recipe = Recipe(PlanOps.Invert(), PlanOps.DistanceTransform(), PlanOps.Invert());
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var plan = Compile(recipe, StreamOptions());

        using var actual = CompiledPlanExecutor.ExecuteFrame(plan, image[0].Pixels.View);

        using var inverted = image.Pipe().Invert().ToImage();
        using var distances = DistanceTransformOps.SquaredEuclidean(inverted[0].Pixels.View, 0.5f);
        using var expected = PointOps.Invert(distances.View);

        actual.Format.ShouldBe(WorkingFormat.Rgba32F);
        Rows(actual.View).ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void ASameFormatPlan_RefusesTheStep()
    {
        Should.Throw<NotSupportedException>(() => new ProcessingPlan().Add(PlanOps.DistanceTransform()));
        Should.Throw<NotSupportedException>(() => new ProcessingPlan().Add(PlanOps.ConvertColorSpace(ColorSpace.Lab)));
    }

    [Fact]
    public void CompositeAfterAFormatChange_UsesTheNewFormat()
    {
        using var background = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        using var overlay = TestImages.Gradient(8, 8, WorkingFormat.Rgba32F);

        using var converted = background.Pipe().ConvertColorSpace(ColorSpace.Lab).ToImage();
        using var expected = AlphaOps.Composite(converted[0].Pixels.View, overlay[0].Pixels.View, 4, 3);
        using var actual = background.Pipe().ConvertColorSpace(ColorSpace.Lab).Composite(overlay, 4, 3).ToImage();

        actual.Format.ShouldBe(WorkingFormat.Rgba32F);
        Rows(actual[0].Pixels.View).ShouldBe(Rows(expected.View));
    }

    private static PipelineRecipe Recipe(params ImageOp[] steps)
    {
        var builder = new PipelineRecipeBuilder();
        foreach (var step in steps)
            builder.Append(step);
        return builder.Build();
    }

    private static CompiledExecutionPlan Compile(PipelineRecipe recipe, CompileOptions options) =>
        PipelineCompiler.Compile(recipe, options);

    private static CompileOptions StreamOptions() => new()
    {
        SourceWidth = 64,
        SourceHeight = 48,
        SourceFormat = WorkingFormat.Rgba8,
        Source = SourceCapabilities.ForwardOnly,
        Sink = SinkCapabilities.RowWritable,
    };

    private static CompileOptions RegionOptions() => new()
    {
        SourceWidth = 64,
        SourceHeight = 48,
        SourceFormat = WorkingFormat.Rgba8,
        Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion,
        Region = new Sdcb.SimdCodecs.Abstractions.PixelRect(8, 6, 32, 24),
    };

    private static byte[] Rows(PixelView view)
    {
        var bytes = new byte[view.Height * view.RowBytes];
        for (var y = 0; y < view.Height; y++)
            view.Row(y).CopyTo(bytes.AsSpan(y * view.RowBytes, view.RowBytes));
        return bytes;
    }
}
