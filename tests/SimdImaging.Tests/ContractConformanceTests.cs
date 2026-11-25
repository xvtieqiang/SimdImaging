using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Contracts;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// The contract is only worth having if it is true. This walks every factory <see cref="PlanOps"/> exposes and
/// checks the declaration against what the operator actually does: the footprint kind, the halo, the declared
/// formats (by running them), the workspace, and the few per-operator requirements that a band executor must know.
/// </summary>
public sealed class ContractConformanceTests
{
    private sealed record Expected(
        string Name,
        Func<ImageOp> Factory,
        WorkingFormatMask Formats,
        InputFootprintKind Footprint,
        int Halo = 0,
        PassModel Pass = PassModel.Single,
        bool SameFormat = true);

    private static readonly Expected[] Ops =
    [
        // Point: one input pixel, no halo.
        new("brightness", () => PlanOps.Brightness(0.1f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("contrast", () => PlanOps.Contrast(1.1f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("gamma", () => PlanOps.Gamma(1.2f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("invert", () => PlanOps.Invert(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("grayscale", () => PlanOps.Grayscale(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("sepia", () => PlanOps.Sepia(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("saturation", () => PlanOps.Saturation(1.2f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("posterize", () => PlanOps.Posterize(8), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("levels", () => PlanOps.Levels(0f, 1f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("point-chain-table", () => PlanOps.PointChain(PointChainTable.Create(PointOps.InvertRow())),
            WorkingFormatMask.Rgba8, InputFootprintKind.Identity),
        new("point-chain", () => PlanOps.PointChain(PointOps.InvertRow()),
            WorkingFormatMask.Rgba8, InputFootprintKind.Identity),
        new("temperature", () => PlanOps.Temperature(0.1f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("curves", () => PlanOps.Curves([new CurvePoint(0f, 0f), new CurvePoint(1f, 1f)]),
            WorkingFormatMask.All, InputFootprintKind.Identity),
        new("vibrance", () => PlanOps.Vibrance(1.2f), WorkingFormatMask.All, InputFootprintKind.Identity),

        // Point operators migrated in P3 batch 1: colour, alpha and transfer curves.
        new("premultiply", () => PlanOps.Premultiply(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("unpremultiply", () => PlanOps.Unpremultiply(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("color-matrix", () => PlanOps.ColorMatrix([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f]),
            WorkingFormatMask.All, InputFootprintKind.Identity),
        new("hue-rotate", () => PlanOps.HueRotate(30f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("alpha-threshold", () => PlanOps.AlphaThreshold(0.5f), WorkingFormatMask.All,
            InputFootprintKind.Identity),
        new("tint", () => PlanOps.Tint(new RgbaColor(200, 150, 90, 255), 0.4f), WorkingFormatMask.All,
            InputFootprintKind.Identity),
        new("color-balance", () => PlanOps.ColorBalance(new ColorBalanceShifts(0.1f, 0f, -0.1f),
            new ColorBalanceShifts(0f, 0.05f, 0f), new ColorBalanceShifts(-0.05f, 0f, 0.05f)),
            WorkingFormatMask.All, InputFootprintKind.Identity),
        new("channel-swap", () => PlanOps.ChannelSwap(2, 0, 1), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("black-white", () => PlanOps.BlackWhite(0.5f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("clamp", () => PlanOps.Clamp(0.1f, 0.9f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("opacity", () => PlanOps.Opacity(0.6f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("lightness", () => PlanOps.Lightness(1.2f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("threshold", () => PlanOps.Threshold(0.5f), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("linearize", () => PlanOps.Linearize(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("delinearize", () => PlanOps.Delinearize(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("color-blindness", () => PlanOps.ColorBlindness(severity: 0.8f), WorkingFormatMask.All,
            InputFootprintKind.Identity),

        // Neighbourhood: a constant halo, except the general convolution, which also needs the whole image for
        // its Mirror/Wrap borders while still widening the band by its radius.
        new("gaussian", () => PlanOps.GaussianBlur(1f), WorkingFormatMask.All, InputFootprintKind.Halo, 3,
            PassModel.SeparableRows),
        new("box", () => PlanOps.BoxBlur(2), WorkingFormatMask.All, InputFootprintKind.Halo, 2,
            PassModel.SeparableRows),
        new("sharpen", () => PlanOps.Sharpen(), WorkingFormatMask.All, InputFootprintKind.Halo, 3,
            PassModel.SeparableRows),
        new("emboss", () => PlanOps.Emboss(), WorkingFormatMask.All, InputFootprintKind.Halo, 1),
        new("sobel", () => PlanOps.Sobel(), WorkingFormatMask.All, InputFootprintKind.Halo, 1),
        new("median3x3", () => PlanOps.Median3x3(), WorkingFormatMask.All, InputFootprintKind.Halo, 1),
        new("median", () => PlanOps.Median(2), WorkingFormatMask.All, InputFootprintKind.Halo, 2),
        new("convolve", () => PlanOps.Convolve(new float[9], 1), WorkingFormatMask.All, InputFootprintKind.WholeSource, 1),
        new("richardson-lucy", () => PlanOps.RichardsonLucy(1f, 2), WorkingFormatMask.All,
            InputFootprintKind.Halo, 12),
        new("richardson-lucy-motion", () => PlanOps.RichardsonLucyMotion(7, 30f, 2), WorkingFormatMask.All,
            InputFootprintKind.Halo, 12),
        new("prewitt", () => PlanOps.Prewitt(), WorkingFormatMask.All, InputFootprintKind.Halo, 1),
        new("scharr", () => PlanOps.Scharr(), WorkingFormatMask.All, InputFootprintKind.Halo, 1),
        new("laplacian", () => PlanOps.Laplacian(), WorkingFormatMask.All, InputFootprintKind.Halo, 1),
        new("convolve3x3", () => PlanOps.Convolve3x3(new float[9]), WorkingFormatMask.All,
            InputFootprintKind.Halo, 1),
        new("morph", () => PlanOps.Morph(2), WorkingFormatMask.All, InputFootprintKind.Halo, 2),
        new("gaussian-sharpen", () => PlanOps.GaussianSharpen(), WorkingFormatMask.All, InputFootprintKind.Halo, 3,
            PassModel.SeparableRows),
        new("erode", () => PlanOps.Erode3x3(), WorkingFormatMask.All, InputFootprintKind.Halo, 1),
        new("dilate", () => PlanOps.Dilate3x3(), WorkingFormatMask.All, InputFootprintKind.Halo, 1),
        new("open", () => PlanOps.Open3x3(), WorkingFormatMask.All, InputFootprintKind.Halo, 2),
        new("close", () => PlanOps.Close3x3(), WorkingFormatMask.All, InputFootprintKind.Halo, 2),
        new("bilateral", () => PlanOps.Bilateral(2), WorkingFormatMask.All, InputFootprintKind.Halo, 2),
        new("motion", () => PlanOps.MotionBlur(5), WorkingFormatMask.All, InputFootprintKind.Halo, 3),
        new("bokeh", () => PlanOps.BokehBlur(2), WorkingFormatMask.All, InputFootprintKind.Halo, 2),
        new("guided", () => PlanOps.GuidedFilter(2), WorkingFormatMask.All, InputFootprintKind.Halo, 2),
        new("nlm", () => PlanOps.NonLocalMeans(2), WorkingFormatMask.All, InputFootprintKind.Halo, 3),
        new("oilpaint", () => PlanOps.OilPaint(2), WorkingFormatMask.All, InputFootprintKind.Halo, 2),

        // Whole-image steps: the ones that read the whole picture declare it; the position-local ones say so.
        new("distance-transform", () => PlanOps.DistanceTransform(), WorkingFormatMask.All,
            InputFootprintKind.WholeSource, Pass: PassModel.GatherThenApply, SameFormat: false),
        new("convert-color-space", () => PlanOps.ConvertColorSpace(ColorSpace.Lab), WorkingFormatMask.All,
            InputFootprintKind.Identity, SameFormat: false),
        new("tone-map", () => PlanOps.ToneMap(), WorkingFormatMask.All, InputFootprintKind.WholeSource),
        new("dither", () => PlanOps.Dither(4), WorkingFormatMask.All, InputFootprintKind.WholeSource),
        new("quantize", () => PlanOps.Quantize(16), WorkingFormatMask.All, InputFootprintKind.WholeSource,
            Pass: PassModel.GatherThenApply),
        new("reduce-colors", () => PlanOps.ReduceColors(16), WorkingFormatMask.All,
            InputFootprintKind.WholeSource, Pass: PassModel.GatherThenApply),
        new("dither-to-palette", () => PlanOps.DitherToPalette(PaletteQuantizer.WebSafe), WorkingFormatMask.All,
            InputFootprintKind.WholeSource),
        new("map-to-palette", () => PlanOps.MapToPalette(PaletteQuantizer.WebSafe), WorkingFormatMask.All,
            InputFootprintKind.Identity),
        new("quantize-websafe", () => PlanOps.QuantizeWebSafe(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("noise", () => PlanOps.Noise(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("vignette", () => PlanOps.Vignette(), WorkingFormatMask.All, InputFootprintKind.Identity),
        new("clahe", () => PlanOps.Clahe(), WorkingFormatMask.All, InputFootprintKind.WholeSource,
            Pass: PassModel.GatherThenApply),
        new("local-laplacian", () => PlanOps.LocalLaplacian(levels: 3, samples: 8), WorkingFormatMask.All,
            InputFootprintKind.WholeSource, Pass: PassModel.GatherThenApply),
        new("normalize", () => PlanOps.Normalize(), WorkingFormatMask.All, InputFootprintKind.WholeSource,
            Pass: PassModel.GatherThenApply),
        new("adaptive", () => PlanOps.AdaptiveThreshold(), WorkingFormatMask.All, InputFootprintKind.WholeSource,
            Pass: PassModel.GatherThenApply),
        new("pixelate", () => PlanOps.Pixelate(8), WorkingFormatMask.All, InputFootprintKind.BlockAligned),
        new("dehaze", () => PlanOps.Dehaze(patchRadius: 2, refineRadius: 2), WorkingFormatMask.All,
            InputFootprintKind.WholeSource, Pass: PassModel.GatherThenApply),
        new("equalize", () => PlanOps.Equalize(), WorkingFormatMask.All, InputFootprintKind.WholeSource,
            Pass: PassModel.GatherThenApply),
    ];

    [Fact]
    public void EveryFactory_DeclaresACompleteContract()
    {
        foreach (var expected in Ops)
        {
            var op = expected.Factory();
            var contract = op.Contract;

            contract.Name.ShouldBe(op.Name);
            contract.Name.ShouldNotBeNullOrEmpty();
            contract.Formats.ShouldBe(expected.Formats, $"{expected.Name}: declared formats");
            contract.Footprint.Kind.ShouldBe(expected.Footprint, $"{expected.Name}: footprint kind");
            contract.HaloX.ShouldBe(expected.Halo, $"{expected.Name}: halo X");
            contract.HaloY.ShouldBe(expected.Halo, $"{expected.Name}: halo Y");
            contract.Pass.ShouldBe(expected.Pass, $"{expected.Name}: pass model");
            contract.Workspace.IsDeclared.ShouldBeTrue(
                $"{expected.Name}: an undeclared workspace makes a ceiling a lower bound");
            contract.Inputs.Count.ShouldBeGreaterThan(0, $"{expected.Name}: needs an input port");
            contract.Inputs[0].Formats.ShouldBe(expected.Formats, $"{expected.Name}: input port formats");
            contract.Output.SameSize.ShouldBeTrue($"{expected.Name}: plan steps are same-size");
            contract.Output.SameFormat.ShouldBe(expected.SameFormat, $"{expected.Name}: output format rule");
            contract.Requirements.Partition.ShouldBe(
                contract.Category is OpCategory.Global or OpCategory.Composite
                    || expected.Footprint is InputFootprintKind.BlockAligned
                    ? Partitioning.WholeImage
                    : Partitioning.RowBands,
                $"{expected.Name}: partitioning");
        }
    }

    [Fact]
    public void EveryDeclaredFormat_ActuallyRuns()
    {
        foreach (var expected in Ops)
        {
            foreach (var format in new[] { WorkingFormat.Rgba8, WorkingFormat.Rgba16, WorkingFormat.Rgba32F })
            {
                if ((expected.Formats & Mask(format)) == 0)
                    continue;

                using var image = TestImages.Gradient(64, 48, format);
                if (!expected.SameFormat)
                {
                    using var direct = expected.Factory().Direct!(image[0].Pixels.View);
                    direct.Width.ShouldBe(64, $"{expected.Name}/{format}: width");
                    direct.Height.ShouldBe(48, $"{expected.Name}/{format}: height");
                    direct.Format.ShouldBe(WorkingFormat.Rgba32F, $"{expected.Name}/{format}: output format");
                    continue;
                }

                using var result = new ProcessingPlan().Add(expected.Factory()).Execute(image[0].Pixels.View);
                result.Width.ShouldBe(64, $"{expected.Name}/{format}: width");
                result.Height.ShouldBe(48, $"{expected.Name}/{format}: height");
                result.Format.ShouldBe(format, $"{expected.Name}/{format}: format");
            }
        }
    }

    [Fact]
    public void Pixelate_DeclaresABlockGrid_NotAHalo()
    {
        var contract = PlanOps.Pixelate(8).Contract;

        contract.Footprint.Kind.ShouldBe(InputFootprintKind.BlockAligned);
        contract.Footprint.IsBlockAligned.ShouldBeTrue();
        contract.HaloX.ShouldBe(0);
        contract.HaloY.ShouldBe(0);
        contract.Requirements.Partition.ShouldBe(Partitioning.WholeImage);

        // A block grid answers with the block, not with a constant radius around the request.
        var mapped = contract.Footprint.MapSourceRect(new PixelRect(9, 9, 2, 2), 64, 48);
        mapped.ShouldBe(new PixelRect(8, 8, 8, 8));
    }

    [Fact]
    public void Convolve_DeclaresTheWholeSourceAndTheHaloItStillNeeds()
    {
        var contract = PlanOps.Convolve(new float[25], 2).Contract;

        contract.Footprint.Kind.ShouldBe(InputFootprintKind.WholeSource);
        contract.Footprint.CoversWholeImage.ShouldBeTrue();
        contract.HaloX.ShouldBe(2);
        contract.HaloY.ShouldBe(2);
    }

    [Fact]
    public void Noise_IsASeededSequence_NotCoordinateDeterministic()
    {
        var requirements = PlanOps.Noise().Contract.Requirements;

        requirements.State.ShouldBe(StateScope.PerCall);
        requirements.Order.ShouldBe(ExecutionOrder.SourceOrder);
        requirements.Determinism.ShouldBe(Determinism.SeededSequence);
    }

    [Fact]
    public void PlanStep_ReadsTheDefinitionInsteadOfCopyingIt()
    {
        var op = PlanOps.GaussianBlur(1.2f);
        var plan = new ProcessingPlan().Add(op);
        var step = plan.Steps[0];

        step.Definition.ShouldBeSameAs(op);
        step.Name.ShouldBe(op.Name);
        step.HaloX.ShouldBe(op.HaloX);
        step.Kernel.ShouldBeSameAs(op.Kernel);
        step.Scratch.ShouldBeSameAs(op.Scratch);
    }

    [Fact]
    public void EveryOperatorWithAWholeImageForm_MatchesThroughAPlan()
    {
        foreach (var expected in Ops)
        {
            var op = expected.Factory();
            if (!expected.SameFormat || op.Direct is not { } direct)
                continue;

            using var image = TestImages.Gradient(40, 32, WorkingFormat.Rgba8);
            using var planned = new ProcessingPlan().Add(op).Execute(image[0].Pixels.View);
            using var whole = direct(image[0].Pixels.View);

            Rows(planned.View).ShouldBe(Rows(whole.View), $"{expected.Name}: plan vs whole-image form");
        }
    }

    private static byte[] Rows(PixelView view)
    {
        var bytes = new byte[view.Height * view.RowBytes];
        for (var y = 0; y < view.Height; y++)
            view.Row(y).CopyTo(bytes.AsSpan(y * view.RowBytes, view.RowBytes));
        return bytes;
    }

    private static WorkingFormatMask Mask(WorkingFormat format) => format switch
    {
        WorkingFormat.Rgba8 => WorkingFormatMask.Rgba8,
        WorkingFormat.Rgba16 => WorkingFormatMask.Rgba16,
        WorkingFormat.Rgba32F => WorkingFormatMask.Rgba32F,
        _ => WorkingFormatMask.None,
    };
}
