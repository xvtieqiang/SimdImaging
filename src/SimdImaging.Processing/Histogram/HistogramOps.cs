using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Global histogram work. Both operations need a whole-image pass before writing a pixel, so they are
/// single-pass over the source and allocate one lookup table — no per-pixel statistics.
/// </summary>
public static class HistogramOps
{
    /// <summary>Counts the code values of one channel into <paramref name="bins"/> buckets.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="channel">0 = R, 1 = G, 2 = B, 3 = A.</param>
    /// <param name="bins">Bucket count; values are mapped from [0,1] onto 0..bins-1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not 0..3, or <paramref name="bins"/> is not positive.</exception>
    public static int[] Histogram(PixelView source, int channel, int bins = 256)
    {
        if ((uint)channel > 3)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be 0..3.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bins);

        var histogram = new int[bins];
        var scale = bins / 256f;
        var row = new float[source.Width * 4];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
            for (var x = 0; x < source.Width; x++)
            {
                var value = (int)(row[x * 4 + channel] * 255f + 0.5f);
                var bucket = (int)(value * scale);
                if (bucket >= bins)
                    bucket = bins - 1;
                histogram[bucket]++;
            }
        }

        return histogram;
    }

    /// <summary>Minimum and maximum of each colour channel, in one pass.</summary>
    public static (ChannelRange R, ChannelRange G, ChannelRange B) MinMax(PixelView source)
    {
        Span<float> minimum = [1f, 1f, 1f];
        Span<float> maximum = [0f, 0f, 0f];
        var row = new float[source.Width * 4];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
            for (var i = 0; i < source.Width * 4; i += 4)
            {
                for (var c = 0; c < 3; c++)
                {
                    minimum[c] = Math.Min(minimum[c], row[i + c]);
                    maximum[c] = Math.Max(maximum[c], row[i + c]);
                }
            }
        }

        return (new ChannelRange(minimum[0], maximum[0]),
                new ChannelRange(minimum[1], maximum[1]),
                new ChannelRange(minimum[2], maximum[2]));
    }

    /// <summary>
    /// Min, max, mean, standard deviation and entropy for all four channels, in one pass. Entropy is taken over
    /// 256 levels regardless of the working format, so the number is comparable between depths.
    /// </summary>
    public static (ChannelStatistics R, ChannelStatistics G, ChannelStatistics B, ChannelStatistics A) Statistics(
        PixelView source)
    {
        // Arrays, not spans: the local function below reads them, and a ref struct cannot be captured.
        var minimum = new[] { 1f, 1f, 1f, 1f };
        var maximum = new[] { 0f, 0f, 0f, 0f };
        var sum = new double[4];
        var sumSquares = new double[4];
        var histograms = new int[4 * 256];
        var row = new float[source.Width * 4];

        long pixels = 0;
        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);

            for (var i = 0; i < source.Width * 4; i += 4)
            {
                for (var c = 0; c < 4; c++)
                {
                    var value = row[i + c];
                    minimum[c] = Math.Min(minimum[c], value);
                    maximum[c] = Math.Max(maximum[c], value);
                    sum[c] += value;
                    sumSquares[c] += (double)value * value;

                    var bin = (int)(value * 255f + 0.5f);
                    if (bin < 0) bin = 0;
                    if (bin > 255) bin = 255;
                    histograms[c * 256 + bin]++;
                }

                pixels++;
            }
        }

        return (Describe(0), Describe(1), Describe(2), Describe(3));

        ChannelStatistics Describe(int channel)
        {
            if (pixels == 0)
                return default;

            var mean = sum[channel] / pixels;
            var variance = Math.Max(0.0, sumSquares[channel] / pixels - mean * mean);
            var entropy = 0.0;

            var histogram = histograms.AsSpan(channel * 256, 256);
            foreach (var count in histogram)
            {
                if (count == 0)
                    continue;

                var probability = (double)count / pixels;
                entropy -= probability * Math.Log2(probability);
            }

            return new ChannelStatistics(minimum[channel], maximum[channel], (float)mean,
                (float)Math.Sqrt(variance), (float)entropy);
        }
    }

    /// <summary>
    /// Equalizes each colour channel through its cumulative distribution (alpha untouched). A channel with a
    /// single occupied level is left as it is — scaling it would turn a flat grey image black.
    /// </summary>
    public static WorkingBuffer Equalize(PixelView source, IPixelPool? pool = null)
    {
        var total = (long)source.Width * source.Height;
        var luts = new byte[3][];

        if (source.Format == WorkingFormat.Rgba8)
        {
            // One pass, three integer histograms, all three channels at once, then a row-band apply.
            // The previous shape called Histogram() three times and each call converted every row to float
            // and back — 3 full-image passes with a float round trip each, on one thread. Measured on a real
            // 1080p photo: 36–45 ms against ImageSharp's 8–9 ms. Merging per-worker integer counts is exact,
            // so the LUT — and therefore every output byte — is unchanged.
            var histograms = HistogramRgba8(source);
            for (var channel = 0; channel < 3; channel++)
                luts[channel] = BuildLut(histograms[channel], total);

            var destinationFast = WorkingBuffer.Rent(source.Width, source.Height, source.Format, pool);
            var view = destinationFast.View;
            var lut0 = luts[0];
            var lut1 = luts[1];
            var lut2 = luts[2];
            RowParallel.For(source.Width, source.Height, (start, end) =>
            {
                for (var y = start; y < end; y++)
                {
                    var row = source.Row(y);
                    var output = view.Row(y);
                    for (var x = 0; x < source.Width; x++)
                    {
                        var o = x * 4;
                        output[o] = lut0[row[o]];
                        output[o + 1] = lut1[row[o + 1]];
                        output[o + 2] = lut2[row[o + 2]];
                        output[o + 3] = row[o + 3];
                    }
                }
            });

            return destinationFast;
        }

        for (var channel = 0; channel < 3; channel++)
        {
            var histogram = Histogram(source, channel);
            luts[channel] = BuildLut(histogram, total);
        }

        var destination = WorkingBuffer.Rent(source.Width, source.Height, source.Format, pool);

        var rowFloats = new float[source.Width * 4];
        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, rowFloats);
            for (var x = 0; x < source.Width; x++)
            {
                var o = x * 4;
                rowFloats[o] = luts[0][ToByte(rowFloats[o])] / 255f;
                rowFloats[o + 1] = luts[1][ToByte(rowFloats[o + 1])] / 255f;
                rowFloats[o + 2] = luts[2][ToByte(rowFloats[o + 2])] / 255f;
            }

            Pixels.WriteRowRaw(rowFloats, source.Width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>
    /// Three 256-bin histograms in one pass over an Rgba8 image. Per-worker integer counts are merged, so the
    /// result is identical to three sequential passes — just once over the data instead of three times.
    /// </summary>
    private static int[][] HistogramRgba8(PixelView source)
    {
        var partials = new System.Collections.Concurrent.ConcurrentBag<int[]>();
        RowParallel.For(source.Width, source.Height, (start, end) =>
        {
            var local = new int[3 * 256];
            for (var y = start; y < end; y++)
            {
                var row = source.Row(y);
                var length = source.Width * 4;
                for (var i = 0; i < length; i += 4)
                {
                    local[row[i]]++;
                    local[256 + row[i + 1]]++;
                    local[512 + row[i + 2]]++;
                }
            }

            partials.Add(local);
        });

        var merged = new[] { new int[256], new int[256], new int[256] };
        foreach (var partial in partials)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var offset = channel * 256;
                for (var v = 0; v < 256; v++)
                    merged[channel][v] += partial[offset + v];
            }
        }

        return merged;
    }

    private static byte[] BuildLut(int[] histogram, long total)
    {
        var lut = new byte[256];
        var cdf = new long[256];

        long cumulative = 0;
        long minimum = 0;
        var found = false;
        for (var i = 0; i < 256; i++)
        {
            cumulative += histogram[i];
            cdf[i] = cumulative;
            if (!found && histogram[i] != 0)
            {
                // The classic formula subtracts the CDF value at the first occupied level.
                minimum = cumulative;
                found = true;
            }
        }

        var denominator = total - minimum;
        if (denominator <= 0)
        {
            // A single occupied level: stretching it would turn a flat grey image black.
            for (var i = 0; i < 256; i++)
                lut[i] = (byte)i;
            return lut;
        }

        for (var i = 0; i < 256; i++)
        {
            var scaled = (cdf[i] - minimum) * 255.0 / denominator;
            var value = (int)(scaled + 0.5);
            lut[i] = (byte)Math.Clamp(value, 0, 255);
        }

        return lut;
    }

    private static byte ToByte(float value)
    {
        var scaled = value * 255f + 0.5f;
        if (scaled <= 0f) return 0;
        if (scaled >= 255f) return 255;
        return (byte)scaled;
    }
}
