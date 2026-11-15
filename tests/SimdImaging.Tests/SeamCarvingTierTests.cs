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
                ? source.RemoveSeams(axis, count)
                : source.RemoveSeams(axis, count);
            return carved.WritablePixels.ToArray();
        }
        finally
        {
            SeamCarving.ForceScalarVector = false;
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
