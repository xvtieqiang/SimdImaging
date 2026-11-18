using SimdImaging.Processing;
using SimdImaging.Abstractions;

namespace SimdImaging.Tests;

/// <summary>
/// Operators that can write straight into the executor's band must produce the same bytes as the whole-image form.
/// </summary>
/// <remarks>
/// The single-operator path takes the direct delegate (one whole-image call), so nothing else in the suite reaches
/// the destination-writing branch. A two-step chain does: it fuses, and the band adapter then calls the operator's
/// "write these rows here" form with a window that carries a halo row above and below the rows being produced. The
/// off-by-one that a halo invites is exactly what these comparisons exist to catch.
/// </remarks>
public class DestinationKernelTests
{
    [Theory]
    [InlineData("median")]
    [InlineData("erode")]
    [InlineData("dilate")]
    [InlineData("emboss")]
    [InlineData("sobel")]
    [InlineData("sharpen")]
    public void FusedPair_MatchesTheSequentialOperators(string op)
    {
        using var source = Ramp(37, 29);

        using var sequential = Apply(Apply(source, op), "invert");
        using var fused = ApplyThenInvert(source, op);

        fused.WritablePixels.SequenceEqual(sequential.WritablePixels)
            .ShouldBeTrue($"{op} then invert must match byte for byte: fused writes into a halo window, " +
                "the sequential path writes a whole image");
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(5, 1)]
    [InlineData(4, 4)]
    public void FusedPair_MatchesAcrossBandBoundaries(int bandRows, int degree)
    {
        using var source = Ramp(23, 19);
        var options = new ExecuteOptions { MaxDegreeOfParallelism = degree, ForceFusion = true };

        using var sequential = Apply(Apply(source, "median"), "emboss");
        var plan = new ProcessingPlan { BandRows = bandRows }
            .Add(PlanOps.Median3x3())
            .Add(PlanOps.Emboss(0.6f));
        using var fused = source.ApplyWithOptions(plan, options);

        fused.WritablePixels.SequenceEqual(sequential.WritablePixels)
            .ShouldBeTrue($"bandRows={bandRows} degree={degree}");
    }

    /// <summary>One operator, applied alone.</summary>
    private static SimdImaging.Image Apply(SimdImaging.Image image, string op) => op switch
    {
        "median" => image.Pipe().Median3x3().ToImage(),
        "erode" => image.Pipe().Erode3x3().ToImage(),
        "dilate" => image.Pipe().Dilate3x3().ToImage(),
        "emboss" => image.Pipe().Emboss(0.6f).ToImage(),
        "sobel" => image.Pipe().Sobel().ToImage(),
        "sharpen" => image.Pipe().Sharpen(0.6f).ToImage(),
        "invert" => image.Pipe().Invert().ToImage(),
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "unknown operator"),
    };

    /// <summary>The operator and invert in one chain, which is what makes the plan fuse and take the band path.</summary>
    private static SimdImaging.Image ApplyThenInvert(SimdImaging.Image image, string op) => op switch
    {
        "median" => image.Pipe().Median3x3().Invert().ToImage(),
        "erode" => image.Pipe().Erode3x3().Invert().ToImage(),
        "dilate" => image.Pipe().Dilate3x3().Invert().ToImage(),
        "emboss" => image.Pipe().Emboss(0.6f).Invert().ToImage(),
        "sobel" => image.Pipe().Sobel().Invert().ToImage(),
        "sharpen" => image.Pipe().Sharpen(0.6f).Invert().ToImage(),
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "unknown operator"),
    };

    private static SimdImaging.Image Ramp(int width, int height)
    {
        var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (byte)((x * 7 + y * 3) & 0xFF);
            pixels[i + 1] = (byte)((x * 3 + y * 11) & 0xFF);
            pixels[i + 2] = (byte)((x ^ y) * 5 & 0xFF);
            pixels[i + 3] = (byte)(200 + (x + y) % 56);
        }

        return image;
    }
}
