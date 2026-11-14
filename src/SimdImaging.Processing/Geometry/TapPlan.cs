namespace SimdImaging.Processing.Geometry;

/// <summary>One reconstructed axis: the first source tap index and the normalised tap weights.</summary>
internal readonly struct TapPlan
{
    public int Start { get; }
    public float[] Weights { get; }
    public int Count => Weights.Length;

    public TapPlan(int start, float[] weights)
    {
        Start = start;
        Weights = weights;
    }
}

/// <summary>
/// The single definition of reconstruction weights. <c>WeightTable.BuildCore</c> (Resize), the
/// warp/affine reconstruction core and EWA all consume <see cref="Parameters"/> / <see cref="FillAt"/>,
/// so there is exactly one implementation of scale, filter widening, radius, tap count, centre
/// phase, weight normalisation and the degenerate <c>sum == 0</c> fallback.
/// <para>The arithmetic is copied verbatim from the original <c>WeightTable.BuildCore</c> so that
/// switching Resize to this builder cannot change a single output byte.</para>
/// </summary>
internal static class TapPlanBuilder
{
    public static (double Scale, double FilterScale, double Radius, int Taps, Func<double, double> Kernel)
        Parameters(int sourceLength, int destinationLength, Resampler resampler)
    {
        if (sourceLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceLength));
        if (destinationLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationLength));

        var (support, kernel) = ResamplerKernels.Get(resampler);
        var scale = (double)destinationLength / sourceLength;

        // Downscaling widens the kernel so every source pixel contributes (area sampling); upscaling keeps it.
        // Nearest is point sampling at any scale, so its support must not widen.
        var filterScale = resampler == Resampler.Nearest ? 1.0 : (scale < 1.0 ? 1.0 / scale : 1.0);
        var radius = support * filterScale;
        var taps = Math.Max(1, (int)Math.Ceiling(2 * radius) + 2);
        return (scale, filterScale, radius, taps, kernel);
    }

    /// <summary>
    /// Fills normalised weights for one continuous source centre (pixel centre at <c>i + 0.5</c>)
    /// and returns the first tap index. <paramref name="buffer"/> must have <c>Taps</c> entries.
    /// </summary>
    public static int FillAt(Span<double> buffer, double center, int sourceLength,
        double filterScale, double radius, Func<double, double> kernel)
    {
        var start = (int)Math.Ceiling(center - radius - 0.5);
        var sum = 0.0;
        for (var k = 0; k < buffer.Length; k++)
        {
            var x = start + k;
            var distance = (x + 0.5 - center) / filterScale;
            var weight = Math.Abs(x + 0.5 - center) <= radius ? kernel(distance) : 0.0;
            buffer[k] = weight;
            sum += weight;
        }

        if (sum == 0.0)
        {
            // Degenerate (a tap range that fell entirely outside the kernel): use the nearest pixel.
            buffer.Clear();
            var nearest = Math.Clamp((int)Math.Floor(center), 0, sourceLength - 1) - start;
            if ((uint)nearest < (uint)buffer.Length)
                buffer[nearest] = 1.0;
            sum = 1.0;
        }

        for (var k = 0; k < buffer.Length; k++)
            buffer[k] /= sum;
        return start;
    }

    /// <summary>Builds a 1-D plan for an arbitrary continuous source coordinate (the warp path).</summary>
    public static TapPlan ForPoint(double center, int sourceLength, Resampler resampler)
    {
        // A warp does not resample an axis, so scale == 1 and the kernel is not widened.
        var (_, filterScale, radius, taps, kernel) = Parameters(sourceLength, sourceLength, resampler);
        var buffer = new double[taps];
        var start = FillAt(buffer, center, sourceLength, filterScale, radius, kernel);
        var weights = new float[taps];
        for (var k = 0; k < taps; k++)
            weights[k] = (float)buffer[k];
        return new TapPlan(start, weights);
    }
}
