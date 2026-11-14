using System.Buffers;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Fusion;

/// <summary>
/// Rents/dispose counting wrapper used by <c>ExecuteOptions.Diagnostics</c>; behaviourally transparent.
/// Thread-safe by construction: fused segments rent from band workers, so the counters are interlocked
/// and the owner list is never read during execution.
/// </summary>
internal sealed class CountingPool : IPixelPool
{
    private readonly IPixelPool _inner;
    private int _rents;
    private int _returns;
    private long _outstanding;
    private long _peak;

    public CountingPool(IPixelPool inner) => _inner = inner;

    public int Rents => Volatile.Read(ref _rents);

    public int Returns => Volatile.Read(ref _returns);

    /// <summary>Bytes rented and not yet released.</summary>
    public long OutstandingBytes => Interlocked.Read(ref _outstanding);

    /// <summary>
    /// The largest <see cref="OutstandingBytes"/> ever observed — the plan's own working set, measured
    /// rather than estimated. <c>MemoryEstimator</c> promises never to report less than this.
    /// </summary>
    public long PeakBytes => Interlocked.Read(ref _peak);

    public IMemoryOwner<byte> Rent(int length)
    {
        Interlocked.Increment(ref _rents);

        var owner = _inner.Rent(length);
        var outstanding = Interlocked.Add(ref _outstanding, length);

        var peak = Interlocked.Read(ref _peak);
        while (outstanding > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peak, outstanding, peak);
            if (seen == peak)
                break;

            peak = seen;
        }

        return new CountingOwner(this, owner, length);
    }

    private sealed class CountingOwner(CountingPool owner, IMemoryOwner<byte> inner, int length) : IMemoryOwner<byte>
    {
        private int _disposed;

        public Memory<byte> Memory => inner.Memory;

        public void Dispose()
        {
            // A double dispose must not corrupt the counters: the underlying owner decides what a
            // second dispose means, we only count the transition.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Increment(ref owner._returns);
                Interlocked.Add(ref owner._outstanding, -length);
            }

            inner.Dispose();
        }
    }
}
