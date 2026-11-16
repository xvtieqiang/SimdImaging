using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The general convolution has a reference it can be held against: the 3×3 fast path that has been in the library
/// longer and is already byte-exact across tiers. Same kernel, same border, same bytes — or the general path is
/// wrong.
/// </summary>
public class ConvolveTests
{
    private const int Width = 37;
    private const int Height = 29;

    private static Image Pattern()
    {
        var image = Image.Create(Width, Height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)((i * 37) % 251);

        return image;
    }

    private static readonly float[] Kernel =
    [
        0.05f, 0.10f, 0.05f,
        0.10f, 0.40f, 0.10f,
        0.05f, 0.10f, 0.05f,
    ];

    [Fact]
    public void General3x3_MatchesTheFastPath_ByteForByte()
    {
        using var image = Pattern();

        using var fast = image.Convolve3x3(Kernel);
        using var general = image.Convolve(Kernel, 1, BorderMode.Clamp);

        general.WritablePixels.ToArray().ShouldBe(fast.WritablePixels.ToArray());
    }

    /// <summary>Alpha is not convolved: it passes through, as in the fast path.</summary>
    [Fact]
    public void Alpha_PassesThrough()
    {
        using var image = Pattern();
        var span = image.WritablePixels;
        for (var i = 3; i < span.Length; i += 4)
            span[i] = (byte)(i % 251);

        var before = span.ToArray();
        using var blurred = image.Convolve(Kernel, 1);

        var after = blurred.WritablePixels;
        for (var i = 3; i < after.Length; i += 4)
            after[i].ShouldBe(before[i]);
    }

    /// <summary>
    /// Border modes must actually differ. A 3×3 box on <c>30 + 20x</c> leaves the left edge as the average of three
    /// taps, and the four modes pick different columns for the two off-image ones: Clamp repeats column 0, Mirror
    /// reflects to column 1 (x[−1] = x[1], as documented), Wrap brings column 8 in, Constant reads zero.
    /// </summary>
    [Fact]
    public void BorderModes_DifferAtTheEdge()
    {
        using var image = Image.Create(9, 9, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < 9; y++)
        {
            for (var x = 0; x < 9; x++)
            {
                var o = ((y * 9) + x) * 4;
                span[o] = (byte)(30 + (x * 20));
                span[o + 1] = (byte)(y * 20);
                span[o + 2] = 100;
                span[o + 3] = 255;
            }
        }

        var box = new float[9];
        Array.Fill(box, 1f / 9f);

        int Left(BorderMode border)
        {
            using var result = image.Convolve(box, 1, border);
            return result.WritablePixels[((4 * 9) + 0) * 4];
        }

        Left(BorderMode.Clamp).ShouldBe((byte)37);
        Left(BorderMode.Mirror).ShouldBe((byte)43);
        Left(BorderMode.Wrap).ShouldBe((byte)90);
        Left(BorderMode.Constant).ShouldBe((byte)27);
    }
}
