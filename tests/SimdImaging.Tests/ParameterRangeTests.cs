using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// A parameter whose domain ends at a value must reject what is past it rather than truncate it: a clamp turns a
/// caller's mistake into a plausible-looking picture, which is the one failure mode that never gets reported.
/// Contrast and Saturation are deliberately absent — a multiplier is defined at any finite value.
/// </summary>
public class ParameterRangeTests
{
    private static Image Pixels()
    {
        var image = Image.Create(8, 6, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)((i * 31) % 251);

        return image;
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void Sepia_RejectsABlendOutsideItsRange(float blend)
    {
        using var image = Pixels();
        Should.Throw<ArgumentOutOfRangeException>(() => image.Sepia(blend));
    }

    [Theory]
    [InlineData(-1.2f)]
    [InlineData(1.2f)]
    public void Temperature_RejectsAShiftOutsideItsRange(float shift)
    {
        using var image = Pixels();
        Should.Throw<ArgumentOutOfRangeException>(() => image.Temperature(shift));
    }

    /// <summary>The endpoints themselves are legitimate.</summary>
    [Fact]
    public void TheEndpointsAreInside()
    {
        using var image = Pixels();

        using var fullSepia = image.Sepia(1f);
        using var noneSepia = image.Sepia(0f);
        using var warmest = image.Temperature(1f);
        using var coolest = image.Temperature(-1f);

        fullSepia.Width.ShouldBe(image.Width);
        noneSepia.WritablePixels.ToArray().ShouldBe(image.WritablePixels.ToArray());
    }

    /// <summary>A multiplier has no domain to leave, so the same guard would be wrong here.</summary>
    [Fact]
    public void MultipliersAcceptAnyFiniteValue()
    {
        using var image = Pixels();

        using var flat = image.Contrast(0f);
        using var doubled = image.Contrast(2f);
        using var negative = image.Saturation(-1f);

        flat.Width.ShouldBe(image.Width);
        doubled.Width.ShouldBe(image.Width);
        negative.Width.ShouldBe(image.Width);
    }
}
