using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The guided filter now runs several lane-parallel passes through <c>Vector256</c>. These tests hold that
/// path against the scalar twin on the same pixels — and pin the one loop that must never be vectorised.
/// </summary>
public class GuidedFilterTierTests
{
    [Theory]
    [InlineData(37, 23, 3)]
    [InlineData(64, 40, 4)]
    [InlineData(6, 5, 2)]      // narrower than the window: exercises the non-interior branch
    public void CrossGuided_VectorAndScalarAgreeExactly(int width, int height, int radius)
    {
        using var source = Pattern(width, height);
        using var guide = source.GaussianBlur(1f);

        var vector = WithScalar(false, () => Cross(source, guide, radius));
        var scalar = WithScalar(true, () => Cross(source, guide, radius));

        vector.ShouldBe(scalar);
    }

    [Fact]
    public void SelfGuided_VectorAndScalarAgreeExactly()
    {
        using var source = Pattern(48, 31);

        var vector = WithScalar(false, () => Self(source));
        var scalar = WithScalar(true, () => Self(source));

        vector.ShouldBe(scalar);
    }

    [Fact]
    public void ThePrefixSumStaysSerial()
    {
        // A full row of 255s with the widest radius the benchmark uses. The box mean of a constant is that
        // constant only if the running prefix accumulates the way the scalar twin does; regrouping it into
        // vector blocks (a prefix scan) changes the rounding, and the last columns are where it shows first.
        using var source = Image.Create(4096, 3, WorkingFormat.Rgba8);
        source.WritablePixels.Fill(255);

        using var filtered = source.GuidedFilter(4, 1e-4f);
        foreach (var value in filtered.WritablePixels)
            value.ShouldBe((byte)255);
    }

    [Fact]
    public void VectorHook_ReallySelectsTheVectorPath()
    {
        // Guards the two tests above from passing vacuously: with the hook ignored they would compare the
        // vector path against itself.
        try
        {
            GuidedFilterOps.ForceScalarVector = true;
            GuidedFilterOps.Vectorized.ShouldBeFalse("scalar is forced");

            GuidedFilterOps.ForceScalarVector = false;
            GuidedFilterOps.Vectorized.ShouldBe(System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated);
        }
        finally
        {
            GuidedFilterOps.ForceScalarVector = false;
        }
    }

    private static byte[] Cross(Image source, Image guide, int radius)
    {
        using var result = source.GuidedFilter(guide, radius, 1e-4f);
        return result.WritablePixels.ToArray();
    }

    private static byte[] Self(Image source)
    {
        using var result = source.GuidedFilter(3, 1e-4f);
        return result.WritablePixels.ToArray();
    }

    private static T WithScalar<T>(bool scalar, Func<T> run)
    {
        try
        {
            GuidedFilterOps.ForceScalarVector = scalar;
            return run();
        }
        finally
        {
            GuidedFilterOps.ForceScalarVector = false;
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
                span[i] = (byte)((x * 11) + (y * 5));
                span[i + 1] = (byte)((x * 3) ^ (y * 7));
                span[i + 2] = (byte)((x + y) * 9);
                span[i + 3] = 255;
            }
        }

        return image;
    }
}
