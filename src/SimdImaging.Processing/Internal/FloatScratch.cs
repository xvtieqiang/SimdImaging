namespace SimdImaging.Processing.Internal;

/// <summary>
/// Thread-local reusable float scratch. <c>ArrayPool&lt;float&gt;.Shared</c> caps a bucket at roughly one
/// million elements (~4 MB), so the 16 MB resize scratch would never be pooled and would be allocated on
/// every call. Each Rent removes a buffer from the free list, so nested operators cannot overwrite each
/// other's scratch.
/// <para>
/// Retention is budgeted, in bytes, per thread: at most <see cref="RetainedBytesLimit"/> is kept idle, and
/// a buffer larger than <see cref="BufferBytesLimit"/> is never retained at all. Both limits apply to what
/// is <b>kept</b>, never to what is handed out — an 8K resize asks for a 64 MB scratch, gets it, and simply
/// does not find it still cached next time. That trade bounds the resident set without slowing the hot
/// path: before this, a 16-thread machine could pin eight arbitrary-size buffers per worker forever.
/// </para>
/// <para>Steady state is zero managed allocation once a thread has warmed up. Buffers are uninitialised,
/// so every caller must fully write the region it reads.</para>
/// <para>
/// The counters are per thread, because the storage is: a snapshot describes the calling thread only. That
/// is deliberate — a process-wide registry would need a lock or an interlocked increment on the hot path,
/// which is exactly what the thread-local design exists to avoid.
/// </para>
/// </summary>
internal static class FloatScratch
{
    /// <summary>Idle bytes one thread may keep.</summary>
    public const long DefaultRetainedBytes = 64L << 20;

    /// <summary>The largest single buffer a thread keeps idle; bigger ones are used and then dropped.</summary>
    public const long DefaultBufferBytes = 32L << 20;

    private const int MaxRetained = 8;

    internal static long RetainedBytesLimit = DefaultRetainedBytes;
    internal static long BufferBytesLimit = DefaultBufferBytes;

    [ThreadStatic]
    private static List<float[]>? _free;

    [ThreadStatic]
    private static long _retainedBytes;

    [ThreadStatic]
    private static long _hits;

    [ThreadStatic]
    private static long _misses;

    [ThreadStatic]
    private static long _allocations;

    [ThreadStatic]
    private static long _evictions;

    /// <summary>What the calling thread currently holds, and what it has done since the process started.</summary>
    public static ScratchStatistics Statistics => new(
        _free?.Count ?? 0,
        _retainedBytes,
        _hits,
        _misses,
        _allocations,
        _evictions,
        RetainedBytesLimit,
        BufferBytesLimit);

    public static float[] Rent(int length)
    {
        var free = _free ??= new List<float[]>();
        // Best fit, not first fit: a small row request must not steal the 16 MB resize buffer, or every
        // resize call reallocates its scratch and the whole pool is pointless.
        var best = -1;
        for (var i = 0; i < free.Count; i++)
        {
            if (free[i].Length < length)
                continue;
            if (best < 0 || free[i].Length < free[best].Length)
                best = i;
        }

        if (best >= 0)
        {
            var found = free[best];
            free.RemoveAt(best);
            _retainedBytes -= (long)found.Length * sizeof(float);
            _hits++;
            return found;
        }

        _misses++;
        _allocations++;
        return GC.AllocateUninitializedArray<float>(length);
    }

    public static void Return(float[] buffer)
    {
        var bytes = (long)buffer.Length * sizeof(float);

        // Too large to keep: the caller takes the reallocating path rather than pinning a buffer the size
        // of the image for the life of the process.
        if (bytes > BufferBytesLimit)
        {
            _evictions++;
            return;
        }

        var free = _free ??= new List<float[]>();

        // Oldest first: the list only ever holds idle buffers in return order, so the head is the least
        // recently used one. Evicting the head frees the least useful memory for a returning caller.
        while (free.Count > 0 && (free.Count >= MaxRetained || _retainedBytes + bytes > RetainedBytesLimit))
        {
            _retainedBytes -= (long)free[0].Length * sizeof(float);
            free.RemoveAt(0);
            _evictions++;
        }

        free.Add(buffer);
        _retainedBytes += bytes;
    }

    /// <summary>Drops every retained buffer on the calling thread; the next Rent allocates again.</summary>
    public static void Trim()
    {
        _evictions += _free?.Count ?? 0;
        _free?.Clear();
        _retainedBytes = 0;
    }

    /// <summary>Kept for the benchmark harness, which needs the next Rent to allocate at a new size.</summary>
    internal static void Clear() => Trim();

    /// <summary>Restores the default limits and clears the calling thread's counters (tests only).</summary>
    internal static void ResetForTests()
    {
        Trim();
        RetainedBytesLimit = DefaultRetainedBytes;
        BufferBytesLimit = DefaultBufferBytes;
        _hits = 0;
        _misses = 0;
        _allocations = 0;
        _evictions = 0;
    }
}

/// <summary>One thread's scratch pool, as <c>MemoryBudget.Snapshot()</c> reports it.</summary>
/// <param name="Buffers">Idle buffers currently held.</param>
/// <param name="RetainedBytes">Their total size.</param>
/// <param name="Hits">Rents served from the pool.</param>
/// <param name="Misses">Rents that had to allocate.</param>
/// <param name="Allocations">Arrays allocated.</param>
/// <param name="Evictions">Buffers dropped by the budget, by <c>Trim</c>, or because they were too large to keep.</param>
/// <param name="RetainedBytesLimit">The idle budget in force.</param>
/// <param name="BufferBytesLimit">The largest buffer the pool will keep.</param>
public readonly record struct ScratchStatistics(
    int Buffers,
    long RetainedBytes,
    long Hits,
    long Misses,
    long Allocations,
    long Evictions,
    long RetainedBytesLimit,
    long BufferBytesLimit)
{
    /// <summary>Hits over all rents, 0 when nothing has been rented.</summary>
    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);
}
