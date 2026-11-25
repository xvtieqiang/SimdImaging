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
        PassModel Pass = PassModel.Single);

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
            contract.Requirements.Partition.ShouldBe(
                expected.Footprint is InputFootprintKind.BlockAligned ? Partitioning.WholeImage
                : expected.Name is "noise" or "vignette" or "clahe" or "local-laplacian" or "normalize"
                    or "adaptive" or "dehaze" or "equalize" ? Partitioning.WholeImage
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

    private static WorkingFormatMask Mask(WorkingFormat format) => format switch
    {
        WorkingFormat.Rgba8 => WorkingFormatMask.Rgba8,
        WorkingFormat.Rgba16 => WorkingFormatMask.Rgba16,
        WorkingFormat.Rgba32F => WorkingFormatMask.Rgba32F,
        _ => WorkingFormatMask.None,
    };
}
