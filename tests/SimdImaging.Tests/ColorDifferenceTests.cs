using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class ColorDifferenceTests
{
    /// <summary>
    /// Sharma, Wu and Dalal (2005), "The CIEDE2000 Color-Difference Formula: Implementation Notes,
    /// Supplementary Test Data, and Mathematical Observations", Table 1 — the thirty-four pairs the
    /// paper publishes so implementations can be compared. They are copied from the paper, not computed
    /// here; pairs 9-16 sit on the hue discontinuity and pairs 17-20 are far apart, which is where a
    /// wrong branch shows up.
    /// </summary>
    private static readonly (double L1, double A1, double B1, double L2, double A2, double B2, double Expected)[] SharmaPairs =
    [
        (50.0000, 2.6772, -79.7751, 50.0000, 0.0000, -82.7485, 2.0425),
        (50.0000, 3.1571, -77.2803, 50.0000, 0.0000, -82.7485, 2.8615),
        (50.0000, 2.8361, -74.0200, 50.0000, 0.0000, -82.7485, 3.4412),
        (50.0000, -1.3802, -84.2814, 50.0000, 0.0000, -82.7485, 1.0000),
        (50.0000, -1.1848, -84.8006, 50.0000, 0.0000, -82.7485, 1.0000),
        (50.0000, -0.9009, -85.5211, 50.0000, 0.0000, -82.7485, 1.0000),
        (50.0000, 0.0000, 0.0000, 50.0000, -1.0000, 2.0000, 2.3669),
        (50.0000, -1.0000, 2.0000, 50.0000, 0.0000, 0.0000, 2.3669),
        (50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0009, 7.1792),
        (50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0010, 7.1792),
        (50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0011, 7.2195),
        (50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0012, 7.2195),
        (50.0000, -0.0010, 2.4900, 50.0000, 0.0009, -2.4900, 4.8045),
        (50.0000, -0.0010, 2.4900, 50.0000, 0.0010, -2.4900, 4.8045),
        (50.0000, -0.0010, 2.4900, 50.0000, 0.0011, -2.4900, 4.7461),
        (50.0000, 2.5000, 0.0000, 50.0000, 0.0000, -2.5000, 4.3065),
        (50.0000, 2.5000, 0.0000, 73.0000, 25.0000, -18.0000, 27.1492),
        (50.0000, 2.5000, 0.0000, 61.0000, -5.0000, 29.0000, 22.8977),
        (50.0000, 2.5000, 0.0000, 56.0000, -27.0000, -3.0000, 31.9030),
        (50.0000, 2.5000, 0.0000, 58.0000, 24.0000, 15.0000, 19.4535),
        (50.0000, 2.5000, 0.0000, 50.0000, 3.1736, 0.5854, 1.0000),
        (50.0000, 2.5000, 0.0000, 50.0000, 3.2972, 0.0000, 1.0000),
        (50.0000, 2.5000, 0.0000, 50.0000, 1.8634, 0.5757, 1.0000),
        (50.0000, 2.5000, 0.0000, 50.0000, 3.2592, 0.3350, 1.0000),
        (60.2574, -34.0099, 36.2677, 60.4626, -34.1751, 39.4387, 1.2644),
        (63.0109, -31.0961, -5.8663, 62.8187, -29.7946, -4.0864, 1.2630),
        (61.2901, 3.7196, -5.3901, 61.4292, 2.2480, -4.9620, 1.8731),
        (35.0831, -44.1164, 3.7933, 35.0232, -40.0716, 1.5901, 1.8645),
        (22.7233, 20.0904, -46.6940, 23.0331, 14.9730, -42.5619, 2.0373),
        (36.4612, 47.8580, 18.3852, 36.2715, 50.5065, 21.2231, 1.4146),
        (90.8027, -2.0831, 1.4410, 91.1528, -1.6435, 0.0447, 1.4441),
        (90.9257, -0.5406, -0.9208, 88.6381, -0.8985, -0.7239, 1.5381),
        (6.7747, -0.2908, -2.4247, 5.8714, -0.0985, -2.2286, 0.6377),
        (2.0776, 0.0795, -1.1350, 0.9033, -0.0636, -0.5514, 0.9082),
    ];

    [Fact]
    public void DeltaE2000_MatchesEverySharmaReferencePair()
    {
        foreach (var pair in SharmaPairs)
        {
            var actual = ColorDifference.DeltaE2000(
                pair.L1, pair.A1, pair.B1, pair.L2, pair.A2, pair.B2);

            actual.ShouldBe(pair.Expected, 1e-4,
                $"({pair.L1},{pair.A1},{pair.B1}) vs ({pair.L2},{pair.A2},{pair.B2}) expected {pair.Expected}, got {actual}");
        }
    }

    [Fact]
    public void DeltaE2000_IsSymmetric()
    {
        foreach (var pair in SharmaPairs)
        {
            var forward = ColorDifference.DeltaE2000(pair.L1, pair.A1, pair.B1, pair.L2, pair.A2, pair.B2);
            var backward = ColorDifference.DeltaE2000(pair.L2, pair.A2, pair.B2, pair.L1, pair.A1, pair.B1);

            backward.ShouldBe(forward, 1e-12);
        }
    }

    [Fact]
    public void DeltaE2000_IdenticalColoursAreZero()
    {
        ColorDifference.DeltaE2000(50, 10, -10, 50, 10, -10).ShouldBe(0.0, 1e-12);
        ColorDifference.DeltaE2000(0, 0, 0, 0, 0, 0).ShouldBe(0.0, 1e-12);
    }

    [Fact]
    public void DeltaE2000_GrowsWithLightness()
    {
        var small = ColorDifference.DeltaE2000(50, 0, 0, 52, 0, 0);
        var large = ColorDifference.DeltaE2000(50, 0, 0, 60, 0, 0);

        large.ShouldBeGreaterThan(small);
    }

    private static Image Patch(byte value)
    {
        var image = Image.Create(8, 6, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return image;
    }

    [Fact]
    public void DeltaE2000_OfAnImageAgainstItselfIsZero()
    {
        using var image = Patch(120);
        var (mean, max) = ColorDifference.DeltaE2000(image[0].Pixels.View, image[0].Pixels.View);

        mean.ShouldBe(0.0, 1e-12);
        max.ShouldBe(0.0, 1e-12);
    }

    [Fact]
    public void DeltaE2000_OfTwoGreysIsTheLightnessDifference()
    {
        using var light = Patch(200);
        using var dark = Patch(100);
        var (mean, max) = ColorDifference.DeltaE2000(light[0].Pixels.View, dark[0].Pixels.View);

        mean.ShouldBeGreaterThan(10.0);
        max.ShouldBe(mean, 0.2);
    }

    [Fact]
    public void DeltaE2000_RejectsImagesOfDifferentSizes()
    {
        using var small = Patch(10);
        using var large = Image.Create(9, 6, WorkingFormat.Rgba8);

        Should.Throw<ArgumentException>(() => ColorDifference.DeltaE2000(small[0].Pixels.View, large[0].Pixels.View));
    }
}
