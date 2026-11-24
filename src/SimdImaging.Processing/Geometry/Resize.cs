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
/// <summary>What the reconstruction-weight cache holds, as <c>MemoryBudget.Snapshot()</c> reports it.</summary>
/// <param name="Entries">Cached size combinations.</param>
/// <param name="Bytes">Their total managed size.</param>
/// <param name="Hits">Builds served from the cache.</param>
/// <param name="Misses">Builds that had to compute a table.</param>
/// <param name="Evictions">Tables dropped by the budget or by a trim.</param>
/// <param name="BudgetBytes">The byte budget in force.</param>
/// <param name="MaxEntries">The entry cap in force.</param>
public readonly record struct WeightCacheStatistics(
    int Entries,
    long Bytes,
    long Hits,
    long Misses,
    long Evictions,
    long BudgetBytes,
    int MaxEntries)
{
    /// <summary>Hits over all requests, 0 when nothing has been requested.</summary>
    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);
}

internal sealed class WeightTable
{
    private WeightTable(int taps, int[] starts, float[] weights)
    {
        Taps = taps;
        Starts = starts;
        Weights = weights;
        Bytes = (long)starts.Length * sizeof(int) + (long)weights.Length * sizeof(float);
    }

    public int Taps { get; }
    public int[] Starts { get; }
    public float[] Weights { get; }

    /// <summary>Managed bytes this table holds; what the cache budget is measured in.</summary>
    public long Bytes { get; }

    // Weights depend only on (sourceLength, destinationLength, resampler), never on pixels. Rebuilding
    // them per frame costs both the kernel evaluations and a fresh ~65 KiB allocation on every resize.
    //
    // Bounded by bytes and by entry count, and evicted least-recently-used rather than all at once: the
    // previous policy cleared the whole cache on overflow, so a process resizing at 25 different sizes
    // rebuilt every table on every call instead of dropping one. A 4K resize holds ~130 KiB per axis, so
    // the default budget keeps roughly sixty distinct sizes.
    private static readonly Dictionary<(int Source, int Destination, Resampler Filter), LinkedListNode<Entry>>
        Cache = new();
    private static readonly LinkedList<Entry> Lru = new();
    private static readonly object CacheGate = new();

    internal const long DefaultBudgetBytes = 8L << 20;
    internal const int DefaultMaxEntries = 64;

    internal static long BudgetBytes = DefaultBudgetBytes;
    internal static int MaxEntries = DefaultMaxEntries;
    private static long _cachedBytes;
    private static long _hits;
    private static long _misses;
    private static long _evictions;

    private readonly record struct Entry((int Source, int Destination, Resampler Filter) Key, WeightTable Table);

    /// <summary>The cache as <c>MemoryBudget.Snapshot()</c> reports it.</summary>
    public static WeightCacheStatistics Statistics
    {
        get
        {
            lock (CacheGate)
                return new WeightCacheStatistics(Cache.Count, _cachedBytes, _hits, _misses, _evictions, BudgetBytes, MaxEntries);
        }
    }

    public static WeightTable Build(int sourceLength, int destinationLength, Resampler resampler)
    {
        var key = (sourceLength, destinationLength, resampler);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                _hits++;
                return node.Value.Table;
            }

            _misses++;
        }

        // Built outside the lock: the arithmetic is the expensive part and it does not touch shared state.
        var built = BuildCore(sourceLength, destinationLength, resampler);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var raced))
            {
                // Another thread built the same table while this one was working; keep theirs so every
                // caller for a given size shares one instance.
                Lru.Remove(raced);
                Lru.AddFirst(raced);
                return raced.Value.Table;
            }

            var entry = new Entry(key, built);
            var node = Lru.AddFirst(entry);
            Cache[key] = node;
            _cachedBytes += built.Bytes;

            while (Cache.Count > 1 && (Cache.Count > MaxEntries || _cachedBytes > BudgetBytes))
                EvictLast();
        }

        return built;
    }

    private static void EvictLast()
    {
        var node = Lru.Last;
        if (node is null)
            return;

        Lru.RemoveLast();
        Cache.Remove(node.Value.Key);
        _cachedBytes -= node.Value.Table.Bytes;
        _evictions++;
    }

    /// <summary>Drops every cached table.</summary>
    internal static void TrimCache()
    {
        lock (CacheGate)
        {
            _evictions += Cache.Count;
            Cache.Clear();
            Lru.Clear();
            _cachedBytes = 0;
        }
    }

    /// <summary>Restores the default budget and clears counters (tests only).</summary>
    internal static void ResetForTests()
    {
        TrimCache();
        BudgetBytes = DefaultBudgetBytes;
        MaxEntries = DefaultMaxEntries;
        _hits = 0;
        _misses = 0;
        _evictions = 0;
    }

    private static WeightTable BuildCore(int sourceLength, int destinationLength, Resampler resampler)
    {
        // M8: the weight mathematics lives in TapPlanBuilder so Warp, Affine and Resize all use
        // one reconstruction filter. The arithmetic is verbatim, including the double-precision
        // normalisation, so switching this over cannot change a single output byte.
        var (scale, filterScale, radius, taps, kernel) =
            TapPlanBuilder.Parameters(sourceLength, destinationLength, resampler);

        var starts = new int[destinationLength];
        var weights = new float[destinationLength * taps];
        var buffer = new double[taps];

        for (var i = 0; i < destinationLength; i++)
        {
            var center = (i + 0.5) / scale;
            var start = TapPlanBuilder.FillAt(buffer, center, sourceLength, filterScale, radius, kernel);

            starts[i] = start;
            var offset = i * taps;
            for (var k = 0; k < taps; k++)
                weights[offset + k] = (float)buffer[k];
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
                            // Rgba8 pixel = 4 bytes. One unaligned 32-bit copy replaces four byte
                            // load/stores; it is a pure bit copy, so the byte sequence is identical on
                            // every endianness and every SIMD tier (no integer reinterpretation).
                            // Measured 1.90-1.99x on the inner loop vs the four-byte version
                            // (`nearest-ablate`), byte-identical across all three shapes.
                            Unsafe.WriteUnaligned(ref destinationRow[to],
                                Unsafe.ReadUnaligned<uint>(ref sourceRow[from]));
                        }
                    }
                }
            });

            return nearest;
        }

        // The tap offset that carries the whole weight, or false when the table is not a single unit tap.
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

        // Each worker owns its own row/scratch buffers (FloatScratch is thread-local), so bands can
        // run concurrently without aliasing. Output rows are disjoint; source rows are read-only.
        RowParallel.ForBands(bandCount, (start, end) =>
        {
            var row = FloatScratch.Rent(source.Width * 4);
            var outputRow = FloatScratch.Rent(stride);

            // A ring of horizontally resized rows instead of a whole source band. Bands used to redo the
            // 2-taps rows of overlap at every boundary, which is why a small scratch cost time and a big
            // one cost memory: the eight-megabyte band held 94 MiB at 7680 wide for a 960-wide answer.
            var ringRows = Math.Min(source.Height, weightsY.Taps + 2);
            var scratch = FloatScratch.Rent(stride * ringRows);
            var filledThrough = -1;
            try
            {
                for (var b = start; b < end; b++)
                {
                    var bandStart = b * bandHeight;
                    var bandEnd = Math.Min(bandStart + bandHeight, height);

                    var required = (long)stride * ringRows * sizeof(float);
                    if (required > MaxScratchBytes)
                        throw new InvalidOperationException(
                            $"The vertical pass needs {required / (1024 * 1024)} MiB of scratch " +
                            $"({ringRows} rows x {stride} floats), above the {MaxScratchBytes / (1024 * 1024)} MiB " +
                            "working-set limit. Reduce the downsample ratio or resize in two steps.");

                    for (var dy = bandStart; dy < bandEnd; dy++)
                    {
                        // Tap windows advance monotonically, so each source row is resized once per worker.
                        var first = Math.Clamp(weightsY.Starts[dy], 0, source.Height - 1);
                        var last = Math.Clamp(weightsY.Starts[dy] + weightsY.Taps - 1, 0, source.Height - 1);
                        for (var sy = Math.Max(filledThrough + 1, first); sy <= last; sy++)
                        {
                            Pixels.ReadRowPremultiplied(source.Row(sy), source.Width, source.Format, row);
                            HorizontalRow(row, source.Width, scratch.AsSpan(sy % ringRows * stride, stride), width, weightsX);
                            filledThrough = sy;
                        }

                        VerticalRow(scratch, ringRows, source.Height, width, dy, weightsY, outputRow);
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


/// <summary>The tap offset that carries the whole weight, or false when the table is not a single unit tap.</summary>
/// <remarks>Shared with the streaming resize, which has to reproduce the nearest fast path byte for byte.</remarks>
internal static bool TryNearestTap(WeightTable table, out int offset)
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

    /// <summary>One source row → one scratch row of premultiplied floats.</summary>
    internal static void HorizontalRow(float[] source, int sourceWidth, Span<float> destination,
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
    internal static void VerticalRow(float[] scratch, int ringRows, int sourceHeight, int width, int dy, WeightTable weights, float[] destination)
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
            Convolve(scratch, ringRows, sourceHeight, stride, interior, start, weights, tapBase,
                rows, tapWeights, destination);
            return;
        }

        var rentedRows = ArrayPool<int>.Shared.Rent(weights.Taps);
        var rentedWeights = ArrayPool<float>.Shared.Rent(weights.Taps);
        try
        {
            Convolve(scratch, ringRows, sourceHeight, stride, interior, start, weights, tapBase,
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
    private static void Convolve(float[] scratch, int ringRows, int sourceHeight, int stride, bool interior,
        int start, WeightTable weights, int tapBase, Span<int> rows, Span<float> tapWeights, float[] destination)
    {
        var tapCount = 0;
        for (var k = 0; k < weights.Taps; k++)
        {
            var weight = weights.Weights[tapBase + k];
            if (weight == 0f)
                continue;
            var sy = interior ? start + k : Math.Clamp(start + k, 0, sourceHeight - 1);
            rows[tapCount] = sy % ringRows * stride;
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

    /// <summary>
    /// Resize with an explicit sampling contract. <c>options == default</c> delegates to the frozen
    /// <see cref="Resize"/> path (byte-identical); any option set switches to the unified per-pixel
    /// core: scale-aware tap plans (downscaling widens the kernel exactly like <see cref="WeightTable"/>
    /// does), per-sample domain transform, alpha mode, edge policy — Background treats the source as
    /// surrounded by a constant field of <paramref name="background"/>, and tap weights are deliberately
    /// <b>not</b> renormalised so in-range pixels keep the <see cref="EdgeMode.Clamp"/> weight sum —
    /// and out-of-gamut handling.
    /// <para>The per-pixel path is deliberately simple (no band scratch, no vector tier): its job is
    /// semantics, not throughput; see docs/known-gaps.md.</para>
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="width">Destination width.</param>
    /// <param name="height">Destination height.</param>
    /// <param name="resampler">Any of the five reconstruction kernels.</param>
    /// <param name="options">Sampling domain, curve, alpha, edge and out-of-gamut contract.</param>
    /// <param name="background">Colour for taps outside the source under <see cref="EdgeMode.Background"/>.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="InvalidDataException"><see cref="OutOfGamutPolicy.Throw"/> detected an out-of-range sample.</exception>
    public static WorkingBuffer ResizeWithOptions(PixelView source, int width, int height,
        Resampler resampler, SampleOptions options, RgbaColor background = default, IPixelPool? pool = null)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        if (options == default)
            return Resize(source, width, height, resampler, pool);

        var (scaleX, filterScaleX, radiusX, tapsX, kernelX) = TapPlanBuilder.Parameters(source.Width, width, resampler);
        var (scaleY, filterScaleY, radiusY, tapsY, kernelY) = TapPlanBuilder.Parameters(source.Height, height, resampler);

        var bufferX = new double[tapsX];
        var weightsX = new float[tapsX];
        var bufferY = new double[tapsY];
        var weightsY = new float[tapsY];

        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;
        var row = new float[width * 4];
        Span<float> sample = stackalloc float[4];
        Span<float> backgroundColour = stackalloc float[4];
        backgroundColour[0] = background.R / 255f;
        backgroundColour[1] = background.G / 255f;
        backgroundColour[2] = background.B / 255f;
        backgroundColour[3] = background.A / 255f;

        try
        {
            for (var y = 0; y < height; y++)
            {
                // The resize phase convention: a destination pixel centre (i + 0.5) maps back to
                // (i + 0.5) / scale — the same centres WeightTable.BuildCore is built on.
                var centreY = (y + 0.5) / scaleY;
                var startY = TapPlanBuilder.FillAt(bufferY, centreY, source.Height, filterScaleY, radiusY, kernelY);
                for (var k = 0; k < weightsY.Length; k++)
                    weightsY[k] = (float)bufferY[k];
                var planY = new TapPlan(startY, weightsY);

                for (var x = 0; x < width; x++)
                {
                    var centreX = (x + 0.5) / scaleX;
                    var startX = TapPlanBuilder.FillAt(bufferX, centreX, source.Width, filterScaleX, radiusX, kernelX);
                    for (var k = 0; k < weightsX.Length; k++)
                        weightsX[k] = (float)bufferX[k];
                    var planX = new TapPlan(startX, weightsX);

                    SamplingCore.SampleWithOptions(source, planX, planY, options, backgroundColour, sample);
                    sample.CopyTo(new Span<float>(row, x * 4, 4));
                }

                Pixels.WriteRowRaw(row, width, source.Format, view.Row(y));
            }

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }
}
