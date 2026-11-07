namespace SimdImaging.Processing;

/// <summary>The reconstruction filter a resize uses. Each is a separable kernel; the weight table is built once
/// per axis and reused for every row.</summary>
public enum Resampler : byte
{
    /// <summary>Point sampling: one source pixel per destination pixel.</summary>
    Nearest,

    /// <summary>Area average when downscaling (blocky when upscaling): the kernel is a box of the scaled width.</summary>
    Box,

    /// <summary>Triangle / bilinear.</summary>
    Bilinear,

    /// <summary>Catmull-Rom cubic (B = 0, C = 0.5): the default, sharp without ringing.</summary>
    Bicubic,

    /// <summary>Lanczos with a = 3: sharpest of the set, slight ringing on hard edges.</summary>
    Lanczos3,
}

internal static class ResamplerKernels
{
    public static (double Support, Func<double, double> Kernel) Get(Resampler resampler) => resampler switch
    {
        Resampler.Nearest => (0.5, static x => Math.Abs(x) < 0.5 ? 1.0 : 0.0),
        Resampler.Box => (0.5, static x => Math.Abs(x) <= 0.5 ? 1.0 : 0.0),
        Resampler.Bilinear => (1.0, static x => { var a = Math.Abs(x); return a < 1.0 ? 1.0 - a : 0.0; }),
        Resampler.Bicubic => (2.0, CatmullRom),
        Resampler.Lanczos3 => (3.0, static x =>
        {
            var a = Math.Abs(x);
            return a < 3.0 ? Sinc(a) * Sinc(a / 3.0) : 0.0;
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(resampler), resampler, "Not a defined resampler."),
    };

    private static double CatmullRom(double x)
    {
        const double a = -0.5;
        x = Math.Abs(x);
        if (x < 1.0)
            return ((a + 2.0) * x - (a + 3.0)) * x * x + 1.0;
        if (x < 2.0)
            return (((x - 5.0) * x + 8.0) * x - 4.0) * a;
        return 0.0;
    }

    private static double Sinc(double x) => x == 0.0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
}
