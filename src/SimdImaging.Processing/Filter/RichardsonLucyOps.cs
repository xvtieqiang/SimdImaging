using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Richardson–Lucy deconvolution. The observed image is explained as a sharp estimate convolved with a known
/// point-spread function; each iteration re-blurs the estimate, compares it with the observation, and scales
/// the estimate by the blurred ratio.
/// <para><b>It runs on straight RGB and copies alpha through untouched.</b> The update is multiplicative, not a
/// linear combination, so premultiplying first would deconvolve the coverage instead of the colour: a
/// half-transparent edge would ring in alpha rather than in red. Every linear filter in this library works
/// premultiplied; this one deliberately does not, and the reason is the ratio.</para>
/// <para><b>Stopping is the iteration count.</b> An automatic "relative change below ε" test is a whole-image
/// reduction, so under band parallelism the number of iterations would depend on how the bands were split —
/// and a different iteration count is a different answer. The documented default is the measured PSNR peak,
/// not a guess: beyond it the operator keeps sharpening noise.</para>
/// <para>Cost is <c>2 · taps · iterations</c> multiply-adds per channel sample. The Gaussian PSF is separable
/// (<c>2·(6σ+1)</c> taps); the motion PSF is applied as a sparse tap list of about <c>length</c> entries.</para>
/// </summary>
public static class RichardsonLucyOps
{
    /// <summary>Ratio floor. Keeps a black region (prediction 0) from dividing by zero without visibly
    /// damping the update: 1e-6 is a quarter of one 8-bit code.</summary>
    private const float Epsilon = 1e-6f;

    /// <summary>Hard ceiling on the four float planes; 8K Rgba32F would otherwise ask for 1.6 GiB silently.</summary>
    private const long MaxScratchBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Deconvolves a Gaussian blur of the given sigma.
    /// </summary>
    /// <param name="source">The blurred pixels.</param>
    /// <param name="sigma">Standard deviation of the blur that is being undone, in pixels.</param>
    /// <param name="iterations">Richardson–Lucy iterations; 0 returns a copy.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is not finite and positive, or
    /// <paramref name="iterations"/> is negative.</exception>
    public static WorkingBuffer RichardsonLucy(PixelView source, float sigma, int iterations = 10,
        IPixelPool? pool = null)
    {
        if (!(sigma > 0f) || float.IsInfinity(sigma))
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be finite and greater than 0.");

        ArgumentOutOfRangeException.ThrowIfNegative(iterations);
        if (iterations == 0)
            return Geometry.Transforms.Copy(source, pool);

        return Run(source, iterations, GaussianKernel(sigma), null, pool);
    }

    /// <summary>
    /// Deconvolves the trail <see cref="FilterOps.MotionBlur"/> produces with the same length and angle: the
    /// point-spread function is built from the identical integer offsets, so the pair is an actual inverse
    /// pair rather than two similar kernels.
    /// </summary>
    /// <param name="source">The blurred pixels.</param>
    /// <param name="length">Trail length in pixels, at least 1 (1 is the identity).</param>
    /// <param name="angleDegrees">Trail direction, clockwise from +x, in a y-down system.</param>
    /// <param name="iterations">Richardson–Lucy iterations; 0 returns a copy.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive,
    /// <paramref name="angleDegrees"/> is not finite, or <paramref name="iterations"/> is negative.</exception>
    public static WorkingBuffer RichardsonLucyMotion(PixelView source, int length, float angleDegrees,
        int iterations = 10, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!float.IsFinite(angleDegrees))
            throw new ArgumentOutOfRangeException(nameof(angleDegrees), angleDegrees, "Angle must be finite.");

        ArgumentOutOfRangeException.ThrowIfNegative(iterations);
        if (length == 1 || iterations == 0)
            return Geometry.Transforms.Copy(source, pool);

        return Run(source, iterations, null, MotionKernel(length, angleDegrees), pool);
    }

    /// <summary>The PSF radius: how far one convolution reaches from an output sample.</summary>
    /// <summary>
    /// Bytes one call allocates: four float planes per channel (twelve planes) plus the row buffer. Declare
    /// it on a plan step (<c>Add(..., scratch: RichardsonLucyOps.ScratchBytes)</c>) or
    /// <c>MemoryEstimator</c> will report a working set that is short by forty-eight bytes a pixel.
    /// </summary>
    /// <param name="width">Band width the operator will see.</param>
    /// <param name="height">Band height the operator will see.</param>
    /// <param name="format">Unused: the planes are float whatever the working format is.</param>
    public static long ScratchBytes(int width, int height, WorkingFormat format)
    {
        var pixels = (long)width * height;
        return 12 * pixels * sizeof(float) + 4L * width * sizeof(float);
    }

    internal static int RadiusOf(float sigma) => Math.Max(1, (int)MathF.Ceiling(3f * sigma));

    /// <summary>Upper bound on the PSF radius of a motion trail, for the same purpose.</summary>
    internal static int RadiusOfMotion(int length) => (int)Math.Ceiling((length - 1) * 0.5);

    /// <summary>
    /// The halo a band executor must declare when this operator runs as a plan step. It is
    /// <c>2 × PSF radius × iterations</c>: one iteration convolves the estimate with the PSF and then convolves
    /// the ratio back, so the output at one pixel depends on the input within two radii; each further iteration
    /// widens that by another two. The earlier <c>radius × iterations</c> was short by a factor of two and left a
    /// one-bit seam at band boundaries, which a fused-versus-stepwise comparison cannot see because both sides use
    /// the same windows.
    /// </summary>
    /// <param name="sigma">Same sigma the call will use.</param>
    /// <param name="iterations">Same iteration count the call will use.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is not finite and positive, or
    /// <paramref name="iterations"/> is negative.</exception>
    public static int RequiredHalo(float sigma, int iterations)
    {
        if (!(sigma > 0f) || float.IsInfinity(sigma))
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be finite and greater than 0.");

        ArgumentOutOfRangeException.ThrowIfNegative(iterations);
        return 2 * RadiusOf(sigma) * iterations;
    }

    /// <summary>The same computation for a motion PSF.</summary>
    /// <param name="length">Same trail length the call will use.</param>
    /// <param name="iterations">Same iteration count the call will use.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive, or
    /// <paramref name="iterations"/> is negative.</exception>
    public static int RequiredHaloMotion(int length, int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfNegative(iterations);
        return 2 * RadiusOfMotion(length) * iterations;
    }

    /// <summary>Gaussian taps, normalized in double then truncated once to float.</summary>
    private static float[] GaussianKernel(float sigma)
    {
        var radius = RadiusOf(sigma);
        var kernel = new float[radius * 2 + 1];
        var sum = 0.0;
        for (var i = -radius; i <= radius; i++)
            sum += Math.Exp(-(i * (double)i) / (2.0 * sigma * sigma));

        for (var i = -radius; i <= radius; i++)
            kernel[i + radius] = (float)(Math.Exp(-(i * (double)i) / (2.0 * sigma * sigma)) / sum);

        return kernel;
    }

    /// <summary>
    /// The same integer offsets <see cref="FilterOps.MotionBlur"/> averages over, counted and normalised: a
    /// sample whose rounded position repeats another's adds weight rather than a new tap. Flattened in
    /// (dy, dx) order so the accumulation order is fixed. The set is symmetric about the centre (rounding is
    /// sign-symmetric), which is what lets the ratio be convolved with the kernel itself instead of its mirror.
    /// </summary>
    private static SparseTap[] MotionKernel(int length, float angleDegrees)
    {
        var radians = angleDegrees * MathF.PI / 180f;
        var dx = MathF.Cos(radians);
        var dy = MathF.Sin(radians);
        var half = (length - 1) / 2f;
        var counts = new Dictionary<(int X, int Y), int>(length);

        for (var i = 0; i < length; i++)
        {
            var t = i - half;
            var offset = ((int)MathF.Round(t * dx), (int)MathF.Round(t * dy));
            counts[offset] = counts.GetValueOrDefault(offset) + 1;
        }

        var taps = new List<SparseTap>(counts.Count);
        foreach (var ((x, y), count) in counts.OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X))
            taps.Add(new SparseTap(x, y, (float)count / length));

        return [.. taps];
    }

    private static WorkingBuffer Run(PixelView source, int iterations, float[]? separable, SparseTap[]? sparse,
        IPixelPool? pool)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = width * height;
        // Four planes per channel (input, estimate, prediction, correction).
        var planes = 12L * pixels * sizeof(float);
        if (planes > MaxScratchBytes)
            throw new InvalidOperationException(
                $"Richardson-Lucy needs {planes / (1024.0 * 1024.0):F0} MiB of float planes for " +
                $"{width}x{height}, over the {MaxScratchBytes / (1024 * 1024)} MiB cap. Downscale first.");

        var input = new[] { new float[pixels], new float[pixels], new float[pixels] };
        var estimate = new[] { new float[pixels], new float[pixels], new float[pixels] };
        var predicted = new[] { new float[pixels], new float[pixels], new float[pixels] };
        var correction = new[] { new float[pixels], new float[pixels], new float[pixels] };

        var row = new float[width * 4];
        for (var y = 0; y < height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            var offset = y * width;
            for (var x = 0; x < width; x++)
            {
                // RL assumes non-negative radiance. 8- and 16-bit input cannot violate that; a float image can.
                var r = Math.Max(0f, row[x * 4]);
                var g = Math.Max(0f, row[x * 4 + 1]);
                var b = Math.Max(0f, row[x * 4 + 2]);
                input[0][offset + x] = r;
                input[1][offset + x] = g;
                input[2][offset + x] = b;
                estimate[0][offset + x] = r;
                estimate[1][offset + x] = g;
                estimate[2][offset + x] = b;
            }
        }

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            for (var c = 0; c < 3; c++)
            {
                Convolve(estimate[c], predicted[c], width, height, separable, sparse);

                var observation = input[c];
                var ratio = predicted[c];
                for (var i = 0; i < pixels; i++)
                    ratio[i] = (observation[i] + Epsilon) / (ratio[i] + Epsilon);
            }

            for (var c = 0; c < 3; c++)
            {
                Convolve(predicted[c], correction[c], width, height, separable, sparse);
                var plane = estimate[c];
                var scale = correction[c];
                for (var i = 0; i < pixels; i++)
                    plane[i] *= scale[i];
            }
        }

        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);
        try
        {
            for (var y = 0; y < height; y++)
            {
                Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
                var offset = y * width;
                for (var x = 0; x < width; x++)
                {
                    row[x * 4] = estimate[0][offset + x];
                    row[x * 4 + 1] = estimate[1][offset + x];
                    row[x * 4 + 2] = estimate[2][offset + x];
                }

                Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
            }
        }
        catch
        {
            destination.Dispose();
            throw;
        }

        return destination;
    }

    private static void Convolve(float[] source, float[] destination, int width, int height,
        float[]? separable, SparseTap[]? sparse)
    {
        if (separable is not null)
            PlanarConvolve.Separable(source, destination, width, height, separable);
        else
            PlanarConvolve.Sparse(source, destination, width, height, sparse!);
    }
}
