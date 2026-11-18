using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The sixteen-bit point-operator table is constructive: it is filled by running the operator's own row
/// transform once per value. These tests hold it against the float path on the same pixels.
/// </summary>
public class PointLut16Tests
{
    [Theory]
    [InlineData("invert")]
    [InlineData("brightness")]
    [InlineData("gamma")]
    [InlineData("contrast")]
    [InlineData("sepia")]
    [InlineData("grayscale")]
    public void Rgba16_PointOperators_TableMatchesTheFloatPath(string op)
    {
        // Above PointOps.Lut16PixelFloor, or both sides would take the float path and the comparison would
        // prove nothing.
        using var source = Pattern(768, 700);

        var table = WithScalar(false, () => Run(op, source));
        var scalar = WithScalar(true, () => Run(op, source));

        table.ShouldBe(scalar);
    }

    [Fact]
    public void Rgba16_BelowTheFloor_StillMatches()
    {
        // Under Lut16PixelFloor the table is not built at all; the two sides must still agree.
        using var source = Pattern(64, 40);   // 2560 pixels, well under the floor

        WithScalar(false, () => Run("invert", source)).ShouldBe(WithScalar(true, () => Run("invert", source)));
    }

    private static byte[] Run(string op, Image source)
    {
        using var result = op switch
        {
            "invert" => source.Pipe().Invert().ToImage(),
            "brightness" => source.Pipe().Brightness(0.15f).ToImage(),
            "gamma" => source.Pipe().Gamma(1.8f).ToImage(),
            "contrast" => source.Pipe().Contrast(1.3f).ToImage(),
            // Both mix channels; if either were routed through a per-channel table this comparison fails.
            "sepia" => source.Pipe().Sepia().ToImage(),
            "grayscale" => source.Pipe().Grayscale().ToImage(),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };

        return result.WritablePixels.ToArray();
    }

    private static byte[] WithScalar(bool scalar, Func<byte[]> run)
    {
        try
        {
            PointOps.ForceScalarPoint = scalar;
            return run();
        }
        finally
        {
            PointOps.ForceScalarPoint = false;
        }
    }

    private static Image Pattern(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba16);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.WritablePixels);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (ushort)((i * 1031 + 13) & 0xFFFF);

        return image;
    }
}
