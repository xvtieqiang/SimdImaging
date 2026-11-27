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

    /// <summary>Diagnostic: workers actually used by the most recent call on this thread. Thread-static because
    /// two plans may execute concurrently and both read it right after their own call; a shared field would report
    /// whichever call wrote last, which is a wrong number rather than a race with consequences.</summary>
    [ThreadStatic] internal static int LastWorkerCount;

    /// <summary>Diagnostic hook: minimum rows assigned to one worker (0 = 32). A same-process sweep
    /// (16/32/64/128/256/512) put 32 at the optimum: sepia 3.211→2.792 ms, rotate90 1.158→0.923 ms,
    /// invert 1.552→1.343 ms versus 128. 16 is too fine (scheduling), 512 too coarse (idle cores).</summary>
    internal static int RowsPerWorkerOverride;

    internal static int RowsPerWorker => RowsPerWorkerOverride > 0 ? RowsPerWorkerOverride : 32;

    /// <summary>
    /// Pixels below which <see cref="For(int, int, Action{int, int})"/> runs the body on the calling thread. Shared with the fusion decision:
    /// an image this small has no other way to use more than one core, so fusing is what gives it parallelism.
    /// </summary>
    internal const long MinWorkPixels = 64 * 1024;

    /// <summary>
    /// Non-zero while this thread is already inside a band of some outer parallel loop. Re-entering
    /// Parallel.For from a worker oversubscribes the pool — the outer loop has the machine saturated
    /// already — so nested work runs inline. ThreadStatic, so it cannot race across workers.
    /// </summary>
    [ThreadStatic] private static int _nesting;

    /// <summary>
    /// Non-zero while the current thread is the top-level owner of a band pump (see <see cref="EnterTopLevel"/>).
    /// In that mode the "you are inside a band already" guard does not apply: the pump runs one band at a time and
    /// the operator's own row loop is the only parallelism there is.
    /// </summary>
    [ThreadStatic] private static int _topLevel;

    /// <summary>The degree the pump was given, applied while it owns the thread (0 = the machine default).</summary>
    [ThreadStatic] private static int _pumpDegree;

    /// <summary>Marks the current thread as the pump's own thread, so <see cref="EnterNested"/> stops forcing the
    /// operator's row loop onto one thread, and so <paramref name="degree"/> reaches that loop instead of the
    /// environment being read a second time. Without the scope a streamed band would silently run single-threaded:
    /// the plan adapters call <see cref="EnterNested"/> unconditionally, and a single band has no outer loop to nest
    /// inside.</summary>
    internal static TopLevelScope EnterTopLevel(int degree)
    {
        _topLevel++;
        var previous = _pumpDegree;
        _pumpDegree = degree;
        return new TopLevelScope(previous);
    }

    /// <summary>Restores the guard on dispose. A struct so the pump's hot path allocates nothing.</summary>
    internal readonly struct TopLevelScope : IDisposable
    {
        private readonly int _previousDegree;

        internal TopLevelScope(int previousDegree) => _previousDegree = previousDegree;

        public void Dispose()
        {
            if (_topLevel > 0)
                _topLevel--;
            _pumpDegree = _previousDegree;
        }
    }

    internal static void EnterNested()
    {
        if (_topLevel == 0)
            _nesting++;
    }

    internal static void ExitNested()
    {
        if (_nesting > 0)
            _nesting--;
    }

    internal static int Degree => ResolveDegree(0);

    /// <summary>One resolution rule for execution and every workspace estimate. Zero means inherit the active
    /// pump/test override, then fall back to the machine; nested row loops always execute inline.</summary>
    internal static int ResolveDegree(int degreeOverride) =>
        _nesting > 0 ? 1
        : degreeOverride > 0 ? degreeOverride
        : _pumpDegree > 0 ? _pumpDegree
        : DegreeOverride > 0 ? DegreeOverride
        : Environment.ProcessorCount;

    /// <summary>The number of workers <see cref="For(int, int, Action{int, int})"/> can actually launch for this geometry.</summary>
    internal static int ResolveWorkers(int width, int height, int degreeOverride)
    {
        if (height <= 0 || width <= 0)
            return 1;
        var degree = ResolveDegree(degreeOverride);
        var workersByRows = Math.Max(1, (height + Math.Max(1, RowsPerWorker) - 1) /
            Math.Max(1, RowsPerWorker));
        if (degree <= 1 || (long)width * height < MinWorkPixels)
            return 1;
        return Math.Min(degree, workersByRows);
    }

    /// <summary>Same fixed-band rule for work already divided into coarse bands (blur / resize), where the
    /// band count is small but each band is expensive. Each worker owns its scratch for all its bands.</summary>
    internal static void ForBands(int count, Action<int, int> body) => ForBands(count, body, DegreeOverride, default);

    /// <summary>Explicit-degree variant: the caller passes the parallelism in (from ExecuteOptions) so a
    /// configured entry point never re-reads the environment; 0 keeps the machine default. Cancellation is
    /// cooperative and <b>fails loudly</b>: a cancelled token makes the loop throw rather than skip bands,
    /// because a skipped band would return a partially written image that looks successful.</summary>
    internal static void ForBands(int count, Action<int, int> body, int degreeOverride, CancellationToken cancellation)
    {
        var degree = ResolveDegree(degreeOverride);
        if (degree <= 1 || count <= 1)
        {
            cancellation.ThrowIfCancellationRequested();
            LastWorkerCount = 1;
            body(0, count);
            return;
        }

        var workers = Math.Min(degree, count);
        LastWorkerCount = workers;
        var band = (count + workers - 1) / workers;

        void Run(int worker)
        {
            var start = worker * band;
            if (start >= count)
                return;
            body(start, Math.Min(start + band, count));
        }

        // Only allocate ParallelOptions when there is a token to observe: the uncancellable path is the
        // hot one (every operator's own band loop) and must not grow an allocation.
        if (cancellation.CanBeCanceled)
            Parallel.For(0, workers, new ParallelOptions { CancellationToken = cancellation }, Run);
        else
            Parallel.For(0, workers, Run);
    }

    internal static void For(int width, int height, Action<int, int> body) =>
        For(width, height, body, Degree, default);

    /// <summary>Explicit-degree, cancellable variant for option-driven operators such as resize. The same
    /// resolved degree is used by workspace estimation and execution; legacy callers retain the old overload.</summary>
    internal static void For(int width, int height, Action<int, int> body, int degreeOverride,
        CancellationToken cancellation)
    {
        // A loop entered from inside a band worker runs inline, so it says nothing about how many workers the image
        // was given — and writing 1 here would overwrite the count the band split just reported.
        var nested = _nesting > 0;
        var degree = ResolveDegree(degreeOverride);
        var workers = ResolveWorkers(width, height, degreeOverride);
        if (degree <= 1 || workers <= 1)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!nested)
                LastWorkerCount = 1;
            body(0, height);
            return;
        }

        if (!nested)
            LastWorkerCount = workers;
        var band = (height + workers - 1) / workers;
        void Run(int worker)
        {
            cancellation.ThrowIfCancellationRequested();
            var start = worker * band;
            if (start >= height)
                return;
            body(start, Math.Min(start + band, height));
        }

        if (cancellation.CanBeCanceled)
            Parallel.For(0, workers, new ParallelOptions { CancellationToken = cancellation }, Run);
        else
            Parallel.For(0, workers, Run);
    }
}
