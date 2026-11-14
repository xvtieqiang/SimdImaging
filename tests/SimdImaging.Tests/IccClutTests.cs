using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The integer half of the ICC LUT path: the 1-D tables and the tetrahedral 3-D CLUT. These tests are
/// deliberately independent of profile parsing — they pin the arithmetic, and the profile tests pin how
/// the bytes reach it.
/// </summary>
public class IccClutTests
{
    private static Curve1D Curve(params ushort[] entries) => Curve1D.FromTable(entries);

    private static IccClut3D Cube(int size, Func<int, int, int, (ushort R, ushort G, ushort B)> corner)
    {
        var samples = new ushort[size * size * size * 3];
        var index = 0;
        for (var r = 0; r < size; r++)
            for (var g = 0; g < size; g++)
                for (var b = 0; b < size; b++)
                {
                    var (red, green, blue) = corner(r, g, b);
                    samples[index++] = red;
                    samples[index++] = green;
                    samples[index++] = blue;
                }

        return new IccClut3D(samples, size);
    }

    private static (ushort R, ushort G, ushort B) Evaluate(IccClut3D cube, ushort r, ushort g, ushort b)
    {
        Span<ushort> input = stackalloc ushort[3] { r, g, b };
        Span<ushort> output = stackalloc ushort[3];
        cube.Evaluate(input, output);
        return (output[0], output[1], output[2]);
    }

    [Fact]
    public void Curve_IdentityIsAPassThrough()
    {
        Curve1D.Identity.Map(0).ShouldBe((ushort)0);
        Curve1D.Identity.Map(12345).ShouldBe((ushort)12345);
        Curve1D.Identity.Map(65535).ShouldBe((ushort)65535);
        Curve1D.Identity.Count.ShouldBe(0);
    }

    [Fact]
    public void Curve_TwoEntryTableInterpolatesBothEndsExactly()
    {
        var curve = Curve(1000, 5000);

        curve.Map(0).ShouldBe((ushort)1000);
        curve.Map(65535).ShouldBe((ushort)5000);

        // Halfway through the domain is halfway through a two-entry table.
        curve.Map(32768).ShouldBe((ushort)3000);
    }

    [Fact]
    public void Curve_OneEntryTableIsAConstant()
    {
        var curve = Curve(4242);

        curve.Map(0).ShouldBe((ushort)4242);
        curve.Map(65535).ShouldBe((ushort)4242);
    }

    [Fact]
    public void Curve_SingleEntryTableIsRejected()
    {
        // A table with no entries is the identity, so an empty array cannot mean "empty table".
        Should.Throw<ArgumentException>(() => Curve1D.FromTable([]));
    }

    [Fact]
    public void Clut_AConstantCubeAnswersEverythingWithTheSameCode()
    {
        var cube = Cube(2, (_, _, _) => (100, 200, 300));

        Evaluate(cube, 0, 0, 0).ShouldBe(((ushort)100, (ushort)200, (ushort)300));
        Evaluate(cube, 65535, 65535, 65535).ShouldBe(((ushort)100, (ushort)200, (ushort)300));
        Evaluate(cube, 1234, 5678, 9012).ShouldBe(((ushort)100, (ushort)200, (ushort)300));
    }

    [Fact]
    public void Clut_IsExactOnTheSixEdgesOfTheUnitCube()
    {
        // Distinct codes per corner so a wrong corner walk cannot cancel out.
        var cube = Cube(2, (r, g, b) => ((ushort)(r * 1000 + g * 200 + b * 40), 0, 0));

        // 000 → 100
        Evaluate(cube, 32768, 0, 0).R.ShouldBe((ushort)500);
        // 000 → 010
        Evaluate(cube, 0, 32768, 0).R.ShouldBe((ushort)100);
        // 000 → 001
        Evaluate(cube, 0, 0, 32768).R.ShouldBe((ushort)20);
        // 000 → 110: the r = g face diagonal
        Evaluate(cube, 32768, 32768, 0).R.ShouldBe((ushort)600);
        // 000 → 101
        Evaluate(cube, 32768, 0, 32768).R.ShouldBe((ushort)520);
        // 000 → 011
        Evaluate(cube, 0, 32768, 32768).R.ShouldBe((ushort)120);
        // 111
        Evaluate(cube, 65535, 65535, 65535).R.ShouldBe((ushort)1240);
    }

    [Fact]
    public void Clut_ReproducesAnAffineCubeExactly()
    {
        // A CLUT that is affine in its indices is reproduced by tetrahedral interpolation at any grid
        // size and any input — that is the whole reason for tetrahedral over trilinear.
        const int size = 5;
        var cube = Cube(size, (r, g, b) =>
        {
            var scale = 1.0 / (size - 1);
            var value = 20000 + 10000 * r * scale - 3000 * g * scale + 5000 * b * scale;
            return ((ushort)Math.Round(value), (ushort)Math.Round(value / 2), (ushort)Math.Round(value / 4));
        });

        // Deterministic sweep: every axis takes nine positions, including points inside each tetrahedron.
        for (var r = 0; r < 9; r++)
            for (var g = 0; g < 9; g++)
                for (var b = 0; b < 9; b++)
                {
                    var codeR = (ushort)(r * 65535 / 8);
                    var codeG = (ushort)(g * 65535 / 8);
                    var codeB = (ushort)(b * 65535 / 8);

                    var expected = 20000 + 10000 * (codeR / 65535.0) - 3000 * (codeG / 65535.0) + 5000 * (codeB / 65535.0);
                    var actual = Evaluate(cube, codeR, codeG, codeB).R;

                    Math.Abs(actual - expected).ShouldBeLessThanOrEqualTo(
                        2.0, $"input ({codeR},{codeG},{codeB}) expected {expected:F2}, got {actual}");
                }
    }

    [Fact]
    public void Clut_ClampsRatherThanWrappingWhenACornerExceedsTheRange()
    {
        // A CLUT may legitimately climb above 65535 between corners; the output code cannot.
        var cube = Cube(2, (r, g, b) => (r == 1 && g == 1 && b == 1 ? (ushort)65535 : (ushort)0, 0, 0));
        var corner = Evaluate(cube, 65535, 65535, 65535).R;
        corner.ShouldBe((ushort)65535);

        var inside = Evaluate(cube, 65534, 65534, 65534).R;
        inside.ShouldBeLessThanOrEqualTo((ushort)65535);
    }

    [Fact]
    public void Clut_RejectsAGridTooSmallToInterpolate()
    {
        Should.Throw<InvalidDataException>(() => new IccClut3D(new ushort[3], 1));
    }

    [Fact]
    public void Clut_RejectsASampleCountThatDoesNotMatchTheGrid()
    {
        Should.Throw<InvalidDataException>(() => new IccClut3D(new ushort[30], 2));
    }
}
