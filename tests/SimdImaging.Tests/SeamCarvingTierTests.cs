using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// The DP row runs four cells at a time. These tests hold the vector path against the scalar twin on the same
/// pixels, because the risk is not "slightly different" — a different rounding anywhere changes which seam is
/// cheapest, and the carve then removes a visibly different column.
/// </summary>
public class SeamCarvingTierTests
{
    [Theory]
    [InlineData(SeamAxis.Vertical)]
    [InlineData(SeamAxis.Horizontal)]
    public void RemoveSeams_VectorAndScalarAgreeExactly(SeamAxis axis)
    {
        using var source = Pattern(97, 61);

        var vector = Remove(source, axis, 9, forceScalar: false);
        var scalar = Remove(source, axis, 9, forceScalar: true);

        vector.ShouldBe(scalar);
    }

    [Fact]
    public void RemoveSeams_OnAFlatImage_VectorAndScalarAgree()
    {
        // Every energy is zero, so the whole row is one tie: this is where a wrong comparison operator shows up.
        using var source = Image.Create(64, 40, WorkingFormat.Rgba8);
        source.WritablePixels.Fill(120);

        Remove(source, SeamAxis.Vertical, 12, forceScalar: false)
            .ShouldBe(Remove(source, SeamAxis.Vertical, 12, forceScalar: true));
    }

    [Fact]
    public void RemoveSeams_OnANarrowImage_ExercisesTheTail()
    {
        // Width 6 leaves the four-lane body nothing but the scalar prologue and epilogue.
        using var source = Pattern(6, 20);

        Remove(source, SeamAxis.Vertical, 2, forceScalar: false)
            .ShouldBe(Remove(source, SeamAxis.Vertical, 2, forceScalar: true));
    }

    private static byte[] Remove(Image source, SeamAxis axis, int count, bool forceScalar)
    {
        try
        {
            SeamCarving.ForceScalarVector = forceScalar;
            using var carved = axis == SeamAxis.Vertical
                ? source.Pipe().RemoveSeams(axis, count).ToImage()
                : source.Pipe().RemoveSeams(axis, count).ToImage();
            return carved.WritablePixels.ToArray();
        }
        finally
        {
            SeamCarving.ForceScalarVector = false;
        }
    }


    /// <summary>
    /// The integer programme against the double one it replaced: for 8- and 16-bit sources every value in the
    /// integer rows is exactly representable, so the two must agree to the byte — including on the tie-heavy and
    /// ragged inputs where a different lane width would show up first.
    /// </summary>
    [Theory]
    [InlineData(SeamAxis.Vertical, 1)]
    [InlineData(SeamAxis.Vertical, 9)]
    [InlineData(SeamAxis.Horizontal, 4)]
    public void IntegerProgramme_MatchesTheDoubleOneExactly(SeamAxis axis, int count)
    {
        using var image = Pattern(97, 61);

        var integer = Remove(image, axis, count, forceScalar: false);
        var doubly = WithDouble(image, () => Remove(image, axis, count, forceScalar: false));

        integer.ShouldBe(doubly);
    }

    [Fact]
    public void IntegerProgramme_OnAFlatImage_MatchesTheDoubleOne()
    {
        using var image = Image.Create(64, 40, WorkingFormat.Rgba8);
        image.WritablePixels.Fill(120);

        Remove(image, SeamAxis.Vertical, 12, forceScalar: false).ShouldBe(WithDouble(image, () => Remove(image, SeamAxis.Vertical, 12, forceScalar: false)));
    }

    [Fact]
    public void IntegerProgramme_OnAWanderingPath_MatchesTheDoubleOne()
    {
        // A diagonal ramp: every row's seam sits one column further along, which is the case where a mapping bug
        // would shift a column without ever failing a flat-image test.
        using var image = Image.Create(96, 64, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 96; x++)
            {
                var i = ((y * 96) + x) * 4;
                var value = (byte)Math.Min(255, Math.Abs(x - y) * 4);
                pixels[i] = value;
                pixels[i + 1] = (byte)(255 - value);
                pixels[i + 2] = (byte)((x * y) & 0xFF);
                pixels[i + 3] = 255;
            }
        }

        Remove(image, SeamAxis.Vertical, 7, forceScalar: false).ShouldBe(WithDouble(image, () => Remove(image, SeamAxis.Vertical, 7, forceScalar: false)));
    }

    [Theory]
    [InlineData(WorkingFormat.Rgba16)]
    public void IntegerProgramme_MatchesTheDoubleOneOnSixteenBit(WorkingFormat format)
    {
        using var image = Image.Create(83, 47, format);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.WritablePixels);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (ushort)((i * 7919 + 3) & 0xFFFF);

        Remove(image, SeamAxis.Vertical, 5, forceScalar: false).ShouldBe(WithDouble(image, () => Remove(image, SeamAxis.Vertical, 5, forceScalar: false)));
    }

    private static byte[] WithDouble(Image image, Func<byte[]> run)
    {
        try
        {
            SeamCarving.ForceDoubleDp = true;
            return run();
        }
        finally
        {
            SeamCarving.ForceDoubleDp = false;
        }
    }

    private static Image Pattern(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                span[i] = (byte)((x * 7) + (y * 3));
                span[i + 1] = (byte)((x * 13) ^ (y * 5));
                span[i + 2] = (byte)((x + y) * 11);
                span[i + 3] = 255;
            }
        }

        return image;
    }
}
