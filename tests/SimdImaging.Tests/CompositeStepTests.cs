using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Geometry;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// Composition and blending are the first steps with two inputs. On Frame they run as plan steps with the overlay
/// captured by the kernel; Stream and Region have no second input and refuse them. These tests pin the bytes against
/// the direct calls and the two-input declaration.
/// </summary>
public sealed class CompositeStepTests
{
    public static TheoryData<WorkingFormat> Formats => new()
    {
        WorkingFormat.Rgba8,
        WorkingFormat.Rgba16,
        WorkingFormat.Rgba32F,
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void CompositeAsAPlanStep_MatchesTheDirectCall(WorkingFormat format)
    {
        using var background = TestImages.Gradient(64, 48, format);
        using var overlay = TestImages.Gradient(20, 16, format);

        using var expected = AlphaOps.Composite(background[0].Pixels.View, overlay[0].Pixels.View, 10, 8);
        using var actual = new ProcessingPlan()
            .Add(PlanOps.Composite(overlay[0].Pixels.View, 10, 8))
            .Execute(background[0].Pixels.View);

        Rows(actual.View).ShouldBe(Rows(expected.View));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void BlendAsAPlanStep_MatchesTheDirectCall(WorkingFormat format)
    {
        using var background = TestImages.Gradient(64, 48, format);
        using var overlay = TestImages.Gradient(20, 16, format);

        using var expected = Blend.Apply(background[0].Pixels.View, overlay[0].Pixels.View, 10, 8,
            BlendMode.Multiply);
        using var actual = new ProcessingPlan()
            .Add(PlanOps.Blend(overlay[0].Pixels.View, 10, 8, BlendMode.Multiply))
            .Execute(background[0].Pixels.View);

        Rows(actual.View).ShouldBe(Rows(expected.View));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void PipelineComposite_DefersAndMatchesTheEagerSequence(WorkingFormat format)
    {
        using var background = TestImages.Gradient(64, 48, format);
        using var overlay = TestImages.Gradient(20, 16, format);

        using var inverted = background.Pipe().Invert().ToImage();
        using var expected = AlphaOps.Composite(inverted[0].Pixels.View, overlay[0].Pixels.View, 10, 8);
        using var actual = background.Pipe().Invert().Composite(overlay, 10, 8).ToImage();

        Rows(actual[0].Pixels.View).ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void PipelineBlend_MatchesTheEagerSequence()
    {
        using var background = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        using var overlay = TestImages.Gradient(20, 16, WorkingFormat.Rgba8);

        using var inverted = background.Pipe().Contrast(1.1f).ToImage();
        using var expected = Blend.Apply(inverted[0].Pixels.View, overlay[0].Pixels.View, 4, 6, BlendMode.Screen);
        using var actual = background.Pipe().Contrast(1.1f).Blend(overlay, 4, 6, BlendMode.Screen).ToImage();

        Rows(actual[0].Pixels.View).ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void CompositeDeclaresTwoInputPorts_AndAWholeImagePartition()
    {
        using var overlay = TestImages.Gradient(8, 6, WorkingFormat.Rgba8);
        var contract = PlanOps.Composite(overlay[0].Pixels.View, 0, 0).Contract;

        contract.Category.ShouldBe(OpCategory.Composite);
        contract.Inputs.Count.ShouldBe(2);
        contract.Inputs[0].Role.ShouldBe(PortRole.Source);
        contract.Inputs[1].Role.ShouldBe(PortRole.Overlay);
        contract.Inputs[1].Formats.ShouldBe(WorkingFormatMask.Rgba8);
        contract.Requirements.Partition.ShouldBe(Partitioning.WholeImage);
        contract.Workspace.IsDeclared.ShouldBeTrue();
    }

    [Fact]
    public void AMismatchedOverlayFormat_IsRefusedAtTheCall()
    {
        using var background = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        using var overlay = TestImages.Gradient(8, 8, WorkingFormat.Rgba16);

        var error = Should.Throw<ArgumentException>(() => background.Pipe().Composite(overlay, 0, 0));
        error.Message.ShouldContain("overlay is Rgba16");
    }

    [Fact]
    public void ACompositeStep_IsObservedInACompiledFrame()
    {
        using var background = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        using var overlay = TestImages.Gradient(8, 8, WorkingFormat.Rgba8);

        var recipe = new PipelineRecipeBuilder()
            .Append(PlanOps.Invert())
            .Append(PlanOps.Composite(overlay[0].Pixels.View, 4, 4))
            .Build();
        var plan = PipelineCompiler.Compile(recipe, new CompileOptions
        {
            SourceWidth = 32,
            SourceHeight = 32,
            SourceFormat = WorkingFormat.Rgba8,
            ObserveSteps = true,
        });

        var seen = new List<string>();
        using var result = CompiledPlanExecutor.ExecuteFrame(plan, background[0].Pixels.View,
            observe: (_, name, _) => seen.Add(name));

        seen.ShouldBe(["Invert", "Composite"]);
        plan.Backend.ShouldBe(ExecutionBackend.Frame);
    }

    [Fact]
    public void GuidedFilterWithAGuide_MatchesTheDirectCall()
    {
        using var source = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        using var guide = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);

        using var expected = GuidedFilterOps.GuidedFilter(source[0].Pixels.View, guide[0].Pixels.View, 2);
        using var actual = source.Pipe().GuidedFilter(guide, 2).ToImage();

        Rows(actual[0].Pixels.View).ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void DrawImageAsAPlanStep_MatchesTheDirectChain()
    {
        using var background = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        using var overlay = TestImages.Gradient(12, 9, WorkingFormat.Rgba8);
        var rect = new PixelRect(10, 8, 20, 15);

        using var scaled = SimdImaging.Processing.Geometry.Canvas.Resize(overlay[0].Pixels.View, rect.Width,
            rect.Height, Resampler.Bilinear, ResizeMode.Stretch, Anchor.Centre, default);
        using var faded = PointOps.Opacity(scaled.View, 0.6f);
        using var expected = AlphaOps.Composite(background[0].Pixels.View, faded.View, rect.X, rect.Y);

        using var planned = new ProcessingPlan().Add(PlanOps.DrawImage(overlay[0].Pixels.View, rect, 0.6f))
            .Execute(background[0].Pixels.View);
        using var piped = background.Pipe().DrawImage(overlay, rect, 0.6f).ToImage();

        Rows(planned.View).ShouldBe(Rows(expected.View));
        Rows(piped[0].Pixels.View).ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void ApplyGainMap_ChangesFormat_AndMatchesTheDirectCall()
    {
        using var baseImage = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);
        using var gainMap = TestImages.Gradient(32, 32, WorkingFormat.Rgba8);

        using var expected = HdrOps.ApplyGainMap(baseImage[0].Pixels.View, gainMap[0].Pixels.View, 4f, 0.8f);
        using var actual = baseImage.Pipe().ApplyGainMap(gainMap, 4f, 0.8f).ToImage();

        actual.Format.ShouldBe(WorkingFormat.Rgba32F);
        Rows(actual[0].Pixels.View).ShouldBe(Rows(expected.View));
    }

    [Fact]
    public void Watermark_AppliesTheOpacityItWasGiven()
    {
        using var background = TestImages.Gradient(40, 32, WorkingFormat.Rgba8);
        using var mark = TestImages.Gradient(8, 6, WorkingFormat.Rgba8);

        const int x = 40 - 8 - 2;
        const int y = 32 - 6 - 2;
        using var faded = PointOps.Opacity(mark[0].Pixels.View, 0.5f);
        using var expected = AlphaOps.Composite(background[0].Pixels.View, faded.View, x, y);

        using var actual = background.Pipe()
            .Watermark(mark, WatermarkPosition.BottomRight, margin: 2, opacity: 0.5f).ToImage();

        Rows(actual[0].Pixels.View).ShouldBe(Rows(expected.View));
    }

    private static byte[] Rows(PixelView view)
    {
        var bytes = new byte[view.Height * view.RowBytes];
        for (var y = 0; y < view.Height; y++)
            view.Row(y).CopyTo(bytes.AsSpan(y * view.RowBytes, view.RowBytes));
        return bytes;
    }
}
