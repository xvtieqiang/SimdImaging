using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Single-image dehazing by the dark-channel prior (He, Sun and Tang, 2009): haze lifts the darkest channel of
/// most natural patches towards the atmospheric light, so how much a patch fails to be dark is a usable estimate
/// of how much haze sits in front of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Steps.</b> The dark channel takes the per-pixel minimum of R, G and B and then the minimum over a patch;
/// the atmospheric light is the mean colour of the brightest 0.1% of that channel; transmission is
/// <c>1 − ω·dark(I/A)</c>, refined by a box filter so the map does not follow every edge in the texture; and the
/// scene is recovered as <c>J = (I − A)/max(t, t0) + A</c>.
/// </para>
/// <para>
/// <b>Why the patch minimum is separable in practice.</b> It is not — a min over a square is only equal to the
/// minimum of the horizontal and vertical minima, which is what this computes. That is exact, not an
/// approximation: <c>min over the square = min over columns of (min over the row)</c>.
/// </para>
/// <para>
/// <b>Determinism.</b> Every loop runs in a fixed order over floats, the patch minimum is order-independent, and
/// the box refinement accumulates in a fixed left-to-right, top-to-bottom order. No random source, no
/// order-dependent tie-break: the output is byte-identical across SIMD tiers because nothing here is vectorised.
/// </para>
/// <para>
/// <b>Memory.</b> Two full-resolution single-channel float planes (dark channel, transmission), plus a
/// <c>2r+1</c>-row ring. At 8K that is about 264 MiB of planes against 126 MiB of image — the operator is a
/// whole-image one and carries no banding.
/// </para>
/// </remarks>
public static class DehazeOps
{
    /// <summary>Dark channel above which a pixel is treated as sky rather than as haze.</summary>
    private const float SkyDarkChannel = 0.70f;

    /// <summary>Transmission a sky pixel keeps, which bounds its amplification to about 1.7×.</summary>
    private const float SkyTransmissionFloor = 0.6f;

    /// <summary>Fraction of the darkest-channel pixels whose colour defines the atmospheric light.</summary>
    private const double AtmosphereFraction = 0.001;

    /// <summary>Removes haze.</summary>
    /// <param name="source">The hazy pixels.</param>
    /// <param name="patchRadius">Radius of the patch the dark channel and transmission are taken over. Larger
    /// keeps more depth detail and costs more; 7 is the paper's value for images around 600 px.</param>
    /// <param name="omega">How much haze to remove; 1 removes it all and usually looks flat, 0.95 keeps a little
    /// depth. Must be in (0, 1].</param>
    /// <param name="tMin">Floor on transmission. Pixels with no transmission estimate left keep this, which
    /// bounds the amplification to <c>1/tMin</c>.</param>
    /// <param name="refineRadius">Radius of the box filter that smooths the transmission map; 0 disables it and
    /// leaves the blocky map the patch minimum produces.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of range.</exception>
    public static WorkingBuffer Dehaze(PixelView source, int patchRadius = 7, float omega = 0.95f,
        float tMin = 0.1f, int refineRadius = 20, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(patchRadius);
        if (!(omega > 0f) || omega > 1f)
            throw new ArgumentOutOfRangeException(nameof(omega), omega, "Omega must be in (0, 1].");
        if (!(tMin > 0f) || tMin > 1f)
            throw new ArgumentOutOfRangeException(nameof(tMin), tMin, "The transmission floor must be in (0, 1].");
        ArgumentOutOfRangeException.ThrowIfNegative(refineRadius);

        var width = source.Width;
        var height = source.Height;
        if (width == 0 || height == 0)
            return WorkingBuffer.Rent(Math.Max(0, width), Math.Max(0, height), source.Format, pool);

        var stride = width * 4;
        var pixels = width * height;

        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);
        var dark = FloatScratch.Rent(pixels);
        var transmission = FloatScratch.Rent(pixels);
        try
        {
            PatchMinimum(source, patchRadius, 0f, 0f, 0f, dark);
            var (atmosphereR, atmosphereG, atmosphereB) = AtmosphericLight(source, dark);
            PatchMinimum(source, patchRadius, atmosphereR, atmosphereG, atmosphereB, transmission);

            for (var i = 0; i < pixels; i++)
                transmission[i] = 1f - (omega * transmission[i]);

            // Sky guard. A bright, flat region has a high dark channel, so the prior reads it as "almost no
            // transmission" and divides by t0 = 0.1 — a tenfold gain applied to an 8-bit gradient, which is what
            // turned an overcast sky into speckle on the first real photograph this was tried on. Where the dark
            // channel is high the pixel is not hazy, it is bright; keeping its transmission up leaves it alone.
            for (var p = 0; p < pixels; p++)
            {
                if (dark[p] > SkyDarkChannel)
                    transmission[p] = MathF.Max(transmission[p], SkyTransmissionFloor);
            }

            if (refineRadius > 0)
            {
                var guide = Luma(source);
                try
                {
                    GuidedRefine(transmission, guide, width, height, refineRadius);
                }
                finally
                {
                    FloatScratch.Return(guide);
                }
            }

            var output = new float[stride];
            var row = new float[stride];
            for (var y = 0; y < height; y++)
            {
                Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
                for (var x = 0; x < width; x++)
                {
                    var t = MathF.Max(transmission[(y * width) + x], tMin);
                    var o = x * 4;
                    output[o] = ((row[o] - atmosphereR) / t) + atmosphereR;
                    output[o + 1] = ((row[o + 1] - atmosphereG) / t) + atmosphereG;
                    output[o + 2] = ((row[o + 2] - atmosphereB) / t) + atmosphereB;
                    output[o + 3] = row[o + 3];
                }

                Pixels.WriteRowRaw(output, width, source.Format, destination.View.Row(y));
            }

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
        finally
        {
            FloatScratch.Return(transmission);
            FloatScratch.Return(dark);
        }
    }

    /// <summary>
    /// Per-pixel minimum over RGB (divided by the atmospheric light when one is given), then the minimum over a
    /// square patch. The square minimum is the minimum of the row minima, so it is computed as a horizontal
    /// window minimum per row followed by a vertical window minimum over the ring of rows.
    /// </summary>
    private static void PatchMinimum(PixelView source, int radius, float ar, float ag, float ab, float[] destination)
    {
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var rows = (radius * 2) + 1;
        var ring = FloatScratch.Rent(rows * width);
        var loaded = new int[rows];
        Array.Fill(loaded, -1);
        var horizontal = FloatScratch.Rent(width);
        var row = new float[stride];
        try
        {
            for (var y = 0; y < height; y++)
            {
                // Fill only the rows the window needs and does not already hold; at the edges the same row is
                // requested repeatedly, which is why the slot remembers which source row it carries.
                for (var k = -radius; k <= radius; k++)
                {
                    var wanted = Math.Clamp(y + k, 0, height - 1);
                    var slot = wanted % rows;
                    if (loaded[slot] == wanted)
                        continue;

                    ReadMinRow(source, wanted, ar, ag, ab, row, horizontal);
                    HorizontalMinimum(horizontal, width, radius);
                    // Slice the source explicitly: FloatScratch hands back a best-fit buffer that may be longer
                    // than the request, and CopyTo would then copy the whole thing into a width-sized span.
                    horizontal.AsSpan(0, width).CopyTo(ring.AsSpan(slot * width, width));
                    loaded[slot] = wanted;
                }

                for (var x = 0; x < width; x++)
                {
                    // Walk the same clamped rows the fill loop walked, rather than every slot in the ring. At the
                    // top and bottom edges several offsets resolve to one row, so the ring has slots that were
                    // never written this row — reading them returns whatever the scratch pool happened to hold,
                    // which is exactly the kind of state that makes two identical calls disagree.
                    var minimum = float.MaxValue;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var rowIndex = Math.Clamp(y + k, 0, height - 1);
                        var value = ring[((rowIndex % rows) * width) + x];
                        if (value < minimum)
                            minimum = value;
                    }

                    destination[(y * width) + x] = minimum;
                }
            }
        }
        finally
        {
            FloatScratch.Return(horizontal);
            FloatScratch.Return(ring);
        }
    }

    /// <summary>Window minimum of one row, in place, with edge replication.</summary>
    private static void HorizontalMinimum(float[] row, int width, int radius)
    {
        if (radius == 0)
            return;

        var copy = new float[width];
        Array.Copy(row, copy, width);
        for (var x = 0; x < width; x++)
        {
            var minimum = float.MaxValue;
            for (var k = -radius; k <= radius; k++)
            {
                var value = copy[Math.Clamp(x + k, 0, width - 1)];
                if (value < minimum)
                    minimum = value;
            }

            row[x] = minimum;
        }
    }

    private static void ReadMinRow(PixelView source, int y, float ar, float ag, float ab, float[] row, Span<float> destination)
    {
        var width = source.Width;
        Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
        var normalise = ar > 0f || ag > 0f || ab > 0f;
        for (var x = 0; x < width; x++)
        {
            var o = x * 4;
            var r = row[o];
            var g = row[o + 1];
            var b = row[o + 2];
            if (normalise)
            {
                r /= ar > 0f ? ar : 1f;
                g /= ag > 0f ? ag : 1f;
                b /= ab > 0f ? ab : 1f;
            }

            destination[x] = MathF.Min(r, MathF.Min(g, b));
        }
    }

    /// <summary>
    /// Mean colour of the brightest <see cref="AtmosphereFraction"/> of the dark channel: a 256-bin histogram
    /// picks the threshold, then the source pixels at or above it are averaged. Deterministic in both steps.
    /// </summary>
    private static (float R, float G, float B) AtmosphericLight(PixelView source, float[] dark)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = width * height;

        Span<int> histogram = stackalloc int[256];
        for (var i = 0; i < pixels; i++)
        {
            var bin = (int)(Math.Clamp(dark[i], 0f, 1f) * 255f);
            histogram[bin]++;
        }

        var wanted = Math.Max(1, (int)(pixels * AtmosphereFraction));
        var threshold = 255;
        var counted = 0;
        for (var bin = 255; bin >= 0; bin--)
        {
            counted += histogram[bin];
            if (counted >= wanted)
            {
                threshold = bin;
                break;
            }
        }

        var cut = threshold / 255f;
        var row = new float[width * 4];
        double sumR = 0, sumG = 0, sumB = 0;
        long count = 0;
        for (var y = 0; y < height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            for (var x = 0; x < width; x++)
            {
                if (dark[(y * width) + x] < cut)
                    continue;

                var o = x * 4;
                sumR += row[o];
                sumG += row[o + 1];
                sumB += row[o + 2];
                count++;
            }
        }

        if (count == 0)
            return (1f, 1f, 1f);

        return ((float)(sumR / count), (float)(sumG / count), (float)(sumB / count));
    }

    /// <summary>Luma of the hazy frame, the guide the transmission is filtered against.</summary>
    private static float[] Luma(PixelView source)
    {
        var width = source.Width;
        var height = source.Height;
        var luma = FloatScratch.Rent(width * height);
        var row = new float[width * 4];
        for (var y = 0; y < height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            for (var x = 0; x < width; x++)
            {
                var o = x * 4;
                luma[(y * width) + x] = (0.299f * row[o]) + (0.587f * row[o + 1]) + (0.114f * row[o + 2]);
            }
        }

        return luma;
    }

    /// <summary>
    /// Guided filter of the transmission plane, with the hazy frame's luma as the guide (He, Sun and Tang use the
    /// same construction in the same paper).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain box filter over the transmission was the first version of this, and on a real photograph it put a
    /// bright halo around every dark object: the sky's low transmission leaked across the silhouette's edge, so the
    /// object was divided by a transmission that belonged to the background and lit up. A guided filter weights the
    /// window by the guide's local variance, so the smoothing stops at the edge that matters.
    /// </para>
    /// <para>
    /// Five box passes over the guide are enough: <c>a = (corr(I,p) − mean(I)·mean(p)) / (var(I) + eps)</c>,
    /// <c>b = mean(p) − a·mean(I)</c>, then <c>q = box(a)·I + box(b)</c>.
    /// </para>
    /// </remarks>
    private static void GuidedRefine(float[] plane, float[] guide, int width, int height, int radius)
    {
        var pixels = width * height;
        var meanI = FloatScratch.Rent(pixels);
        var meanP = FloatScratch.Rent(pixels);
        var corrI = FloatScratch.Rent(pixels);
        var corrIp = FloatScratch.Rent(pixels);
        var coefficientA = FloatScratch.Rent(pixels);
        var coefficientB = FloatScratch.Rent(pixels);
        try
        {
            BoxMean(guide, meanI, width, height, radius);
            BoxMean(plane, meanP, width, height, radius);

            for (var p = 0; p < pixels; p++)
            {
                corrI[p] = guide[p] * guide[p];
                corrIp[p] = guide[p] * plane[p];
            }

            BoxMean(corrI, corrI, width, height, radius);
            BoxMean(corrIp, corrIp, width, height, radius);

            const float epsilon = 1e-3f;
            for (var p = 0; p < pixels; p++)
            {
                var variance = corrI[p] - (meanI[p] * meanI[p]);
                var covariance = corrIp[p] - (meanI[p] * meanP[p]);
                var a = covariance / (variance + epsilon);
                coefficientA[p] = a;
                coefficientB[p] = meanP[p] - (a * meanI[p]);
            }

            BoxMean(coefficientA, coefficientA, width, height, radius);
            BoxMean(coefficientB, coefficientB, width, height, radius);

            for (var p = 0; p < pixels; p++)
                plane[p] = (coefficientA[p] * guide[p]) + coefficientB[p];
        }
        finally
        {
            FloatScratch.Return(coefficientB);
            FloatScratch.Return(coefficientA);
            FloatScratch.Return(corrIp);
            FloatScratch.Return(corrI);
            FloatScratch.Return(meanP);
            FloatScratch.Return(meanI);
        }
    }

    /// <summary>Separable box mean, in place when <paramref name="destination"/> is the source.</summary>
    private static void BoxMean(float[] source, float[] destination, int width, int height, int radius)
    {
        var temporary = FloatScratch.Rent(width * height);
        try
        {
            BoxPass(source, temporary, width, height, radius, horizontal: true);
            BoxPass(temporary, destination, width, height, radius, horizontal: false);
        }
        finally
        {
            FloatScratch.Return(temporary);
        }
    }

    /// <summary>One separable pass of the box mean, with edge replication.</summary>
    private static void BoxPass(float[] source, float[] destination, int width, int height, int radius, bool horizontal)
    {
        var outer = horizontal ? height : width;
        var inner = horizontal ? width : height;
        var scale = 1.0 / ((radius * 2) + 1);
        for (var a = 0; a < outer; a++)
        {
            double sum = 0;
            for (var k = -radius; k <= radius; k++)
                sum += Sample(source, a, Math.Clamp(k, 0, inner - 1), width, horizontal);

            for (var b = 0; b < inner; b++)
            {
                destination[Index(a, b, width, horizontal)] = (float)(sum * scale);
                var leaving = Sample(source, a, Math.Clamp(b - radius, 0, inner - 1), width, horizontal);
                var entering = Sample(source, a, Math.Clamp(b + radius + 1, 0, inner - 1), width, horizontal);
                sum += entering - leaving;
            }
        }
    }

    private static float Sample(float[] plane, int a, int b, int width, bool horizontal) =>
        horizontal ? plane[(a * width) + b] : plane[(b * width) + a];

    private static int Index(int a, int b, int width, bool horizontal) =>
        horizontal ? (a * width) + b : (b * width) + a;
}
