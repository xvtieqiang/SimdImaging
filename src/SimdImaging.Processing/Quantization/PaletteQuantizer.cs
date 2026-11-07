using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Reduces an image to a palette with median-cut over a 5-bit-per-channel histogram (32³ bins). Working on a
/// histogram instead of the pixels keeps the split cost independent of image size and makes the result
/// deterministic: no random seeding, no iteration order that depends on the input.
/// </summary>
public static class PaletteQuantizer
{
    private const int Bits = 5;
    private const int Levels = 1 << Bits;          // 32
    private const int Bins = 1 << (Bits * 3);      // 32768

    /// <summary>Builds a palette of at most <paramref name="maxColors"/> colours.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxColors"/> is outside 2..256.</exception>
    public static ColorPalette Build(PixelView source, int maxColors)
    {
        if (maxColors is < 2 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maxColors), maxColors, "A palette holds 2..256 colours.");

        var counts = new int[Bins];
        var sums = new long[Bins * 3];

        if (source.Format == WorkingFormat.Rgba8)
        {
            // Byte path: the histogram only needs the 8-bit code values, so converting a whole row to floats
            // just to convert it back is pure overhead (it was the bulk of the 213 ms measured in M2).
            for (var y = 0; y < source.Height; y++)
            {
                var row = source.Row(y);
                for (var x = 0; x < source.Width; x++)
                {
                    var o = x * 4;
                    int r = row[o], g = row[o + 1], b = row[o + 2];
                    var bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    counts[bin]++;
                    sums[bin * 3] += r;
                    sums[bin * 3 + 1] += g;
                    sums[bin * 3 + 2] += b;
                }
            }
        }
        else
        {
            var row = new float[source.Width * 4];
            for (var y = 0; y < source.Height; y++)
            {
                Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
                for (var x = 0; x < source.Width; x++)
                {
                    var o = x * 4;
                    var r = ToByte(row[o]);
                    var g = ToByte(row[o + 1]);
                    var b = ToByte(row[o + 2]);
                    var bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    counts[bin]++;
                    sums[bin * 3] += r;
                    sums[bin * 3 + 1] += g;
                    sums[bin * 3 + 2] += b;
                }
            }
        }

        var boxes = new List<Box> { Box.Create(counts, sums, OccupiedBins(counts)) };
        while (boxes.Count < maxColors)
        {
            var index = -1;
            var largest = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                var range = boxes[i].LongestAxisRange();
                if (range > largest && boxes[i].Bins.Length > 1)
                {
                    largest = range;
                    index = i;
                }
            }

            if (index < 0)
                break;

            var (left, right) = boxes[index].Split();
            boxes[index] = left;
            boxes.Add(right);
        }

        var colors = new RgbaColor[boxes.Count];
        for (var i = 0; i < boxes.Count; i++)
            colors[i] = boxes[i].Average();

        return new ColorPalette(colors);
    }

    /// <summary>Maps every pixel to its nearest palette colour (squared sRGB distance), keeping alpha.</summary>
    public static PixelBuffer Apply(PixelView source, ColorPalette palette, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var lookup = BuildLookup(palette);

        var destination = PixelBuffer.Rent(source.Width, source.Height, source.Format, pool);
        if (source.Format == WorkingFormat.Rgba8)
        {
            for (var y = 0; y < source.Height; y++)
            {
                var row = source.Row(y);
                var output = destination.View.Row(y);
                for (var x = 0; x < source.Width; x++)
                {
                    var o = x * 4;
                    var bin = ((row[o] >> 3) << 10) | ((row[o + 1] >> 3) << 5) | (row[o + 2] >> 3);
                    var color = palette[lookup[bin]];
                    output[o] = color.R;
                    output[o + 1] = color.G;
                    output[o + 2] = color.B;
                    output[o + 3] = row[o + 3];
                }
            }

            return destination;
        }

        var floatRow = new float[source.Width * 4];
        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, floatRow);
            for (var x = 0; x < source.Width; x++)
            {
                var o = x * 4;
                var bin = ((ToByte(floatRow[o]) >> 3) << 10) | ((ToByte(floatRow[o + 1]) >> 3) << 5) | (ToByte(floatRow[o + 2]) >> 3);
                var color = palette[lookup[bin]];
                floatRow[o] = color.R / 255f;
                floatRow[o + 1] = color.G / 255f;
                floatRow[o + 2] = color.B / 255f;
            }

            Pixels.WriteRowRaw(floatRow, source.Width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    internal static byte[] BuildLookup(ColorPalette palette)
    {
        // One nearest-colour lookup per histogram bin; pixels then cost a table read.
        var lookup = new byte[Bins];
        var colors = palette.Colors;
        for (var bin = 0; bin < Bins; bin++)
        {
            var r = ((bin >> 10) & 31) << 3 | 4;
            var g = ((bin >> 5) & 31) << 3 | 4;
            var b = (bin & 31) << 3 | 4;

            var best = 0;
            var bestDistance = int.MaxValue;
            for (var i = 0; i < colors.Length; i++)
            {
                var dr = r - colors[i].R;
                var dg = g - colors[i].G;
                var db = b - colors[i].B;
                var distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            lookup[bin] = (byte)best;
        }

        return lookup;
    }

    /// <summary>The fixed 216-colour web-safe palette (6 levels per channel, steps of 51).</summary>
    public static ColorPalette WebSafe { get; } = CreateWebSafe();

    private static ColorPalette CreateWebSafe()
    {
        var colors = new RgbaColor[216];
        var index = 0;
        for (var r = 0; r < 6; r++)
        {
            for (var g = 0; g < 6; g++)
            {
                for (var b = 0; b < 6; b++)
                    colors[index++] = new RgbaColor((byte)(r * 51), (byte)(g * 51), (byte)(b * 51), 255);
            }
        }

        return new ColorPalette(colors);
    }

    /// <summary>
    /// Maps to a palette with error diffusion. The propagated error is clamped to <paramref name="maxError"/>
    /// so a large quantisation step cannot produce snow.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="palette">The target palette.</param>
    /// <param name="kernel">The diffusion kernel; <see cref="DitherKernel.None"/> maps without error feedback.</param>
    /// <param name="maxError">Largest error carried to a neighbour, in sample units.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="NotSupportedException">An ordered (Bayer) kernel: it assumes a uniform step, which an
    /// arbitrary palette does not have.</exception>
    public static PixelBuffer Dither(PixelView source, ColorPalette palette,
        DitherKernel kernel = DitherKernel.FloydSteinberg, float maxError = 16f / 255f, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentOutOfRangeException.ThrowIfNegative(maxError);

        if (kernel == DitherKernel.None)
            return Apply(source, palette, pool);

        if (DitherTaps.BayerSize(kernel) > 0)
            throw new NotSupportedException(
                "Ordered dithering assumes a uniform step; use DitherOps.Dither for a bit depth, or an error-diffusion kernel for a palette.");

        var taps = DitherTaps.For(kernel);
        if (taps.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(kernel), kernel, "Unknown dither kernel.");

        var lookup = BuildLookup(palette);
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var destination = PixelBuffer.Rent(width, height, source.Format, pool);

        // One ring slot per row the kernel can reach: Stucki writes two rows down, so a single "next" buffer
        // is not enough.
        var slotCount = DitherTaps.MaxDy(kernel) + 1;
        var slots = new float[slotCount * stride];
        var row = new float[stride];
        Span<float> adjusted = stackalloc float[3];

        for (var y = 0; y < height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            var current = slots.AsSpan((y % slotCount) * stride, stride);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                for (var c = 0; c < 3; c++)
                    adjusted[c] = Math.Clamp(row[offset + c] + current[offset + c], 0f, 1f);

                var bin = ((ToByte(adjusted[0]) >> 3) << 10) | ((ToByte(adjusted[1]) >> 3) << 5) | (ToByte(adjusted[2]) >> 3);
                var chosen = palette[lookup[bin]];

                for (var c = 0; c < 3; c++)
                {
                    var target = (c == 0 ? chosen.R : c == 1 ? chosen.G : chosen.B) / 255f;
                    var error = Math.Clamp(adjusted[c] - target, -maxError, maxError);
                    row[offset + c] = target;

                    if (error == 0f)
                        continue;

                    foreach (var tap in taps)
                    {
                        var targetX = x + tap.Dx;
                        if ((uint)targetX >= (uint)width)
                            continue;

                        if (tap.Dy == 0)
                            current[targetX * 4 + c] += error * tap.Weight;
                        else
                            slots[((y + tap.Dy) % slotCount) * stride + targetX * 4 + c] += error * tap.Weight;
                    }
                }
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
            current.Clear();
        }

        return destination;
    }

    private static byte ToByte(float value)
    {
        var scaled = value * 255f + 0.5f;
        if (scaled <= 0f) return 0;
        if (scaled >= 255f) return 255;
        return (byte)scaled;
    }

    private static int[] OccupiedBins(int[] counts)
    {
        var used = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] != 0)
                used++;
        }

        var bins = new int[used];
        var index = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] != 0)
                bins[index++] = i;
        }

        return bins;
    }

    private sealed class Box
    {
        private readonly int[] _counts;
        private readonly long[] _sums;

        private Box(int[] counts, long[] sums, int[] bins, long count, long sumR, long sumG, long sumB)
        {
            _counts = counts;
            _sums = sums;
            Bins = bins;
            Count = count;
            SumR = sumR;
            SumG = sumG;
            SumB = sumB;
        }

        public int[] Bins { get; }
        public long Count { get; }
        public long SumR { get; }
        public long SumG { get; }
        public long SumB { get; }

        public static Box Create(int[] counts, long[] sums, int[] bins)
        {
            long count = 0, r = 0, g = 0, b = 0;
            foreach (var bin in bins)
            {
                count += counts[bin];
                r += sums[bin * 3];
                g += sums[bin * 3 + 1];
                b += sums[bin * 3 + 2];
            }

            return new Box(counts, sums, bins, count, r, g, b);
        }

        public RgbaColor Average() => new(
            (byte)((SumR + Count / 2) / Count),
            (byte)((SumG + Count / 2) / Count),
            (byte)((SumB + Count / 2) / Count),
            255);

        public int LongestAxisRange()
        {
            var (minR, maxR, minG, maxG, minB, maxB) = Bounds();
            return Math.Max(maxR - minR, Math.Max(maxG - minG, maxB - minB));
        }

        public (Box Left, Box Right) Split()
        {
            var (minR, maxR, minG, maxG, minB, maxB) = Bounds();
            var rangeR = maxR - minR;
            var rangeG = maxG - minG;
            var rangeB = maxB - minB;
            var axis = rangeR >= rangeG && rangeR >= rangeB ? 0 : (rangeG >= rangeB ? 1 : 2);

            Array.Sort(Bins, (a, b) => Axis(a, axis).CompareTo(Axis(b, axis)));

            var half = Count / 2;
            long accumulated = 0;
            var split = 1;
            for (var i = 0; i < Bins.Length - 1; i++)
            {
                accumulated += _counts[Bins[i]];
                if (accumulated >= half)
                {
                    split = i + 1;
                    break;
                }
            }

            var left = Bins.AsSpan(0, split).ToArray();
            var right = Bins.AsSpan(split).ToArray();
            return (Create(_counts, _sums, left), Create(_counts, _sums, right));
        }

        private (int MinR, int MaxR, int MinG, int MaxG, int MinB, int MaxB) Bounds()
        {
            int minR = 31, maxR = 0, minG = 31, maxG = 0, minB = 31, maxB = 0;
            foreach (var bin in Bins)
            {
                var r = (bin >> 10) & 31;
                var g = (bin >> 5) & 31;
                var b = bin & 31;
                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                if (g < minG) minG = g;
                if (g > maxG) maxG = g;
                if (b < minB) minB = b;
                if (b > maxB) maxB = b;
            }

            return (minR, maxR, minG, maxG, minB, maxB);
        }

        private static int Axis(int bin, int axis) => axis switch
        {
            0 => (bin >> 10) & 31,
            1 => (bin >> 5) & 31,
            _ => bin & 31,
        };
    }
}
