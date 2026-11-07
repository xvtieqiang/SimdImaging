using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// How a dither spreads the quantisation error. The error-diffusion kernels push each pixel's remainder to
/// neighbours not yet visited (serially, in a fixed scan order); the Bayer matrices are ordered thresholds,
/// which need no serial dependency but only make sense against a uniform step.
/// </summary>
public enum DitherKernel : byte
{
    /// <summary>No dither: round to the nearest level.</summary>
    None,

    /// <summary>Floyd–Steinberg: seven sixteenths right, three down-left, five down, one down-right.</summary>
    FloydSteinberg,

    /// <summary>Atkinson: six eighths, two rows down; deliberately loses a quarter of the error for cleaner
    /// highlights and shadows.</summary>
    Atkinson,

    /// <summary>Burkes: two rows, divisor 32.</summary>
    Burkes,

    /// <summary>Sierra: three rows, divisor 32.</summary>
    Sierra,

    /// <summary>Stucki: three rows, divisor 42 — a sharper Floyd–Steinberg.</summary>
    Stucki,

    /// <summary>Ordered 2×2 threshold matrix.</summary>
    Bayer2,

    /// <summary>Ordered 4×4 threshold matrix.</summary>
    Bayer4,

    /// <summary>Ordered 8×8 threshold matrix.</summary>
    Bayer8,
}

/// <summary>One error-diffusion tap, relative to the pixel being quantised.</summary>
internal readonly record struct DitherTap(int Dx, int Dy, float Weight);

/// <summary>The kernel tables. Weights come from the published matrices; rows that fall outside the image are
/// dropped, which is why they are not renormalised per pixel.</summary>
internal static class DitherTaps
{
    private static readonly DitherTap[] FloydSteinberg =
    [
        new(1, 0, 7f / 16f),
        new(-1, 1, 3f / 16f), new(0, 1, 5f / 16f), new(1, 1, 1f / 16f),
    ];

    // Six eighths: the missing quarter is intentional, not a typo.
    private static readonly DitherTap[] Atkinson =
    [
        new(1, 0, 1f / 8f), new(2, 0, 1f / 8f),
        new(-1, 1, 1f / 8f), new(0, 1, 1f / 8f), new(1, 1, 1f / 8f),
        new(0, 2, 1f / 8f),
    ];

    private static readonly DitherTap[] Burkes =
    [
        new(1, 0, 8f / 32f), new(2, 0, 4f / 32f),
        new(-2, 1, 2f / 32f), new(-1, 1, 4f / 32f), new(0, 1, 8f / 32f), new(1, 1, 4f / 32f), new(2, 1, 2f / 32f),
    ];

    private static readonly DitherTap[] Sierra =
    [
        new(1, 0, 5f / 32f), new(2, 0, 3f / 32f),
        new(-2, 1, 2f / 32f), new(-1, 1, 4f / 32f), new(0, 1, 5f / 32f), new(1, 1, 4f / 32f), new(2, 1, 2f / 32f),
        new(-1, 2, 2f / 32f), new(0, 2, 3f / 32f), new(1, 2, 2f / 32f),
    ];

    private static readonly DitherTap[] Stucki =
    [
        new(1, 0, 8f / 42f), new(2, 0, 4f / 42f),
        new(-2, 1, 2f / 42f), new(-1, 1, 4f / 42f), new(0, 1, 8f / 42f), new(1, 1, 4f / 42f), new(2, 1, 2f / 42f),
        new(-1, 2, 1f / 42f), new(0, 2, 2f / 42f), new(1, 2, 4f / 42f), new(2, 2, 2f / 42f), new(3, 2, 1f / 42f),
    ];

    public static ReadOnlySpan<DitherTap> For(DitherKernel kernel) => kernel switch
    {
        DitherKernel.FloydSteinberg => FloydSteinberg,
        DitherKernel.Atkinson => Atkinson,
        DitherKernel.Burkes => Burkes,
        DitherKernel.Sierra => Sierra,
        DitherKernel.Stucki => Stucki,
        _ => default,
    };

    /// <summary>Deepest row a kernel writes to; zero for ordered kernels.</summary>
    public static int MaxDy(DitherKernel kernel)
    {
        var taps = For(kernel);
        var max = 0;
        foreach (var tap in taps)
            max = Math.Max(max, tap.Dy);
        return max;
    }

    /// <summary>Edge length of the ordered matrix, or 0 when the kernel is a diffusion kernel.</summary>
    public static int BayerSize(DitherKernel kernel) => kernel switch
    {
        DitherKernel.Bayer2 => 2,
        DitherKernel.Bayer4 => 4,
        DitherKernel.Bayer8 => 8,
        _ => 0,
    };

    public static ReadOnlySpan<byte> BayerMatrix(int size) => size switch
    {
        2 => Bayer2,
        4 => Bayer4,
        _ => Bayer8,
    };

    private static readonly byte[] Bayer2 = Build(2);
    private static readonly byte[] Bayer4 = Build(4);
    private static readonly byte[] Bayer8 = Build(8);

    /// <summary>Recursive construction: each 2×2 block of the next size is the previous matrix scaled by four,
    /// plus the offsets <c>[[0,2],[3,1]]</c>.</summary>
    private static byte[] Build(int size)
    {
        var matrix = new byte[size * size];
        if (size == 1)
            return matrix;

        var half = size / 2;
        var sub = Build(half);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var quadrant = (y / half) * 2 + (x / half);
                var offset = quadrant switch
                {
                    0 => 0,
                    1 => 2,
                    2 => 3,
                    _ => 1,
                };

                matrix[y * size + x] = (byte)(4 * sub[(y % half) * half + (x % half)] + offset);
            }
        }

        return matrix;
    }
}

/// <summary>
/// Colour-depth dithering: reduce each channel to <c>2^bits</c> levels, either by rounding (no dither), by an
/// ordered threshold matrix, or by pushing the remainder onto neighbours.
/// <para>
/// Error diffusion is serial by construction — pixel <c>(x, y)</c> depends on the error of the pixels before
/// it in the fixed scan order — so it is <c>D2</c>: deterministic for a given input, but the result depends
/// on the scan order, never on the thread count. Ordered dithering is <c>D0</c>.
/// </para>
/// </summary>
public static class DitherOps
{
    /// <summary>Reduces each colour channel to <paramref name="bits"/> bits with the given dither.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="bits">Bits kept per channel, 1..8; 8 is a copy.</param>
    /// <param name="kernel">The dither, or <see cref="DitherKernel.None"/> to round.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bits"/> is outside 1..8.</exception>
    public static PixelBuffer Dither(PixelView source, int bits, DitherKernel kernel = DitherKernel.FloydSteinberg,
        IPixelPool? pool = null)
    {
        if (bits is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "Bits per channel must be 1..8.");

        var levels = (1 << bits) - 1;
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var destination = PixelBuffer.Rent(width, height, source.Format, pool);

        if (bits == 8 || kernel == DitherKernel.None)
        {
            return Reduce(source, destination, levels, width, stride);
        }

        var bayerSize = DitherTaps.BayerSize(kernel);
        if (bayerSize > 0)
        {
            return Ordered(source, destination, levels, width, stride, bayerSize);
        }

        return Diffuse(source, destination, levels, width, height, stride, kernel);
    }

    private static PixelBuffer Reduce(PixelView source, PixelBuffer destination, int levels, int width, int stride)
    {
        var row = new float[stride];
        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                row[offset] = Round(row[offset], levels);
                row[offset + 1] = Round(row[offset + 1], levels);
                row[offset + 2] = Round(row[offset + 2], levels);
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    private static float Round(float value, int levels)
    {
        var level = (int)MathF.Floor(value * levels + 0.5f);
        if (level <= 0) return 0f;
        if (level >= levels) return 1f;
        return level / (float)levels;
    }

    private static PixelBuffer Ordered(PixelView source, PixelBuffer destination, int levels, int width, int stride,
        int size)
    {
        var matrix = DitherTaps.BayerMatrix(size);
        var scale = 1f / (size * size);
        var row = new float[stride];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            var matrixRow = (y % size) * size;

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                var threshold = (matrix[matrixRow + x % size] + 0.5f) * scale;
                row[offset] = Quantise(row[offset], threshold, levels);
                row[offset + 1] = Quantise(row[offset + 1], threshold, levels);
                row[offset + 2] = Quantise(row[offset + 2], threshold, levels);
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>Thresholds rather than rounds: the same level either side of a boundary depending on where the
    /// pixel sits in the matrix.</summary>
    private static float Quantise(float value, float threshold, int levels)
    {
        var level = (int)MathF.Floor(value * levels + threshold);
        if (level <= 0) return 0f;
        if (level >= levels) return 1f;
        return level / (float)levels;
    }

    private static PixelBuffer Diffuse(PixelView source, PixelBuffer destination, int levels, int width, int height,
        int stride, DitherKernel kernel)
    {
        var taps = DitherTaps.For(kernel);
        if (taps.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(kernel), kernel, "Unknown dither kernel.");

        // One ring slot per row a kernel can reach, plus the current row: Stucki writes two rows down, so a
        // single "next" buffer is not enough.
        var slotCount = DitherTaps.MaxDy(kernel) + 1;
        var slots = new float[slotCount * stride];
        var row = new float[stride];

        for (var y = 0; y < height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            var current = slots.AsSpan((y % slotCount) * stride, stride);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                for (var c = 0; c < 3; c++)
                {
                    var value = Math.Clamp(row[offset + c] + current[offset + c], 0f, 1f);
                    var quantised = MathF.Floor(value * levels + 0.5f) / levels;
                    var error = value - quantised;
                    row[offset + c] = quantised;

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
}
