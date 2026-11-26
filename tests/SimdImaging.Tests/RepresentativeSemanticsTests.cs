using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Contracts;

namespace SimdImaging.Tests;

/// <summary>
/// The eight representative operators from the architecture review, checked for the declarations an optimisation may
/// not silently change: alpha policy, pass, state, order and determinism. This is deliberately not a "set a flag"
/// test: the pixel-level pair below proves Pixelate really averages alpha, which is why its declaration is
/// <see cref="AlphaPolicy.Recomputes"/> rather than the default.
/// </summary>
public sealed class RepresentativeSemanticsTests
{
    [Fact]
    public void TheRepresentativeOps_DeclareTheirRealAlphaPolicy()
    {
        PlanOps.Invert().Contract.Semantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        PlanOps.GaussianBlur(1f).Contract.Semantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        PlanOps.Equalize().Contract.Semantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        PlanOps.Noise().Contract.Semantics.Alpha.ShouldBe(AlphaPolicy.Preserve);

        PlanOps.Pixelate(8).Contract.Semantics.Alpha.ShouldBe(AlphaPolicy.Recomputes);
        PlanOps.Composite(0, 0).Contract.Semantics.Alpha.ShouldBe(AlphaPolicy.Recomputes);
        PlanOps.Blend(0, 0, BlendMode.Multiply).Contract.Semantics.Alpha.ShouldBe(AlphaPolicy.Recomputes);
    }

    [Fact]
    public void Pixelate_ReallyAveragesAlpha()
    {
        using var image = Image.Create(4, 4, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var o = ((y * 4) + x) * 4;
                pixels[o] = 10;
                pixels[o + 1] = 20;
                pixels[o + 2] = 30;
                pixels[o + 3] = (byte)(((x + y) & 1) == 0 ? 0 : 255);
            }
        }

        using var result = image.Pipe().Pixelate(2).ToImage();

        // Each 2x2 block holds two transparent and two opaque samples, with the same colour, so the block's alpha is
        // the average. A binary edge would leave 0 or 255; the contract says this operator recomputes alpha.
        var alpha = result.WritablePixels[3];
        alpha.ShouldBeInRange((byte)100, (byte)155);
    }

    [Fact]
    public void TheStatefulAndMultiPassOps_DeclareWhatTheirPlannerNeeds()
    {
        var noise = PlanOps.Noise().Contract;
        noise.Requirements.State.ShouldBe(StateScope.PerCall);
        noise.Requirements.Order.ShouldBe(ExecutionOrder.SourceOrder);
        noise.Requirements.Determinism.ShouldBe(Determinism.SeededSequence);

        var equalize = PlanOps.Equalize().Contract;
        equalize.Pass.ShouldBe(PassModel.GatherThenApply);
        equalize.Requirements.State.ShouldBe(StateScope.PerImage);

        var pixelate = PlanOps.Pixelate(8).Contract;
        pixelate.Footprint.Kind.ShouldBe(InputFootprintKind.BlockAligned);
        pixelate.Requirements.Partition.ShouldBe(Partitioning.WholeImage);

        PlanOps.GaussianBlur(1f).Contract.Pass.ShouldBe(PassModel.SeparableRows);
    }
}
