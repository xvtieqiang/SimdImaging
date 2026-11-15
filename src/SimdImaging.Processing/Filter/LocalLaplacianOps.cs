using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Local Laplacian filtering (Paris et al., 2011): a Laplacian pyramid whose coefficients are recomputed
/// through a pointwise remap centred on each pixel's own intensity, which is what lets it boost local
/// detail without the haloing a plain unsharp mask produces.
/// <para>
/// <b>Structure.</b> A Gaussian pyramid of the input provides, at every level, the intensity that selects
/// the sample — using the full-resolution value for the coarse levels picks the wrong sample and shows up
/// as banding. For each sampled intensity <c>v</c> the remapped image <c>r(I − v) + v</c> is downsampled
/// level by level; each level's coefficient is <c>R_l − U(R_{l+1})</c>, written into the output Laplacian
/// pyramid only at pixels whose nearest sample is <c>v</c>. The collapse is <c>out_l = L_l + U(out_{l+1})</c>.
/// Analysis and synthesis share <see cref="Pyramid.Upsample"/>, so the two cannot disagree.
/// </para>
/// <para>
/// <b>Ranges and alpha.</b> Only RGB is processed; alpha is copied through byte for byte. The sample range
/// is <c>[0,1]</c> for the integer formats and <c>[min,max]</c> of the image for <c>Rgba32F</c> (an [0,1]
/// grid would miss every HDR value). Working planes are float, so no level is re-quantised to 8 bits.
/// </para>
/// <para>
/// <b>Memory.</b> Levels are stored at their own size (4/3 of one image, not one image per level).
/// <see cref="MaxScratchBytes"/> is a hard ceiling: exceeding it throws instead of letting the process run
/// out. <c>ProcessingPlan.MaxWorkingBytes</c> does not see an operator's internal scratch, which is exactly
/// why this operator carries its own limit.
/// </para>
/// </summary>
public static class LocalLaplacianOps
{
    /// <summary>Hard ceiling on the working set of one call (planes + scratch).</summary>
    public const long MaxScratchBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Test hook: computes the remap per pixel instead of through the code-value table. Both
    /// paths must produce identical bytes; this exists so the table can be proven equal to the direct form.</summary>
    internal static bool ForceDirectRemap;

    /// <summary>Local Laplacian detail enhancement.</summary>
    /// <param name="source">The input pixels.</param>
    /// <param name="levels">Requested Laplacian levels; clamped so the coarsest level keeps at least eight
    /// pixels on its short side (small images must not fail on the default).</param>
    /// <param name="samples">How many intensities the remap is evaluated at; more is smoother and slower.</param>
    /// <param name="alpha">Detail exponent inside <c>±sigmaR</c>; 1 is identity, below 1 amplifies detail.</param>
    /// <param name="beta">Amplitude of the edge branch beyond <c>sigmaR</c>; 1 is identity.</param>
    /// <param name="sigmaR">Range split between "detail" and "edge", in sample units; must be positive.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of range.</exception>
    /// <exception cref="InvalidOperationException">The estimated working set exceeds <see cref="MaxScratchBytes"/>.</exception>
    public static WorkingBuffer LocalLaplacian(PixelView source, int levels = 5, int samples = 32,
        float alpha = 0.25f, float beta = 1f, float sigmaR = 0.15f, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(levels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);
        if (!(sigmaR > 0f) || !float.IsFinite(sigmaR))
            throw new ArgumentOutOfRangeException(nameof(sigmaR), sigmaR, "The range split must be finite and positive.");
        if (!(alpha >= 0f) || !float.IsFinite(alpha))
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "The detail exponent must be finite and non-negative.");
        if (!(beta >= 0f) || !float.IsFinite(beta))
            throw new ArgumentOutOfRangeException(nameof(beta), beta, "The edge amplitude must be finite and non-negative.");

        var width = source.Width;
        var height = source.Height;

        // Clamp, do not throw: a 16×16 image with the default levels must still run.
        var effectiveLevels = levels;
        while (effectiveLevels > 1 && (Math.Min(width, height) >> effectiveLevels) < 4)
            effectiveLevels--;

        var levelCount = effectiveLevels + 1;
        var widths = new int[levelCount];
        var heights = new int[levelCount];
        widths[0] = width;
        heights[0] = height;
        for (var l = 1; l < levelCount; l++)
        {
            widths[l] = Pyramid.DownsampledLength(widths[l - 1]);
            heights[l] = Pyramid.DownsampledLength(heights[l - 1]);
        }

        var estimate = EstimateBytes(widths, heights, levelCount, samples);
        if (estimate > MaxScratchBytes)
            throw new InvalidOperationException(
                $"Local Laplacian would need an estimated {estimate} bytes, above the {MaxScratchBytes}-byte limit.");

        var byteCount = (long)width * height;
        if (byteCount > int.MaxValue / 4)
            throw new InvalidOperationException("The image is too large for the pyramid's float planes.");

        var gaussian = new float[3][][];
        var laplacian = new float[3][][];
        for (var c = 0; c < 3; c++)
        {
            gaussian[c] = new float[levelCount][];
            laplacian[c] = new float[effectiveLevels][];
            for (var l = 0; l < levelCount; l++)
                gaussian[c][l] = new float[widths[l] * heights[l]];
            for (var l = 0; l < effectiveLevels; l++)
                laplacian[c][l] = new float[widths[l] * heights[l]];
        }

        // A single level has no Laplacian band to remap; the operator is the identity by definition.
        if (effectiveLevels == 1)
            return Geometry.Transforms.Copy(source, pool);

        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);
        try
        {
            CopyIntoGaussian(source, gaussian, width, height);

            var scratch = new float[Math.Max(widths[0] * heights[1], widths[1] * heights[0])];
            for (var l = 1; l < levelCount; l++)
            {
                Ensure(ref scratch, Math.Max(widths[l] * heights[l - 1], widths[l - 1] * heights[l]));
                for (var c = 0; c < 3; c++)
                    Pyramid.Downsample(gaussian[c][l - 1], widths[l - 1], heights[l - 1],
                        gaussian[c][l], widths[l], heights[l], scratch);
            }

            // The deepest slot holds the ORIGINAL base band, not the remapped one. Collapsing onto the
            // input's residual is what keeps flat regions and overall tone where they were: a remapped
            // residual would shift a constant image by the difference between its value and the nearest
            // sampled intensity.
            for (var c = 0; c < 3; c++)
                Array.Copy(gaussian[c][effectiveLevels - 1], laplacian[c][effectiveLevels - 1],
                    widths[effectiveLevels - 1] * heights[effectiveLevels - 1]);

            var (minimum, maximum) = SampleRange(source.Format, gaussian, width, height);
            var step = samples > 1 ? (maximum - minimum) / (samples - 1) : 0.0;
            var lut = BuildRemapTable(source.Format, samples, minimum, step, alpha, beta, sigmaR);

            // Which sample a pixel belongs to depends on the level's own gaussian plane and the sample grid —
            // neither of which the sample loop changes. Recomputing it inside that loop meant evaluating the
            // same double division 32 times for the same pixel (the default `samples`), and the whole
            // assignment costs about a third more than one level's pixels.
            var assignments = BuildAssignments(gaussian, widths, heights, effectiveLevels, minimum, step,
                samples, out var assignmentOffsets);

            // current holds R_l, next holds R_{l+1}; no swapping, because a swap would hand the level-0
            // remap a level-1-sized buffer on the next sample.
            var current = new float[3][];
            var next = new float[3][];
            var upsampled = new float[3][];
            for (var c = 0; c < 3; c++)
            {
                current[c] = new float[widths[0] * heights[0]];
                next[c] = new float[widths[1] * heights[1]];
                upsampled[c] = new float[widths[0] * heights[0]];
            }

            for (var s = 0; s < samples; s++)
            {
                var centre = minimum + step * s;
                RemapInto(source, gaussian, current, width, height, lut, s, centre, alpha, beta, sigmaR);

                for (var l = 0; l < effectiveLevels; l++)
                {
                    Ensure(ref scratch, Math.Max(widths[l + 1] * heights[l], widths[l] * heights[l + 1]));
                    for (var c = 0; c < 3; c++)
                    {
                        Pyramid.Downsample(current[c], widths[l], heights[l], next[c], widths[l + 1], heights[l + 1], scratch);
                        Pyramid.Upsample(next[c], widths[l + 1], heights[l + 1], upsampled[c], widths[l], heights[l], scratch);

                        if (l == effectiveLevels - 1)
                            continue; // the base band is the original residual, set before the sample loop

                        var coefficient = laplacian[c][l];
                        if (assignments is null)
                        {
                            var reference = gaussian[c][l];
                            for (var p = 0; p < coefficient.Length; p++)
                            {
                                if (NearestIndex(reference[p], minimum, step, samples) == s)
                                    coefficient[p] = current[c][p] - upsampled[c][p];
                            }
                        }
                        else
                        {
                            var offset = assignmentOffsets[(c * effectiveLevels) + l];
                            for (var p = 0; p < coefficient.Length; p++)
                            {
                                if (assignments[offset + p] == s)
                                    coefficient[p] = current[c][p] - upsampled[c][p];
                            }
                        }
                    }

                    if (l + 1 < effectiveLevels)
                    {
                        var length = widths[l + 1] * heights[l + 1];
                        for (var c = 0; c < 3; c++)
                            Array.Copy(next[c], current[c], length);
                    }
                }
            }

            Collapse(laplacian, effectiveLevels, widths, heights);

            WriteOut(destination, source, laplacian[0][0], laplacian[1][0], laplacian[2][0]);
        }
        catch
        {
            destination.Dispose();
            throw;
        }

        return destination;
    }

    private static void Ensure(ref float[] buffer, int length)
    {
        if (buffer.Length < length)
            buffer = new float[length];
    }

    private static long EstimateBytes(int[] widths, int[] heights, int levelCount, int samples)
    {
        long bytes = 0;
        for (var l = 0; l < levelCount; l++)
            bytes += 4L * widths[l] * heights[l];

        bytes += 12L * widths[0] * heights[0];

        // The per-pixel sample assignment, when one is built. Reporting less than the truth here would let a
        // call through the ceiling and then allocate above it anyway.
        if (samples <= MaxByteIndex)
            for (var l = 0; l < levelCount; l++)
                bytes += 3L * widths[l] * heights[l];

        return bytes;
    }

    private static void CopyIntoGaussian(PixelView source, float[][][] gaussian, int width, int height)
    {
        var row = new float[width * 4];
        for (var y = 0; y < height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            var offset = y * width;
            for (var x = 0; x < width; x++)
            {
                gaussian[0][0][offset + x] = row[x * 4];
                gaussian[1][0][offset + x] = row[x * 4 + 1];
                gaussian[2][0][offset + x] = row[x * 4 + 2];
            }
        }
    }

    private static (double Minimum, double Maximum) SampleRange(WorkingFormat format, float[][][] gaussian,
        int width, int height)
    {
        if (format != WorkingFormat.Rgba32F)
            return (0.0, 1.0);

        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        for (var c = 0; c < 3; c++)
        {
            var plane = gaussian[c][0];
            for (var i = 0; i < width * height; i++)
            {
                var value = plane[i];
                if (double.IsNaN(value)) continue;
                if (value < minimum) minimum = value;
                if (value > maximum) maximum = value;
            }
        }

        if (double.IsInfinity(minimum) || double.IsInfinity(maximum))
            return (0.0, 1.0);

        if (maximum - minimum < 1e-12)
            maximum = minimum + 1e-6;

        return (minimum, maximum);
    }

    private static float[]? BuildRemapTable(WorkingFormat format, int samples, double minimum, double step,
        float alpha, float beta, float sigmaR)
    {
        if (ForceDirectRemap)
            return null;

        var size = format switch
        {
            WorkingFormat.Rgba8 => Pixels.MaxCode8 + 1,
            WorkingFormat.Rgba16 => Pixels.MaxCode16 + 1,
            _ => 0,
        };

        if (size == 0)
            return null;

        // Indexed by code value, not by normalised float: the table must reproduce exactly the float the
        // direct path would hand to Remap, including the 1/255 multiplication.
        var maxCode = size - 1;
        var table = new float[samples * size];
        for (var s = 0; s < samples; s++)
        {
            var centre = minimum + step * s;
            var offset = s * size;
            for (var code = 0; code < size; code++)
                table[offset + code] = (float)Remap(code * (1f / maxCode), centre, alpha, beta, sigmaR);
        }

        return table;
    }

    private static void RemapInto(PixelView source, float[][][] gaussian, float[][] current, int width, int height,
        float[]? table, int sample, double centre, float alpha, float beta, float sigmaR)
    {
        if (table is null)
        {
            for (var c = 0; c < 3; c++)
            {
                var plane = gaussian[c][0];
                var target = current[c];
                for (var i = 0; i < width * height; i++)
                    target[i] = (float)Remap(plane[i], centre, alpha, beta, sigmaR);
            }

            return;
        }

        var size = source.Format == WorkingFormat.Rgba8 ? Pixels.MaxCode8 + 1 : Pixels.MaxCode16 + 1;
        var offset = sample * size;
        if (source.Format == WorkingFormat.Rgba8)
        {
            for (var y = 0; y < height; y++)
            {
                var codes = source.Row(y);
                var at = y * width;
                for (var x = 0; x < width; x++)
                {
                    current[0][at + x] = table[offset + codes[x * 4]];
                    current[1][at + x] = table[offset + codes[x * 4 + 1]];
                    current[2][at + x] = table[offset + codes[x * 4 + 2]];
                }
            }
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                var codes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(source.Row(y));
                var at = y * width;
                for (var x = 0; x < width; x++)
                {
                    current[0][at + x] = table[offset + codes[x * 4]];
                    current[1][at + x] = table[offset + codes[x * 4 + 1]];
                    current[2][at + x] = table[offset + codes[x * 4 + 2]];
                }
            }
        }
    }

    /// <summary>The Paris remap: identity at <c>alpha = beta = 1</c>.</summary>
    private static double Remap(double value, double centre, float alpha, float beta, float sigmaR)
    {
        var difference = value - centre;
        var magnitude = Math.Abs(difference);
        if (magnitude == 0.0)
            return centre;

        var sign = Math.Sign(difference);
        if (magnitude <= sigmaR)
            return centre + sign * sigmaR * Math.Pow(magnitude / sigmaR, alpha);

        return centre + sign * (sigmaR + (double)beta * (magnitude - sigmaR));
    }

    /// <summary>Largest sample count a byte can index.</summary>
    private const int MaxByteIndex = 256;

    /// <summary>
    /// Assigns every (channel, level, pixel) to its nearest sampled intensity, once.
    /// </summary>
    /// <remarks>
    /// Returns null when <paramref name="samples"/> does not fit in a byte; the caller then keeps the
    /// per-pixel path rather than growing to two bytes per pixel for a case that never appears in practice.
    /// The values are the same <see cref="NearestIndex"/> results the loop used to recompute, so nothing
    /// downstream can tell the difference.
    /// </remarks>
    private static byte[]? BuildAssignments(float[][][] gaussian, int[] widths, int[] heights, int levelCount,
        double minimum, double step, int samples, out int[] offsets)
    {
        if (samples > MaxByteIndex)
        {
            offsets = [];
            return null;
        }

        offsets = new int[3 * levelCount];
        long total = 0;
        for (var c = 0; c < 3; c++)
        {
            for (var l = 0; l < levelCount; l++)
            {
                offsets[(c * levelCount) + l] = (int)total;
                total += (long)widths[l] * heights[l];
            }
        }

        var table = new byte[total];
        for (var c = 0; c < 3; c++)
        {
            for (var l = 0; l < levelCount; l++)
            {
                var offset = offsets[(c * levelCount) + l];
                var plane = gaussian[c][l];
                for (var p = 0; p < plane.Length; p++)
                    table[offset + p] = (byte)NearestIndex(plane[p], minimum, step, samples);
            }
        }

        return table;
    }

    private static int NearestIndex(double value, double minimum, double step, int samples)
    {
        if (samples <= 1 || !(step > 0.0))
            return 0;

        var index = (int)Math.Round((value - minimum) / step);
        if (index < 0) return 0;
        if (index >= samples) return samples - 1;
        return index;
    }

    private static void Collapse(float[][][] laplacian, int levels, int[] widths, int[] heights)
    {
        if (levels <= 1)
            return;

        for (var l = levels - 2; l >= 0; l--)
        {
            var scratch = new float[widths[l] * heights[l + 1]];
            var upsampled = new float[widths[l] * heights[l]];
            for (var c = 0; c < 3; c++)
            {
                Pyramid.Upsample(laplacian[c][l + 1], widths[l + 1], heights[l + 1], upsampled, widths[l], heights[l], scratch);
                var target = laplacian[c][l];
                for (var i = 0; i < target.Length; i++)
                    target[i] += upsampled[i];
            }
        }
    }

    private static void WriteOut(WorkingBuffer destination, PixelView source, float[] r, float[] g, float[] b)
    {
        var width = source.Width;
        var row = new float[width * 4];
        var original = new float[width * 4];
        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, original);
            for (var x = 0; x < width; x++)
            {
                var at = y * width + x;
                row[x * 4] = r[at];
                row[x * 4 + 1] = g[at];
                row[x * 4 + 2] = b[at];
                row[x * 4 + 3] = original[x * 4 + 3];
            }

            Pixels.WriteRowRaw(row, width, destination.Format, destination.View.Row(y));
        }
    }
}
