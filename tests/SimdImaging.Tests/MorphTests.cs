using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Min and max select rather than compute, so there is no tolerance to argue about: the result must be the exact
/// value the window contains, and the reference is a window scan.
/// </summary>
public class MorphTests
{
    private static Image Pattern(int width = 21, int height = 13)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        var random = new Random(20261117);
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)random.Next(256);

        return image;
    }

    private static byte[] Reference(Image image, int radius, bool minimum)
    {
        var width = image.Width;
        var height = image.Height;
        var source = image.WritablePixels.ToArray();
        var expected = new byte[source.Length];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var best = minimum ? byte.MaxValue : byte.MinValue;
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        for (var dx = -radius; dx <= radius; dx++)
                        {
                            var sx = Math.Clamp(x + dx, 0, width - 1);
                            var sy = Math.Clamp(y + dy, 0, height - 1);
                            var value = source[(((sy * width) + sx) * 4) + c];
                            best = minimum ? Math.Min(best, value) : Math.Max(best, value);
                        }
                    }

                    expected[(((y * width) + x) * 4) + c] = best;
                }
            }
        }

        return expected;
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    [InlineData(5, true)]
    [InlineData(5, false)]
    public void Morph_MatchesANaiveWindowScan(int radius, bool minimum)
    {
        using var image = Pattern();
        var expected = Reference(image, radius, minimum);

        using var actual = image.Morph(radius, minimum ? Morphology.Erode : Morphology.Dilate);

        for (var y = 0; y < image.Height; y++)
        {
            actual.WritablePixels.Slice(y * image.Width * 4, image.Width * 4).ToArray()
                .ShouldBe(expected.AsSpan(y * image.Width * 4, image.Width * 4).ToArray(), $"row {y} differs");
        }
    }

    /// <summary>The banded plan must equal the whole-image call: the window is the halo.</summary>
    [Fact]
    public void BandedPlan_MatchesTheWholeImage()
    {
        using var image = Pattern();
        using var whole = image.Morph(3, Morphology.Dilate);

        var plan = new ProcessingPlan { BandRows = 4 }.Add(PlanOps.Morph(3, Morphology.Dilate));
        using var banded = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { MaxDegreeOfParallelism = 4 });

        for (var y = 0; y < image.Height; y++)
            banded.View.Row(y).ToArray().ShouldBe(whole.WritablePixels.Slice(y * image.Width * 4, image.Width * 4).ToArray(),
                $"row {y} differs");
    }

    /// <summary>
    /// Opening and closing are compositions, so they must equal doing the two primitives by hand — byte for byte, no
    /// tolerance, because every step is selection or a clamped subtraction.
    /// </summary>
    [Theory]
    [InlineData(2, Morphology.Open)]
    [InlineData(2, Morphology.Close)]
    [InlineData(3, Morphology.Open)]
    [InlineData(3, Morphology.Close)]
    public void Combinations_MatchThePrimitivesByHand(int radius, Morphology kind)
    {
        using var image = Pattern();

        using var first = image.Morph(radius, kind == Morphology.Open ? Morphology.Erode : Morphology.Dilate);
        using var second = first.Morph(radius, kind == Morphology.Open ? Morphology.Dilate : Morphology.Erode);

        using var combined = image.Morph(radius, kind);

        combined.WritablePixels.ToArray().ShouldBe(second.WritablePixels.ToArray());
    }

    /// <summary>
    /// Top-hat is what the opening removed: zero on a flat image, and the bright speck itself on a speckled one.
    /// Black-hat is the mirror, so a bright speck leaves it at zero.
    /// </summary>
    [Fact]
    public void Hats_FollowFromTheCombinations()
    {
        using var image = Image.Create(15, 15, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = span[i + 1] = span[i + 2] = 60;
            span[i + 3] = 255;
        }

        var speck = ((7 * 15) + 7) * 4;
        span[speck] = span[speck + 1] = span[speck + 2] = 200;

        using var topHat = image.Morph(2, Morphology.TopHat);
        using var blackHat = image.Morph(2, Morphology.BlackHat);

        topHat.WritablePixels[speck].ShouldBe((byte)140, "the top-hat holds the speck the opening removed");
        blackHat.WritablePixels[speck].ShouldBe((byte)0, "a bright speck leaves the black-hat empty");
        topHat.WritablePixels[0].ShouldBe((byte)0, "a flat region has nothing to remove");
    }

    /// <summary>Radius 1 keeps the 3×3 fast path, so the two must agree.</summary>
    [Fact]
    public void RadiusOne_MatchesTheThreeByThreePath()
    {
        using var image = Pattern();

        using var fast = image.Erode3x3();
        using var general = image.Morph(1, Morphology.Erode);

        general.WritablePixels.ToArray().ShouldBe(fast.WritablePixels.ToArray());
    }
}
