using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>How far two images are apart, in normalised sample units.</summary>
/// <param name="MaxDifference">Largest per-pixel channel difference, 0..1.</param>
/// <param name="MeanDifference">Mean of the per-pixel worst channel difference, 0..1.</param>
/// <param name="PixelsOverThreshold">Pixels whose worst channel difference exceeds the threshold.</param>
public readonly record struct DiffResult(float MaxDifference, float MeanDifference, long PixelsOverThreshold);

/// <summary>
/// Whole-image measurements that do not produce a new image. Everything is computed in normalised [0,1]
/// sample units, so an 8-bit, 16-bit and float image of the same content compare equal instead of differing
/// by a factor of 257.
/// </summary>
public static class AnalysisOps
{
    /// <summary>
    /// The tight box around pixels whose alpha exceeds <paramref name="threshold"/>. Returns an empty rect
    /// (zero width and height) when nothing qualifies — callers must not assume a non-empty result.
    /// </summary>
    public static PixelRect AlphaBounds(PixelView source, float threshold = 0f)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;

        var row = new float[source.Width * 4];
        for (var y = 0; y < source.Height; y++)
        {
            var first = -1;
            var last = -1;

            if (source.Format == WorkingFormat.Rgba8)
            {
                var bytes = source.Row(y);
                var cutoff = (byte)Math.Clamp(threshold * 255f, 0f, 255f);
                for (var x = 0; x < source.Width; x++)
                {
                    if (bytes[x * 4 + 3] <= cutoff)
                        continue;

                    if (first < 0)
                        first = x;
                    last = x;
                }
            }
            else
            {
                Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
                for (var x = 0; x < source.Width; x++)
                {
                    if (!(row[x * 4 + 3] > threshold))
                        continue;

                    if (first < 0)
                        first = x;
                    last = x;
                }
            }

            if (first < 0)
                continue;

            minX = Math.Min(minX, first);
            maxX = Math.Max(maxX, last);
            minY = Math.Min(minY, y);
            maxY = y;
        }

        if (maxX < 0)
            return new PixelRect(0, 0, 0, 0);

        return new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// Peak signal-to-noise ratio over the colour channels, in dB, with the peak fixed at 1. Identical images
    /// return <see cref="double.PositiveInfinity"/> rather than a finite cap.
    /// </summary>
    /// <exception cref="ArgumentException">The images have different dimensions.</exception>
    public static double Psnr(PixelView a, PixelView b)
    {
        EnsureSameSize(a, b);

        double squared = 0;
        var rowA = new float[a.Width * 4];
        var rowB = new float[a.Width * 4];

        for (var y = 0; y < a.Height; y++)
        {
            Pixels.ReadRowRaw(a.Row(y), a.Width, a.Format, rowA);
            Pixels.ReadRowRaw(b.Row(y), b.Width, b.Format, rowB);

            for (var i = 0; i < rowA.Length; i += 4)
            {
                var dr = (double)rowA[i] - rowB[i];
                var dg = (double)rowA[i + 1] - rowB[i + 1];
                var db = (double)rowA[i + 2] - rowB[i + 2];
                squared += dr * dr + dg * dg + db * db;
            }
        }

        var mean = squared / ((double)a.Width * a.Height * 3);
        return mean <= 0 ? double.PositiveInfinity : 10 * Math.Log10(1.0 / mean);
    }

    /// <summary>Absolute differences between two images: the worst channel, the mean, and how many pixels
    /// exceed <paramref name="threshold"/>.</summary>
    /// <param name="a">The first image.</param>
    /// <param name="b">The second image.</param>
    /// <param name="threshold">Per-pixel cutoff for <see cref="DiffResult.PixelsOverThreshold"/>, 0..1.</param>
    /// <exception cref="ArgumentException">The images have different dimensions.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is not positive.</exception>
    public static DiffResult Diff(PixelView a, PixelView b, float threshold = 1f / 255f)
    {
        EnsureSameSize(a, b);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);

        float maximum = 0f;
        double sum = 0;
        long over = 0;

        var rowA = new float[a.Width * 4];
        var rowB = new float[a.Width * 4];

        for (var y = 0; y < a.Height; y++)
        {
            Pixels.ReadRowRaw(a.Row(y), a.Width, a.Format, rowA);
            Pixels.ReadRowRaw(b.Row(y), b.Width, b.Format, rowB);

            for (var i = 0; i < rowA.Length; i += 4)
            {
                var dr = MathF.Abs(rowA[i] - rowB[i]);
                var dg = MathF.Abs(rowA[i + 1] - rowB[i + 1]);
                var db = MathF.Abs(rowA[i + 2] - rowB[i + 2]);
                var worst = MathF.Max(dr, MathF.Max(dg, db));

                sum += worst;
                if (worst > maximum)
                    maximum = worst;
                if (worst > threshold)
                    over++;
            }
        }

        var pixels = (double)a.Width * a.Height;
        return new DiffResult(maximum, (float)(sum / pixels), over);
    }

    /// <summary>
    /// The <paramref name="count"/> colours that cover most of the image, most used first. The palette comes
    /// from median cut, and each entry is then replaced by the mean of the pixels actually assigned to it, so
    /// the answer is the image's colour, not the quantiser's bin centre.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="count">How many colours to return, 1..256.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 1..256.</exception>
    public static RgbaColor[] DominantColors(PixelView source, int count = 8)
    {
        if (count is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be 1..256.");

        var palette = PaletteQuantizer.Build(source, Math.Max(2, count));
        var lookup = PaletteQuantizer.BuildLookup(palette);
        var entries = palette.Count;
        var sums = new long[entries * 4];

        var row = new float[source.Width * 4];
        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
            for (var x = 0; x < source.Width; x++)
            {
                var offset = x * 4;
                var bin = (ToByte(row[offset]) >> 3) << 10 | (ToByte(row[offset + 1]) >> 3) << 5 | (ToByte(row[offset + 2]) >> 3);
                var index = lookup[bin] * 4;
                sums[index] += ToByte(row[offset]);
                sums[index + 1] += ToByte(row[offset + 1]);
                sums[index + 2] += ToByte(row[offset + 2]);
                sums[index + 3]++;
            }
        }

        var order = new int[entries];
        for (var i = 0; i < entries; i++)
            order[i] = i;

        Array.Sort(order, (a, b) =>
        {
            var delta = sums[b * 4 + 3].CompareTo(sums[a * 4 + 3]);
            return delta != 0 ? delta : a.CompareTo(b);
        });

        var result = new RgbaColor[Math.Min(count, entries)];
        for (var i = 0; i < result.Length; i++)
        {
            var index = order[i] * 4;
            var pixels = sums[index + 3];
            result[i] = pixels == 0
                ? palette[order[i]]
                : new RgbaColor(
                    (byte)((sums[index] + pixels / 2) / pixels),
                    (byte)((sums[index + 1] + pixels / 2) / pixels),
                    (byte)((sums[index + 2] + pixels / 2) / pixels),
                    255);
        }

        return result;
    }

    private static byte ToByte(float value)
    {
        var scaled = value * 255f + 0.5f;
        if (scaled <= 0f) return 0;
        if (scaled >= 255f) return 255;
        return (byte)scaled;
    }

    /// <summary>
    /// Structural similarity over an 11×11 Gaussian window (σ 1.5), on BT.601 luma. 1 means identical.
    /// <para>
    /// The moments are produced row by row through five ring buffers, so the working set is a few rows rather
    /// than five full planes — a 4K pair costs under a megabyte of scratch, not a quarter of a gigabyte.
    /// </para>
    /// </summary>
    /// <param name="a">The first image.</param>
    /// <param name="b">The second image.</param>
    /// <exception cref="ArgumentException">The images have different dimensions.</exception>
    public static double Ssim(PixelView a, PixelView b)
    {
        EnsureSameSize(a, b);

        const int Radius = 5;
        const int Window = Radius * 2 + 1;
        const float C1 = 0.01f * 0.01f;
        const float C2 = 0.03f * 0.03f;

        var kernel = GaussianKernel(1.5f, Radius);
        var width = a.Width;
        var height = a.Height;

        // Five quantities: luma A, luma B, A², B², A·B.
        var ring = new float[5][];
        for (var i = 0; i < 5; i++)
            ring[i] = new float[Window * width];

        var horizontal = new float[5 * width];
        var rowA = new float[width * 4];
        var rowB = new float[width * 4];

        double total = 0;

        for (var y = 0; y < height; y++)
        {
            Pixels.ReadRowRaw(a.Row(y), width, a.Format, rowA);
            Pixels.ReadRowRaw(b.Row(y), width, b.Format, rowB);

            for (var x = 0; x < width; x++)
            {
                float s0 = 0f, s1 = 0f, s2 = 0f, s3 = 0f, s4 = 0f;
                for (var k = 0; k < Window; k++)
                {
                    var sx = Math.Clamp(x + k - Radius, 0, width - 1);
                    var offset = sx * 4;
                    var la = 0.299f * rowA[offset] + 0.587f * rowA[offset + 1] + 0.114f * rowA[offset + 2];
                    var lb = 0.299f * rowB[offset] + 0.587f * rowB[offset + 1] + 0.114f * rowB[offset + 2];
                    var weight = kernel[k];

                    s0 += weight * la;
                    s1 += weight * lb;
                    s2 += weight * la * la;
                    s3 += weight * lb * lb;
                    s4 += weight * la * lb;
                }

                horizontal[x] = s0;
                horizontal[width + x] = s1;
                horizontal[2 * width + x] = s2;
                horizontal[3 * width + x] = s3;
                horizontal[4 * width + x] = s4;
            }

            var slot = y % Window;
            for (var i = 0; i < 5; i++)
                horizontal.AsSpan(i * width, width).CopyTo(ring[i].AsSpan(slot * width, width));

            if (y >= Radius)
                total += RowSsim(ring, kernel, width, y - Radius, height, Radius, C1, C2);
        }

        // The last `Radius` output rows still need their window, which now only repeats the final row.
        for (var y = height; y < height + Radius; y++)
            total += RowSsim(ring, kernel, width, y - Radius, height, Radius, C1, C2);

        return total / ((double)width * height);
    }

    private static double RowSsim(float[][] ring, float[] kernel, int width, int outputRow, int height, int radius,
        float c1, float c2)
    {
        var window = radius * 2 + 1;
        double sum = 0;

        for (var x = 0; x < width; x++)
        {
            float m0 = 0f, m1 = 0f, m2 = 0f, m3 = 0f, m4 = 0f;

            for (var k = 0; k < window; k++)
            {
                var sourceRow = Math.Clamp(outputRow - radius + k, 0, height - 1);
                var slot = sourceRow % window;
                var weight = kernel[k];
                var index = slot * width + x;

                m0 += weight * ring[0][index];
                m1 += weight * ring[1][index];
                m2 += weight * ring[2][index];
                m3 += weight * ring[3][index];
                m4 += weight * ring[4][index];
            }

            var varianceA = m2 - m0 * m0;
            var varianceB = m3 - m1 * m1;
            var covariance = m4 - m0 * m1;

            var numerator = (2f * m0 * m1 + c1) * (2f * covariance + c2);
            var denominator = (m0 * m0 + m1 * m1 + c1) * (varianceA + varianceB + c2);
            sum += denominator == 0f ? 0 : numerator / denominator;
        }

        return sum;
    }

    private static float[] GaussianKernel(float sigma, int radius)
    {
        var kernel = new float[radius * 2 + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var weight = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            kernel[i + radius] = weight;
            sum += weight;
        }

        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;
        return kernel;
    }

    private static void EnsureSameSize(PixelView a, PixelView b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new ArgumentException($"Images differ in size: {a.Width}x{a.Height} vs {b.Width}x{b.Height}.");
    }
}
