using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The convolution kernel now has a fourth tier. This machine has no AVX-512, so the 512 path is exercised
/// through <c>Vector512&lt;T&gt;</c>'s software implementation: the arithmetic — and therefore the bytes — can
/// be checked here, the throughput cannot.
/// <para>
/// All three paths must agree exactly, not approximately. The kernel's determinism argument is that the vector
/// axis is pixels and every lane sums its taps in ascending index order, so a lane's addition sequence is
/// character-for-character the scalar twin's. These tests are what stops that argument from becoming folklore.
/// </para>
/// </summary>
public class PlanarConvolveTierTests
{
    private static readonly float[] Gaussian = [1f / 16, 4f / 16, 6f / 16, 4f / 16, 1f / 16];

    [Fact]
    public void Separable_AllTiersAgreeExactly()
    {
        const int width = 97;      // odd width: the vector tail and the clamped prefix both get exercised
        const int height = 23;
        var source = Plane(width, height);
        var scalar = new float[width * height];
        var hardware = new float[width * height];
        var software512 = new float[width * height];

        WithTiers(
            scalarRun: () => PlanarConvolve.Separable(source, scalar, width, height, Gaussian),
            hardwareRun: () => PlanarConvolve.Separable(source, hardware, width, height, Gaussian),
            software512Run: () => PlanarConvolve.Separable(source, software512, width, height, Gaussian));

        hardware.ShouldBe(scalar);
        software512.ShouldBe(scalar);
    }

    [Fact]
    public void Sparse_AllTiersAgreeExactly()
    {
        const int width = 89;
        const int height = 17;
        var source = Plane(width, height);
        var scalar = new float[width * height];
        var hardware = new float[width * height];
        var software512 = new float[width * height];

        // A 15-tap motion PSF: dense enough to hit the vector body, sparse enough to use the tap walk.
        var taps = new SparseTap[15];
        for (var i = 0; i < taps.Length; i++)
            taps[i] = new SparseTap(i - 7, 0, 1f / 15);

        WithTiers(
            scalarRun: () => PlanarConvolve.Sparse(source, scalar, width, height, taps),
            hardwareRun: () => PlanarConvolve.Sparse(source, hardware, width, height, taps),
            software512Run: () => PlanarConvolve.Sparse(source, software512, width, height, taps));

        hardware.ShouldBe(scalar);
        software512.ShouldBe(scalar);
    }

    [Fact]
    public void Vector512Hook_ReallySelectsTheWiderTier()
    {
        // Guards the two tests above from passing vacuously: if the hook stopped reaching the 512 branch they
        // would compare 256 against 256 and go green without ever running the tier they claim to check.
        try
        {
            PlanarConvolve.ForceScalar = true;
            PlanarConvolve.Use512.ShouldBeFalse("scalar is forced");

            PlanarConvolve.ForceScalar = false;
            PlanarConvolve.ForceVector512 = true;
            PlanarConvolve.Use512.ShouldBeTrue("the hook must select the 512 path");
        }
        finally
        {
            PlanarConvolve.ForceScalar = false;
            PlanarConvolve.ForceVector512 = false;
        }
    }

    private static void WithTiers(Action scalarRun, Action hardwareRun, Action software512Run)
    {
        try
        {
            PlanarConvolve.ForceScalar = true;
            scalarRun();

            PlanarConvolve.ForceScalar = false;
            hardwareRun();

            PlanarConvolve.ForceVector512 = true;
            software512Run();
        }
        finally
        {
            PlanarConvolve.ForceScalar = false;
            PlanarConvolve.ForceVector512 = false;
        }
    }

    private static float[] Plane(int width, int height)
    {
        var plane = new float[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                plane[(y * width) + x] = (float)(Math.Sin((x * 0.7) + (y * 1.3)) * 0.5 + 0.5);
        return plane;
    }
}
