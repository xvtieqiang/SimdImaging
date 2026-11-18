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

        using var fast = image.Pipe().Convolve3x3(Kernel).ToImage();
        using var general = image.Pipe().Convolve(Kernel, 1, BorderMode.Clamp).ToImage();

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
        using var blurred = image.Pipe().Convolve(Kernel, 1).ToImage();

        var after = blurred.WritablePixels;
        for (var i = 3; i < after.Length; i += 4)
            after[i].ShouldBe(before[i]);
    }

    /// <summary>The general median at radius 1 must reproduce the 3×3 fast path exactly, alpha included.</summary>
    [Fact]
    public void Median1_MatchesTheFastPath_ByteForByte()
    {
        using var image = Pattern();

        using var fast = image.Pipe().Median3x3().ToImage();
        using var general = image.Pipe().Median(1).ToImage();

        general.WritablePixels.ToArray().ShouldBe(fast.WritablePixels.ToArray());
    }

    /// <summary>
    /// A 5×5 window removes a 2×2 speck that a 3×3 window leaves half of — the reason the larger kernels exist.
    /// </summary>
    [Fact]
    public void LargerMedian_RemovesABiggerSpeck()
    {
        using var image = Image.Create(15, 15, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = 60;
            span[i + 1] = 60;
            span[i + 2] = 60;
            span[i + 3] = 255;
        }

        // A five-pixel cross centred at (7,7): five of the nine pixels in a 3×3 window, which a median of nine
        // keeps (the fifth value is white); five of twenty-five in a 5×5 window, which it drops.
        foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var o = (((7 + dy) * 15) + (7 + dx)) * 4;
            span[o] = span[o + 1] = span[o + 2] = 255;
        }

        var centre = ((7 * 15) + 7) * 4;

        using var small = image.Pipe().Median(1).ToImage();
        small.WritablePixels[centre].ShouldBeGreaterThan((byte)60, "a 3×3 window must keep a five-pixel cross");

        using var large = image.Pipe().Median(2).ToImage();
        large.WritablePixels[centre].ShouldBe((byte)60, "a 5×5 window must remove it");
    }

    /// <summary>
    /// The sliding histogram must equal a naive sort of the same window. Binning is exact for Rgba8, so there is no
    /// tolerance here: the two implementations either agree on every byte or one of them is wrong.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void HistogramMedian_MatchesANaiveSort(int radius)
    {
        using var image = Image.Create(17, 13, WorkingFormat.Rgba8);
        var random = new Random(20261116);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)random.Next(256);

        using var fast = image.Pipe().Median(radius).ToImage();

        // Naive reference: copy the window, sort, take the middle; edges replicate.
        var reference = new byte[span.Length];
        var window = new byte[(radius * 2 + 1) * (radius * 2 + 1)];
        for (var y = 0; y < 13; y++)
        {
            for (var x = 0; x < 17; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var filled = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        for (var dx = -radius; dx <= radius; dx++)
                        {
                            var sx = Math.Clamp(x + dx, 0, 16);
                            var sy = Math.Clamp(y + dy, 0, 12);
                            window[filled++] = span[(((sy * 17) + sx) * 4) + c];
                        }
                    }

                    Array.Sort(window);
                    reference[(((y * 17) + x) * 4) + c] = window[window.Length / 2];
                }
            }
        }

        fast.WritablePixels.ToArray().ShouldBe(reference);
    }

    /// <summary>
    /// The banded plan must give the same bytes as the whole-image call: a median reads a radius of rows on either
    /// side, so a plan that under-declares its halo shows up as a seam.
    /// </summary>
    [Theory]
    [InlineData(2, 4)]
    [InlineData(3, 1)]
    [InlineData(4, 7)]
    public void BandedMedian_MatchesWholeImage(int radius, int bandRows)
    {
        using var image = Pattern();
        using var whole = image.Pipe().Median(radius).ToImage();

        var plan = new ProcessingPlan { BandRows = bandRows }.Add(PlanOps.Median(radius));
        using var banded = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) });

        var expected = whole.WritablePixels;
        for (var y = 0; y < image.Height; y++)
        {
            banded.View.Row(y).ToArray().ShouldBe(expected.Slice(y * image.Width * 4, image.Width * 4).ToArray(),
                $"row {y} differs between the banded plan and the whole-image call");
        }
    }

    /// <summary>
    /// A naive convolution, used to decide which side is wrong when two implementations disagree.
    /// </summary>
    [Fact]
    public void ConvolveAndBanded_AgreeWithANaiveReference_ForWrap()
    {
        using var image = Pattern();
        var width = image.Width;
        var height = image.Height;
        var source = image.WritablePixels.ToArray();

        int Map(int value, int length) => ((value % length) + length) % length;

        var expected = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0f;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var sx = Map(x + dx, width);
                            var sy = Map(y + dy, height);
                            sum += Kernel[((dy + 1) * 3) + dx + 1] * (source[(((sy * width) + sx) * 4) + c] / 255f);
                        }
                    }

                    expected[(((y * width) + x) * 4) + c] = (byte)Math.Clamp((int)((sum * 255f) + 0.5f), 0, 255);
                }

                expected[(((y * width) + x) * 4) + 3] = source[(((y * width) + x) * 4) + 3];
            }
        }

        using var whole = FilterOps.Convolve(image[0].Pixels.View, Kernel, 1, BorderMode.Wrap);
        var plan = new ProcessingPlan { BandRows = 5 }.Add(PlanOps.Convolve(Kernel, 1, BorderMode.Wrap));
        using var banded = plan.Execute(image[0].Pixels.View);

        // Both paths are held to the naive answer. They disagreed once: the whole-image path's row ring has 2r+1
        // slots and Wrap needs rows height-2r apart, so it served a stale row from a colliding slot.
        for (var y = 0; y < height; y++)
        {
            whole.View.Row(y).ToArray().ShouldBe(expected.AsSpan(y * width * 4, width * 4).ToArray(),
                $"whole-image Wrap, row {y}");
            banded.View.Row(y).ToArray().ShouldBe(expected.AsSpan(y * width * 4, width * 4).ToArray(),
                $"banded Wrap, row {y}");
        }
    }

    /// <summary>
    /// A banded plan must give the same bytes as the whole-image call for every border mode. This is the test the
    /// padding approach could not pass: Mirror and Wrap have to be placed against the image edge, not the band edge.
    /// </summary>
    [Theory]
    [InlineData(BorderMode.Clamp, 3)]
    [InlineData(BorderMode.Clamp, 11)]
    [InlineData(BorderMode.Mirror, 3)]
    [InlineData(BorderMode.Mirror, 11)]
    [InlineData(BorderMode.Constant, 3)]
    [InlineData(BorderMode.Constant, 11)]
    [InlineData(BorderMode.Wrap, 3)]
    [InlineData(BorderMode.Wrap, 11)]
    public void BandedConvolve_MatchesWholeImage(BorderMode border, int bandRows)
    {
        using var image = Pattern();
        using var whole = FilterOps.Convolve(image[0].Pixels.View, Kernel, 1, border, constantValue: 0.2f);

        var plan = new ProcessingPlan { BandRows = bandRows }
            .Add(PlanOps.Convolve(Kernel, 1, border, constantValue: 0.2f));

        using var banded = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) });

        for (var y = 0; y < image.Height; y++)
        {
            banded.View.Row(y).ToArray().ShouldBe(
                whole.View.Row(y).ToArray(), $"{border} bandRows={bandRows} row {y} differs");
        }
    }

    /// <summary>A radius the histogram cannot index is refused explicitly.</summary>
    [Fact]
    public void MedianAboveRadius8_IsRefused()
    {
        using var image = Pattern();
        Should.Throw<ArgumentOutOfRangeException>(() => image.Pipe().Median(9).ToImage());
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
            using var result = image.Pipe().Convolve(box, 1, border).ToImage();
            return result.WritablePixels[((4 * 9) + 0) * 4];
        }

        Left(BorderMode.Clamp).ShouldBe((byte)37);
        Left(BorderMode.Mirror).ShouldBe((byte)43);
        Left(BorderMode.Wrap).ShouldBe((byte)90);
        Left(BorderMode.Constant).ShouldBe((byte)27);
    }
}
