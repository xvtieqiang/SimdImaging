using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// <c>ToImage()</c> hands back an image the caller owns and will dispose. If it ever hands back the source, that
/// dispose returns the source's buffer to the pool while the caller still holds it — so the contract is checked by
/// disposing the result and then reading the original.
/// </summary>
public class PipelineLifetimeTests
{
    private static Image Pixels()
    {
        var image = Image.Create(8, 6, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)((i * 37) % 251);

        return image;
    }

    /// <summary>An empty chain is the identity, and the identity is still a copy.</summary>
    [Fact]
    public void AnEmptyPipeline_ReturnsACopy_TheOriginalSurvivesItsDisposal()
    {
        using var image = Pixels();
        var expected = image.WritablePixels.ToArray();

        Image copy;
        using (copy = image.Pipe().ToImage())
        {
            copy.ShouldNotBeSameAs(image);
            copy.WritablePixels.ToArray().ShouldBe(expected);
        }

        // The dispose above is the test: the original must still be readable and unchanged.
        image.WritablePixels.ToArray().ShouldBe(expected);
    }

    /// <summary>A chain that does no work must not clone; the image comes back as itself.</summary>
    [Fact]
    public void Mutate_WithNoSteps_IsANoOp()
    {
        using var image = Pixels();
        var expected = image.WritablePixels.ToArray();

        var returned = image.Mutate(_ => { });

        returned.ShouldBeSameAs(image);
        image.WritablePixels.ToArray().ShouldBe(expected);
    }

    /// <summary>And a chain that does work still replaces the frames.</summary>
    [Fact]
    public void Mutate_WithSteps_ReplacesThePixels()
    {
        using var image = Pixels();
        var expected = image.WritablePixels.ToArray();

        image.Mutate(p => p.Invert());

        image.WritablePixels.ToArray().ShouldNotBe(expected);
        image.WritablePixels[0].ShouldBe((byte)(255 - expected[0]));
    }

    /// <summary>A non-empty chain returns its own buffer, not the source's.</summary>
    [Fact]
    public void ANonEmptyPipeline_ReturnsAnIndependentImage()
    {
        using var image = Pixels();
        var expected = image.WritablePixels.ToArray();

        using (var result = image.Pipe().Invert().ToImage())
        {
            result.ShouldNotBeSameAs(image);
        }

        image.WritablePixels.ToArray().ShouldBe(expected);
    }
}
