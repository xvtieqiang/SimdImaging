using System.Buffers;
using System.Collections.Concurrent;

namespace SimdImaging.Abstractions;

/// <summary>
/// Where pixel storage comes from. Every buffer a stage allocates goes through one of these, so a pipeline
/// can run at zero steady-state allocation and a test can assert it.
/// <para>
/// Rent returns an owner: disposing the owner returns the memory. There is deliberately no separate
/// <c>Return</c> — one owner, one release, so a buffer cannot be returned twice.
/// </para>
/// </summary>
public interface IPixelPool
{
    /// <summary>Rents at least <paramref name="length"/> bytes. Contents are undefined.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    IMemoryOwner<byte> Rent(int length);

    /// <summary>A human-readable name, for diagnostics.</summary>
    string Name => GetType().Name;
}

/// <summary>
/// The default pool. Small buffers come from <see cref="ArrayPool{T}.Shared"/>, which buckets to powers of
/// two; buffers above <see cref="ExactThreshold"/> come from a 1 MiB-granularity pool instead.
/// <para>
/// <b>Why the split.</b> <see cref="ArrayPool{T}.Shared"/> rounds every request up to the next power of two
/// <i>and</i> retains what it rounds, without ever shrinking. For pixel work that is the worst possible
/// shape: a 4K Rgba8 frame is 33 MiB, which lands in the 64 MiB bucket — one live frame costs two. Measured
/// on a six-step 4K chain, the shared pool held 462 MiB of managed heap where the plan itself needs two
/// frames (66 MiB). Above 4 MiB the rounding waste exceeds the allocation saving, so those requests are
/// served from buckets sized to whole mebibytes and capped at <see cref="MaxPerSize"/> retained arrays.
/// </para>
/// <para>Below the threshold the per-request waste is at most a few hundred KiB and the shared pool's
/// cross-process reuse is worth more than exact sizing.</para>
/// </summary>
public sealed class ArrayPixelPool : IPixelPool
{
    /// <summary>The process-wide instance.</summary>
    public static ArrayPixelPool Shared { get; } = new();

    /// <summary>Requests above this many bytes bypass the power-of-two pool.</summary>
    public const int ExactThreshold = 4 * 1024 * 1024;

    /// <summary>Retained arrays per exact size; two covers a ping-pong pair.</summary>
    public const int MaxPerSize = 2;

    private const int GranuleShift = 20; // 1 MiB

    private readonly ArrayPool<byte> _pool;
    private readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> _exact = new();

    /// <summary>Creates a pool over <paramref name="pool"/>, or <see cref="ArrayPool{T}.Shared"/> when null.</summary>
    public ArrayPixelPool(ArrayPool<byte>? pool = null) => _pool = pool ?? ArrayPool<byte>.Shared;

    /// <inheritdoc />
    public IMemoryOwner<byte> Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length <= ExactThreshold)
            return new Owner(_pool, _pool.Rent(length));

        var granules = (length + (1 << GranuleShift) - 1) >> GranuleShift;
        var bag = _exact.GetOrAdd(granules, static _ => new ConcurrentBag<byte[]>());
        return bag.TryTake(out var reused)
            ? new ExactOwner(bag, reused)
            : new ExactOwner(bag, new byte[granules << GranuleShift]);
    }

    /// <summary>Drops every retained large buffer; the next rent allocates again.</summary>
    public void Trim()
    {
        foreach (var bag in _exact.Values)
            while (bag.TryTake(out _)) { }
    }

    private sealed class Owner : IMemoryOwner<byte>
    {
        private ArrayPool<byte>? _pool;
        private byte[]? _array;

        public Owner(ArrayPool<byte> pool, byte[] array)
        {
            _pool = pool;
            _array = array;
        }

        public Memory<byte> Memory => Volatile.Read(ref _array) is { } array
            ? array
            : throw new ObjectDisposedException(nameof(ArrayPixelPool));

        public void Dispose()
        {
            var pool = Interlocked.Exchange(ref _pool, null);
            var array = Interlocked.Exchange(ref _array, null);
            if (pool is not null && array is not null)
                pool.Return(array);
        }
    }

    private sealed class ExactOwner : IMemoryOwner<byte>
    {
        private ConcurrentBag<byte[]>? _bag;
        private byte[]? _array;

        public ExactOwner(ConcurrentBag<byte[]> bag, byte[] array)
        {
            _bag = bag;
            _array = array;
        }

        public Memory<byte> Memory => Volatile.Read(ref _array) is { } array
            ? array
            : throw new ObjectDisposedException(nameof(ArrayPixelPool));

        public void Dispose()
        {
            var bag = Interlocked.Exchange(ref _bag, null);
            var array = Interlocked.Exchange(ref _array, null);

            // Count is approximate under concurrency, so a burst can briefly retain one extra array. That is
            // the safe direction: over-retaining by one is bounded, under-retaining would need a lock.
            if (bag is not null && array is not null && bag.Count < MaxPerSize)
                bag.Add(array);
        }
    }
}
