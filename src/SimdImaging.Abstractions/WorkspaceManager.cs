namespace SimdImaging.Abstractions;

/// <summary>
/// A reservation account for engine-managed working memory. Components reserve the bytes they are about to hold and
/// release them when the buffer is gone; the manager records the concurrent peak and can refuse a reservation above
/// its limit. It is a reservation account, not a process-wide cap: callers, codecs and the GC are outside it.
/// </summary>
public sealed class WorkspaceManager
{
    private readonly long _limit;
    private long _outstanding;
    private long _peak;

    /// <summary>A manager with an optional limit; 0 means no limit.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limitBytes"/> is negative.</exception>
    public WorkspaceManager(long limitBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
        _limit = limitBytes;
    }

    /// <summary>The limit passed to the constructor; 0 when unlimited.</summary>
    public long LimitBytes => _limit;

    /// <summary>Bytes currently reserved and not released.</summary>
    public long OutstandingBytes => Interlocked.Read(ref _outstanding);

    /// <summary>The largest <see cref="OutstandingBytes"/> observed.</summary>
    public long PeakBytes => Interlocked.Read(ref _peak);

    /// <summary>
    /// Reserves <paramref name="bytes"/> until the returned lease is disposed. Disposing twice is harmless.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The reservation would exceed <see cref="LimitBytes"/>.</exception>
    public IDisposable Reserve(long bytes, string name = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes == 0)
            return new Lease(this, 0);

        long outstanding;
        while (true)
        {
            var current = Interlocked.Read(ref _outstanding);
            if (bytes > long.MaxValue - current)
                throw new InvalidOperationException(
                    $"Reserving {bytes} bytes for '{name}' would overflow the workspace reservation total.");

            outstanding = current + bytes;
            if (_limit > 0 && outstanding > _limit)
                throw new InvalidOperationException(
                    $"Reserving {bytes} bytes for '{name}' would hold {outstanding} bytes, above the workspace limit " +
                    $"of {_limit} bytes.");

            if (Interlocked.CompareExchange(ref _outstanding, outstanding, current) == current)
                break;
        }

        var peak = Interlocked.Read(ref _peak);
        while (outstanding > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peak, outstanding, peak);
            if (seen == peak)
                break;
            peak = seen;
        }

        return new Lease(this, bytes);
    }

    private sealed class Lease(WorkspaceManager owner, long bytes) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.Add(ref owner._outstanding, -bytes);
        }
    }
}
