using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// Per-destination-pixel filter taps for one axis: a start index into the source, a fixed number of taps and
/// normalized weights. Fixed tap count (zero padded at the ends) keeps the inner loop branch-free and leaves
/// room for a vectorized version later.
/// </summary>
internal sealed class WeightTable
{
    private WeightTable(int taps, int[] starts, float[] weights)
    {
        Taps = taps;
        Starts = starts;
        Weights = weights;
    }

    public int Taps { get; }
    public int[] Starts { get; }
    public float[] Weights { get; }

    // Weights depend only on (sourceLength, destinationLength, resampler), never on pixels. Rebuilding
    // them per frame costs both the kernel evaluations and a fresh ~65 KiB allocation on every resize.
    // Bounded so a long-lived process resizing at many different sizes cannot grow without limit.
    private static readonly Dictionary<(int Source, int Destination, Resampler Filter), WeightTable> Cache = new();
    private static readonly object CacheGate = new();
    private const int MaxCached = 24;

    public static WeightTable Build(int sourceLength, int destinationLength, Resampler resampler)
    {
        var key = (sourceLength, destinationLength, resampler);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;
        }

        var built = BuildCore(sourceLength, destinationLength, resampler);
        lock (CacheGate)
        {
            if (Cache.Count >= MaxCached)
                Cache.Clear();
            Cache[key] = built;
        }

        return built;
    }

    private static WeightTable BuildCore(int sourceLength, int destinationLength, Resampler resampler)
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

        var starts = new int[destinationLength];
        var weights = new float[destinationLength * taps];
        var buffer = new double[taps];

        for (var i = 0; i < destinationLength; i++)
        {
            var center = (i + 0.5) / scale;
            var start = (int)Math.Ceiling(center - radius - 0.5);
            var sum = 0.0;
            for (var k = 0; k < taps; k++)
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
                Array.Clear(buffer);
                var nearest = Math.Clamp((int)Math.Floor(center), 0, sourceLength - 1) - start;
                if ((uint)nearest < (uint)taps)
                    buffer[nearest] = 1.0;
                sum = 1.0;
            }

            starts[i] = start;
            var offset = i * taps;
            for (var k = 0; k < taps; k++)
                weights[offset + k] = (float)(buffer[k] / sum);
        }

        return new WeightTable(taps, starts, weights);
    }
}

/// <summary>
/// Separable resampling: horizontal into a float scratch, vertical into the destination. Working in
/// premultiplied floats is what keeps transparent pixels from bleeding their colour into neighbours.
/// <para>
/// Both passes are vectorized — one pixel (four channels) per iteration horizontally, eight floats per
/// iteration vertically. Every output element still accumulates its taps in the same order as the scalar
/// path, so the vector width changes speed, not bytes.
/// </para>
/// </summary>
public static class Resample
{
    /// <summary>
    /// Row-band float scratch budget. A same-process sweep (16/8/4/2/1 MiB, three repeats each) showed
    /// 16 MiB pays a 2.2× cold-start penalty on the first call (43.1 ms vs 19.5 ms afterwards), while
    /// 4 MiB stays within 1.15× and is ~9% faster in steady state — the smaller band fits in L3.
    /// Every budget produced byte-identical output. Changing this must never change the output bytes.
    /// </summary>
    internal static long ScratchBudgetBytes { get; set; } = 4L << 20;

    /// <summary>Hard ceiling for one band's scratch. The soft budget above only picks a band height; this is
    /// the point at which the working set would break the documented limit, so it throws instead of growing.</summary>
    private const long MaxScratchBytes = 512L << 20;

    public static WorkingBuffer Resize(PixelView source, int width, int height, Resampler resampler, IPixelPool? pool)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        var weightsX = WeightTable.Build(source.Width, width, resampler);
        var weightsY = WeightTable.Build(source.Height, height, resampler);

        // Nearest is point sampling. Sending it through the separable path expands the entire source to float,
        // runs two convolution passes and contracts again — measured 2.170 ms at 1080p -> 960x540 against
        // ImageSharp's 0.283 ms, for what is a byte copy.
        //
        // The table does NOT always carry exactly one tap: it keeps a three-tap window with the middle weight
        // at one and the neighbours at zero, so the precondition is "exactly one non-zero weight, and it is
        // one", not "Taps == 1". Checking Taps alone would have enabled the fast path for 1920->960 only and
        // left every other ratio on the slow path, which the 17-case table would never have shown.
        // Rgba8 only: that is where the float round trip is provably the identity on bytes.
        if (resampler == Resampler.Nearest && source.Format == WorkingFormat.Rgba8
            && TryNearestTap(weightsX, out var tapX) && TryNearestTap(weightsY, out var tapY))
        {
            var nearest = WorkingBuffer.Rent(width, height, source.Format, pool);
            var nearestView = nearest.View;
            var startsX = weightsX.Starts;
            var startsY = weightsY.Starts;

            // ForBands, not For: `For` refuses to parallelise below 64 K pixels, and a 640x360 source
            // resized to 320x180 is 57.6 K — so the fast path silently ran on one core and reported
            // LastWorkerCount = 1. Bands have no such gate; the split is explicit and rows are disjoint.
            var degree = RowParallel.Degree;
            var bandRows = Math.Max(16, (height + Math.Max(1, degree) - 1) / Math.Max(1, degree));
            var bands = Math.Max(1, (height + bandRows - 1) / bandRows);

            RowParallel.ForBands(bands, (firstBand, lastBand) =>
            {
                for (var band = firstBand; band < lastBand; band++)
                {
                    var firstRow = band * bandRows;
                    var lastRow = Math.Min(firstRow + bandRows, height);
                    for (var y = firstRow; y < lastRow; y++)
                    {
                        var sourceRow = source.Row(startsY[y] + tapY);
                        var destinationRow = nearestView.Row(y);
                        for (var x = 0; x < width; x++)
                        {
                            var from = (startsX[x] + tapX) * 4;
                            var to = x * 4;
                            destinationRow[to] = sourceRow[from];
                            destinationRow[to + 1] = sourceRow[from + 1];
                            destinationRow[to + 2] = sourceRow[from + 2];
                            destinationRow[to + 3] = sourceRow[from + 3];
                        }
                    }
                }
            });

            return nearest;
        }

        // The tap offset that carries the whole weight, or false when the table is not a single unit tap.
        static bool TryNearestTap(WeightTable table, out int offset)
        {
            offset = -1;
            for (var x = 0; x < table.Starts.Length; x++)
            {
                var baseIndex = x * table.Taps;
                var unit = -1;
                for (var k = 0; k < table.Taps; k++)
                {
                    var weight = table.Weights[baseIndex + k];
                    if (weight == 1f && unit < 0)
                    {
                        unit = k;
                        continue;
                    }

                    if (weight != 0f)
                        return false;
                }

                if (unit < 0)
                    return false;

                if (offset < 0)
                    offset = unit;
                else if (offset != unit)
                    return false;          // the unit tap moves between destination pixels
            }

            return offset >= 0;
        }

        var stride = width * 4;
        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;

        var budgetBytes = ScratchBudgetBytes;
        var budgetRows = Math.Max(weightsY.Taps + 2, (int)(budgetBytes / Math.Max(1L, (long)stride * 4)));
        var scaleY = (double)height / source.Height;
        var bandHeight = Math.Max(1, (int)((budgetRows - weightsY.Taps) * Math.Max(scaleY, 1e-9)));
        bandHeight = Math.Min(bandHeight, 512);
        // Also split by core count so a 1080p resize uses all cores, not just the 5 bands that
        // the scratch budget alone would produce. Floor at 32 rows so tap overlap stays cheap.
        var coreBands = Math.Max(32, (height + RowParallel.Degree - 1) / RowParallel.Degree);
        bandHeight = Math.Min(bandHeight, coreBands);
        var bandCount = (height + bandHeight - 1) / bandHeight;
        var initialRows = Math.Min(source.Height,
            (int)Math.Ceiling(bandHeight / Math.Max(scaleY, 1e-9)) + 2 * weightsY.Taps + 4);

        // Each worker owns its own row/scratch buffers (FloatScratch is thread-local), so bands can
        // run concurrently without aliasing. Output rows are disjoint; source rows are read-only.
        RowParallel.ForBands(bandCount, (start, end) =>
        {
            var row = FloatScratch.Rent(source.Width * 4);
            var outputRow = FloatScratch.Rent(stride);
            var scratchRows = initialRows;
            var scratch = FloatScratch.Rent(stride * scratchRows);
            try
            {
                for (var b = start; b < end; b++)
                {
                    var bandStart = b * bandHeight;
                    var bandEnd = Math.Min(bandStart + bandHeight, height);

                    var sourceStart = int.MaxValue;
                    var sourceEnd = int.MinValue;
                    for (var dy = bandStart; dy < bandEnd; dy++)
                    {
                        var startRow = weightsY.Starts[dy];
                        sourceStart = Math.Min(sourceStart, Math.Clamp(startRow, 0, source.Height - 1));
                        sourceEnd = Math.Max(sourceEnd, Math.Clamp(startRow + weightsY.Taps - 1, 0, source.Height - 1));
                    }

                    var neededRows = sourceEnd - sourceStart + 1;
                    if (neededRows > scratchRows)
                    {
                        // This path used to grow without a ceiling, silently exceeding the documented
                        // working-set contract. A band whose tap window spans more than MaxScratchBytes is
                        // now refused instead of quietly renting gigabytes.
                        var required = (long)stride * neededRows * sizeof(float);
                        if (required > MaxScratchBytes)
                            throw new InvalidOperationException(
                                $"The vertical pass needs {required / (1024 * 1024)} MiB of scratch for one " +
                                $"band ({neededRows} rows x {stride} floats), above the {MaxScratchBytes / (1024 * 1024)} MiB " +
                                "working-set limit. Reduce the downsample ratio or resize in two steps.");

                        FloatScratch.Return(scratch);
                        scratchRows = neededRows;
                        scratch = FloatScratch.Rent(stride * scratchRows);
                    }

                    for (var sy = sourceStart; sy <= sourceEnd; sy++)
                    {
                        Pixels.ReadRowPremultiplied(source.Row(sy), source.Width, source.Format, row);
                        HorizontalRow(row, source.Width, scratch.AsSpan((sy - sourceStart) * stride, stride), width, weightsX);
                    }

                    for (var dy = bandStart; dy < bandEnd; dy++)
                    {
                        VerticalRow(scratch, sourceStart, source.Height, width, dy, weightsY, outputRow);
                        Pixels.WriteRowUnpremultiplied(outputRow, width, source.Format, view.Row(dy));
                    }
                }
            }
            finally
            {
                FloatScratch.Return(row);
                FloatScratch.Return(outputRow);
                FloatScratch.Return(scratch);
            }
        });

        return destination;
    }


    /// <summary>One source row → one scratch row of premultiplied floats.</summary>
    private static void HorizontalRow(float[] source, int sourceWidth, Span<float> destination,
        int destinationWidth, WeightTable weights)
    {
        var taps = weights.Taps;
        var starts = weights.Starts;
        var all = weights.Weights;

        for (var dx = 0; dx < destinationWidth; dx++)
        {
            var start = starts[dx];
            var tapBase = dx * taps;
            var accumulator = Vector128<float>.Zero;

            // The clamp ternary used to sit inside the tap loop, so EVERY tap paid a branch.
            // Split the paths instead. Tap order is unchanged, so the bytes are unchanged.
            if (start >= 0 && start + taps <= sourceWidth)
            {
                for (var k = 0; k < taps; k++)
                {
                    var weight = all[tapBase + k];
                    if (weight == 0f)
                        continue;
                    accumulator += Vector128.LoadUnsafe(ref source[(start + k) * 4]) * Vector128.Create(weight);
                }
            }
            else
            {
                for (var k = 0; k < taps; k++)
                {
                    var weight = all[tapBase + k];
                    if (weight == 0f)
                        continue;
                    var sx = Math.Clamp(start + k, 0, sourceWidth - 1);
                    accumulator += Vector128.LoadUnsafe(ref source[sx * 4]) * Vector128.Create(weight);
                }
            }

            accumulator.StoreUnsafe(ref destination[dx * 4]);
        }
    }


    /// <summary>One destination row: accumulate every tap in a register, write the row once.</summary>
    private static void VerticalRow(float[] scratch, int scratchStart, int sourceHeight, int width, int dy, WeightTable weights, float[] destination)
    {
        var stride = width * 4;
        var start = weights.Starts[dy];
        var tapBase = dy * weights.Taps;
        var interior = start >= 0 && start + weights.Taps <= sourceHeight;

        // Tap count grows with the downsample ratio: 16384 -> 1 needs ~98 K taps, which as two stackalloc
        // arrays is ~786 KB and overflows a 1 MB worker stack. Small ratios keep the stack (cheapest thing
        // there is); past StackTapLimit the arrays come from the shared pool instead.
        const int StackTapLimit = 256;
        if (weights.Taps <= StackTapLimit)
        {
            Span<int> rows = stackalloc int[StackTapLimit];
            Span<float> tapWeights = stackalloc float[StackTapLimit];
            Convolve(scratch, scratchStart, sourceHeight, stride, interior, start, weights, tapBase,
                rows, tapWeights, destination);
            return;
        }

        var rentedRows = ArrayPool<int>.Shared.Rent(weights.Taps);
        var rentedWeights = ArrayPool<float>.Shared.Rent(weights.Taps);
        try
        {
            Convolve(scratch, scratchStart, sourceHeight, stride, interior, start, weights, tapBase,
                rentedRows, rentedWeights, destination);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedRows);
            ArrayPool<float>.Shared.Return(rentedWeights);
        }
    }

    /// <summary>
    /// Resolves the tap rows once and accumulates the whole destination row in registers, writing it a single
    /// time. Each lane starts at zero and adds taps in ascending order, so the float result — and therefore
    /// every output byte — matches the scalar path.
    /// </summary>
    private static void Convolve(float[] scratch, int scratchStart, int sourceHeight, int stride, bool interior,
        int start, WeightTable weights, int tapBase, Span<int> rows, Span<float> tapWeights, float[] destination)
    {
        var tapCount = 0;
        for (var k = 0; k < weights.Taps; k++)
        {
            var weight = weights.Weights[tapBase + k];
            if (weight == 0f)
                continue;
            var sy = interior ? start + k : Math.Clamp(start + k, 0, sourceHeight - 1);
            rows[tapCount] = (sy - scratchStart) * stride;
            tapWeights[tapCount] = weight;
            tapCount++;
        }

        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= stride; i += 8)
            {
                var sum = Vector256<float>.Zero;
                for (var t = 0; t < tapCount; t++)
                    sum += Vector256.LoadUnsafe(ref scratch[rows[t] + i]) * Vector256.Create(tapWeights[t]);
                sum.StoreUnsafe(ref destination[i]);
            }
        }

        for (; i < stride; i++)
        {
            var sum = 0f;
            for (var t = 0; t < tapCount; t++)
                sum += scratch[rows[t] + i] * tapWeights[t];
            destination[i] = sum;
        }
    }
}
