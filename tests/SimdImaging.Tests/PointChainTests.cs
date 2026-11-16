using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// A collapsed chain is only worth having if it is the same function. The table is built by walking the same
/// transforms over the same 256 code values with the same rounding between them, so the test is not a tolerance:
/// it is byte equality against the operators run one after another.
/// </summary>
public class PointChainTests
{
    private static Image Pattern(WorkingFormat format = WorkingFormat.Rgba8, int width = 29, int height = 17)
    {
        var image = Image.Create(width, height, format);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)((i * 37) % 251);

        return image;
    }

    private static void RowsMatch(Image expected, WorkingBuffer actual, int width)
    {
        for (var y = 0; y < expected.Height; y++)
        {

            var bytesPerPixel = expected.Format.BytesPerPixel();
            actual.View.Row(y).ToArray().ShouldBe(
                expected.WritablePixels.Slice(y * width * bytesPerPixel, width * bytesPerPixel).ToArray(),
                $"row {y} differs");
        }
    }

    [Fact]
    public void SingleStep_MatchesTheOperator()
    {
        using var image = Pattern();
        using var sequential = image.Contrast(1.2f);
        using var collapsed = FilterOps.PointChain(image[0].Pixels.View, [PointOps.ContrastRow(1.2f)]);
        RowsMatch(sequential, collapsed, image.Width);
    }

    [Fact]
    public void TwoStepChain_MatchesTheSequentialOperators()
    {
        using var image = Pattern();
        using var sequential = image.Contrast(1.2f).Invert();

        using var collapsed = FilterOps.PointChain(image[0].Pixels.View,
            [PointOps.ContrastRow(1.2f), PointOps.InvertRow()]);

        RowsMatch(sequential, collapsed, image.Width);
    }

    /// <summary>Five steps: the case that was measured to be slower before the table was collapsed.</summary>
    [Fact]
    public void FiveStepChain_MatchesTheSequentialOperators()
    {
        using var image = Pattern();
        using var sequential = image.Contrast(1.2f).Invert().Contrast(0.9f).Brightness(0.05f).Invert();

        using var collapsed = FilterOps.PointChain(image[0].Pixels.View,
        [
            PointOps.ContrastRow(1.2f),
            PointOps.InvertRow(),
            PointOps.ContrastRow(0.9f),
            PointOps.BrightnessRow(0.05f),
            PointOps.InvertRow(),
        ]);

        RowsMatch(sequential, collapsed, image.Width);
    }

    /// <summary>Alpha is a channel and takes part: a chain that touches it must collapse the same way.</summary>
    [Fact]
    public void AlphaIsCarriedThrough()
    {
        using var image = Pattern();
        var span = image.WritablePixels;
        for (var i = 3; i < span.Length; i += 4)
            span[i] = (byte)(i % 251);

        var before = span.ToArray();
        using var collapsed = FilterOps.PointChain(image[0].Pixels.View, [PointOps.ContrastRow(1.2f)]);

        for (var i = 3; i < before.Length; i += 4)
            collapsed.View.Row(i / (image.Width * 4))[(i % (image.Width * 4))].ShouldBe(before[i]);
    }

    /// <summary>
    /// A channel-mixing transform cannot be collapsed into per-channel tables, and pretending otherwise would bake
    /// the grey axis and quietly return the wrong colour. It is refused instead.
    /// </summary>
    [Fact]
    public void ChannelMixingTransform_IsRefused()
    {
        using var image = Pattern();
        Should.Throw<NotSupportedException>(() => FilterOps.PointChain(image[0].Pixels.View,
            [PointOps.ContrastRow(1.2f), PointOps.SepiaRow()]));
    }

    /// <summary>A pre-built table must produce exactly what building one per call produces, and it must refuse a
    /// mixing transform at construction rather than at use.</summary>
    [Fact]
    public void PrebuiltTable_MatchesThePerCallChain()
    {
        using var first = Pattern();
        using var second = Pattern(width: 23, height: 11);

        var table = PointChainTable.Create(PointOps.ContrastRow(1.2f), PointOps.InvertRow());

        using var perCall = FilterOps.PointChain(first[0].Pixels.View,
            [PointOps.ContrastRow(1.2f), PointOps.InvertRow()]);
        using var reused = first.PointChain(table);

        RowsMatch(reused, perCall, first.Width);

        // The same table on a different image, to prove it carries no per-call state.
        using var other = second.PointChain(table);
        using var otherPerCall = FilterOps.PointChain(second[0].Pixels.View,
            [PointOps.ContrastRow(1.2f), PointOps.InvertRow()]);

        RowsMatch(other, otherPerCall, second.Width);
    }

    [Fact]
    public void PrebuiltTable_RefusesAMixingTransform()
    {
        Should.Throw<NotSupportedException>(() => PointChainTable.Create(PointOps.SepiaRow()));
    }

    /// <summary>The plan step collapses the same way as the direct call.</summary>
    [Fact]
    public void PlanStep_MatchesTheDirectCall()
    {
        using var image = Pattern();
        using var direct = FilterOps.PointChain(image[0].Pixels.View,
            [PointOps.ContrastRow(1.2f), PointOps.InvertRow()]);

        var plan = new ProcessingPlan { BandRows = 5 }
            .Add(PlanOps.PointChain(PointOps.ContrastRow(1.2f), PointOps.InvertRow()));

        using var banded = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { MaxDegreeOfParallelism = 3 });

        for (var y = 0; y < image.Height; y++)
            banded.View.Row(y).ToArray().ShouldBe(direct.View.Row(y).ToArray(), $"row {y} differs");
    }

    /// <summary>Wider formats keep the per-operator path; the table rule is 8-bit.</summary>
    [Fact]
    public void WiderFormats_AreRefused()
    {
        using var image = Pattern(WorkingFormat.Rgba16, width: 13, height: 9);
        Should.Throw<NotSupportedException>(() => FilterOps.PointChain(image[0].Pixels.View,
            [PointOps.ContrastRow(1.2f)]));
    }
}
