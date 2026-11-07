namespace SimdImaging.Processing.Internal;

/// <summary>
/// Fixed row bands, no work stealing — architecture.md §6.2. Every output row depends only on input
/// rows, so the worker count cannot change a single output byte.
/// <para>The decision to parallelise is by WORK, not by height: a 4000×200 image is 800 K pixels and
/// deserves workers even though it has fewer than 256 rows. Row count only caps how many workers can
/// be useful (one per ~128 rows). The old height-only rule left wide, short images single-threaded.</para>
/// </summary>
internal static class RowParallel
{
    /// <summary>Test hook / caller override. 0 = use the machine default.</summary>
    internal static int DegreeOverride;

    /// <summary>Diagnostic: workers actually used by the most recent call on this thread.</summary>
    internal static int LastWorkerCount;

    /// <summary>Diagnostic hook: minimum rows assigned to one worker (0 = 32). A same-process sweep
    /// (16/32/64/128/256/512) put 32 at the optimum: sepia 3.211→2.792 ms, rotate90 1.158→0.923 ms,
    /// invert 1.552→1.343 ms versus 128. 16 is too fine (scheduling), 512 too coarse (idle cores).</summary>
    internal static int RowsPerWorkerOverride;

    internal static int RowsPerWorker => RowsPerWorkerOverride > 0 ? RowsPerWorkerOverride : 32;

    /// <summary>
    /// Non-zero while this thread is already inside a band of some outer parallel loop. Re-entering
    /// Parallel.For from a worker oversubscribes the pool — the outer loop has the machine saturated
    /// already — so nested work runs inline. ThreadStatic, so it cannot race across workers.
    /// </summary>
    [ThreadStatic] private static int _nesting;

    internal static void EnterNested() => _nesting++;

    internal static void ExitNested()
    {
        if (_nesting > 0)
            _nesting--;
    }

    internal static int Degree => _nesting > 0
        ? 1
        : DegreeOverride > 0 ? DegreeOverride : Environment.ProcessorCount;

    /// <summary>Same fixed-band rule for work already divided into coarse bands (blur / resize), where the
    /// band count is small but each band is expensive. Each worker owns its scratch for all its bands.</summary>
    internal static void ForBands(int count, Action<int, int> body)
    {
        var degree = Degree;
        if (degree <= 1 || count <= 1)
        {
            LastWorkerCount = 1;
            body(0, count);
            return;
        }

        var workers = Math.Min(degree, count);
        LastWorkerCount = workers;
        var band = (count + workers - 1) / workers;
        Parallel.For(0, workers, worker =>
        {
            var start = worker * band;
            if (start >= count)
                return;
            body(start, Math.Min(start + band, count));
        });
    }

    internal static void For(int width, int height, Action<int, int> body)
    {
        var degree = Degree;
        const long minWorkPixels = 64 * 1024;
        var perWorker = Math.Max(1, RowsPerWorker);
        var workers = Math.Min(degree, Math.Max(1, (height + perWorker - 1) / perWorker));
        if (degree <= 1 || (long)width * height < minWorkPixels || workers <= 1)
        {
            LastWorkerCount = 1;
            body(0, height);
            return;
        }

        LastWorkerCount = workers;
        var band = (height + workers - 1) / workers;
        Parallel.For(0, workers, worker =>
        {
            var start = worker * band;
            if (start >= height)
                return;
            body(start, Math.Min(start + band, height));
        });
    }
}
