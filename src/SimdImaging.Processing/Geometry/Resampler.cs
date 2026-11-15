namespace SimdImaging.Processing;

/// <summary>The reconstruction filter a resize uses. Each is a separable kernel; the weight table is built once
/// per axis and reused for every row.</summary>
/// <remarks>
/// The first five members keep the byte values they shipped with, so a persisted or defaulted enum value never
/// changes meaning. Members are appended, never reordered.
/// <para>
/// Two pairs are the same kernel under two names, because that is how the names are used in the wild:
/// <see cref="Triangle"/> is <see cref="Bilinear"/>, and <see cref="CatmullRom"/> is <see cref="Bicubic"/>.
/// Kept as separate members so a caller porting code that says "Triangle" does not have to translate it.
/// </para>
/// </remarks>
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

    /// <summary>Triangle filter, support 1. Numerically identical to <see cref="Bilinear"/>.</summary>
    Triangle,

    /// <summary>Catmull-Rom (B = 0, C = 0.5), support 2. Numerically identical to <see cref="Bicubic"/>.</summary>
    CatmullRom,

    /// <summary>Cubic B-spline (B = 1, C = 0), support 2: the smoothest of the cubics, never rings, softens.</summary>
    Spline,

    /// <summary>Mitchell-Netravali (B = 1/3, C = 1/3), support 2: the balanced cubic, mild ringing.</summary>
    MitchellNetravali,

    /// <summary>Cubic Hermite (B = 0, C = 0), support 1: interpolating, softer than Catmull-Rom.</summary>
    Hermite,

    /// <summary>Welch parabola (<c>1 - x²</c>), support 1: cheap and smooth, blurs more than a triangle.</summary>
    Welch,

    /// <summary>Robidoux (B = 0.3782…, C = 0.3109…), support 2: tuned for minimal error on natural images.</summary>
    Robidoux,

    /// <summary>RobidouxSharp (B = 0.2620…, C = 0.3690…), support 2: Robidoux's sharper sibling.</summary>
    RobidouxSharp,

    /// <summary>Lanczos with a = 2: milder ringing than a = 3, popular for downscaling photographs.</summary>
    Lanczos2,

    /// <summary>Lanczos with a = 5: stronger ringing, slightly sharper than a = 3.</summary>
    Lanczos5,

    /// <summary>Lanczos with a = 8: the widest window here; only worth it for very large downscales.</summary>
    Lanczos8,
}

internal static class ResamplerKernels
{
    /// <summary>Mitchell-Netravali's two-parameter cubic family, on |x|.</summary>
    /// <remarks>
    /// One formula covers B-spline (1, 0), Mitchell (1/3, 1/3), Catmull-Rom (0, 0.5) and the Robidoux pair.
    /// Catmull-Rom and the existing <see cref="Resampler.Bicubic"/> deliberately do *not* go through this: the
    /// shipped kernel evaluates the same polynomial in a different order, and a different order rounds
    /// differently — re-routing it here would change every byte the library has ever produced.
    /// </remarks>
    private static Func<double, double> Cubic(double b, double c) => x =>
    {
        x = Math.Abs(x);
        if (x < 1.0)
            return ((12.0 - 9.0 * b - 6.0 * c) * x * x * x + (-18.0 + 12.0 * b + 6.0 * c) * x * x + 6.0 - 2.0 * b) / 6.0;
        if (x < 2.0)
            return ((-b - 6.0 * c) * x * x * x + (6.0 * b + 30.0 * c) * x * x + (-12.0 * b - 48.0 * c) * x + 8.0 * b + 24.0 * c) / 6.0;
        return 0.0;
    };

    /// <summary>Lanczos with window <paramref name="a"/>; a = 3 keeps its own case so its bytes do not move.</summary>
    private static Func<double, double> Lanczos(double a) => x =>
    {
        var v = Math.Abs(x);
        return v < a ? Sinc(v) * Sinc(v / a) : 0.0;
    };

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

        Resampler.Triangle => (1.0, static x => { var a = Math.Abs(x); return a < 1.0 ? 1.0 - a : 0.0; }),
        Resampler.CatmullRom => (2.0, CatmullRom),
        Resampler.Spline => (2.0, Cubic(1.0, 0.0)),
        Resampler.MitchellNetravali => (2.0, Cubic(1.0 / 3.0, 1.0 / 3.0)),
        Resampler.Hermite => (1.0, static x =>
        {
            var a = Math.Abs(x);
            return a < 1.0 ? (2.0 * a - 3.0) * a * a + 1.0 : 0.0;
        }),
        Resampler.Welch => (1.0, static x =>
        {
            var a = Math.Abs(x);
            return a < 1.0 ? 1.0 - a * a : 0.0;
        }),
        Resampler.Robidoux => (2.0, Cubic(0.37821575509399867, 0.31089212245300067)),
        Resampler.RobidouxSharp => (2.0, Cubic(0.2620145123990142, 0.3689927438004929)),
        Resampler.Lanczos2 => (2.0, Lanczos(2.0)),
        Resampler.Lanczos5 => (5.0, Lanczos(5.0)),
        Resampler.Lanczos8 => (8.0, Lanczos(8.0)),
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
