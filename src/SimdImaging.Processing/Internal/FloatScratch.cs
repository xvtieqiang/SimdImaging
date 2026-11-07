namespace SimdImaging.Processing.Internal;

/// <summary>
/// Thread-local reusable float scratch. <c>ArrayPool&lt;float&gt;.Shared</c> caps a bucket at roughly one
/// million elements (~4 MB), so the 16 MB resize scratch would never be pooled and would be allocated on
/// every call. This pool has no size ceiling; each Rent removes a buffer from the free list so nested
/// operators cannot overwrite each other's scratch.
/// <para>Steady state is zero managed allocation once a thread has warmed up. Buffers are uninitialised,
/// so every caller must fully write the region it reads.</para>
/// </summary>
internal static class FloatScratch
{
    private const int MaxRetained = 8;

    [ThreadStatic]
    private static List<float[]>? _free;

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
            return found;
        }

        return GC.AllocateUninitializedArray<float>(length);
    }

    public static void Return(float[] buffer)
    {
        var free = _free ??= new List<float[]>();
        if (free.Count < MaxRetained)
            free.Add(buffer);
    }

    /// <summary>Drops every retained buffer. Only used when a benchmark changes the scratch budget and
    /// needs the next Rent to allocate at the new size rather than reuse an oversized buffer.</summary>
    internal static void Clear() => _free?.Clear();
}
