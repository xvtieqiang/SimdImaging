namespace SimdImaging.Processing.Internal;

/// <summary>
/// Thread-local reusable double scratch, the double twin of <see cref="FloatScratch"/>. The EWA weight
/// grid needs double precision for its single-rounding normalisation (double divide, one float
/// truncation at the accumulator), and <c>ArrayPool&lt;double&gt;</c> would cap the bucket at 1M elements.
/// <para>Steady state is zero managed allocation once a thread has warmed up. Buffers are uninitialised,
/// so every caller must fully write the region it reads.</para>
/// </summary>
internal static class DoubleScratch
{
    private const int MaxRetained = 8;

    [ThreadStatic]
    private static List<double[]>? _free;

    public static double[] Rent(int length)
    {
        var free = _free ??= new List<double[]>();
        // Best fit, same reasoning as FloatScratch: a small weight grid must not steal a larger buffer
        // another caller is about to reuse.
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

        return GC.AllocateUninitializedArray<double>(length);
    }

    public static void Return(double[] buffer)
    {
        var free = _free ??= new List<double[]>();
        if (free.Count < MaxRetained)
            free.Add(buffer);
    }
}
