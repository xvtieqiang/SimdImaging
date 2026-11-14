using System.Buffers;
using System.Runtime.InteropServices;

namespace SimdImaging.Tests;

/// <summary>
/// Contract tests for the default pixel pool, the piece that makes the "buffer pool / rent-and-return /
/// ownership" capability in the learning checklist mechanically checkable rather than prose.
/// <para>Everything is observed through an injected <see cref="ArrayPool{T}"/>: the small-buffer path is
/// required to go through it, the large-buffer path is required to bypass it, and disposing an owner must
/// return its array exactly once. The injected pool records calls, so the tests assert the pool's own
/// declared behaviour (IPixelPool: "one owner, one release") without reaching into private fields.</para>
/// <para>These tests are single-threaded and timing-independent: no assertion depends on scheduling or
/// on a measured byte count, so they cannot flake the way a stopwatch comparison can.</para>
/// </summary>
public class ArrayPixelPoolTests
{
    [Fact]
    public void Rent_NegativeLength_ThrowsRatherThanSilentlyClamping()
    {
        var pool = new ArrayPixelPool(new TrackingArrayPool());
        Should.Throw<ArgumentOutOfRangeException>(() => pool.Rent(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => pool.Rent(int.MinValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(ArrayPixelPool.ExactThreshold)]
    public void Rent_AlwaysReturnsAtLeastTheRequestedBytes(int length)
    {
        var pool = new ArrayPixelPool(new TrackingArrayPool());
        using var owner = pool.Rent(length);
        owner.Memory.Length.ShouldBeGreaterThanOrEqualTo(length);
    }

    [Fact]
    public void SmallRent_GoesThroughTheBackingArrayPool()
    {
        var backing = new TrackingArrayPool();
        var pool = new ArrayPixelPool(backing);

        using (var owner = pool.Rent(1234))
            owner.Memory.Length.ShouldBeGreaterThanOrEqualTo(1234);

        backing.RentCount.ShouldBe(1, "a request at/below ExactThreshold must use the ArrayPool");
        backing.ReturnCount.ShouldBe(1, "disposing the owner must return its array once");
        backing.Returned[0].ShouldBeSameAs(backing.Rented[0], "the exact rented array must be returned");
    }

    [Fact]
    public void LargeRent_BypassesThePowerOfTwoArrayPool()
    {
        var backing = new TrackingArrayPool();
        var pool = new ArrayPixelPool(backing);
        var length = ArrayPixelPool.ExactThreshold + 1;

        using (var owner = pool.Rent(length))
            owner.Memory.Length.ShouldBeGreaterThanOrEqualTo(length);

        backing.RentCount.ShouldBe(0, "large requests must come from the 1 MiB-granularity pool, not the ArrayPool");
        backing.ReturnCount.ShouldBe(0, "large buffers are retained by the exact pool, not the ArrayPool");
    }

    [Fact]
    public void Owner_DoubleDispose_ReturnsExactlyOnce()
    {
        var backing = new TrackingArrayPool();
        var pool = new ArrayPixelPool(backing);

        var owner = pool.Rent(512);
        owner.Dispose();
        owner.Dispose();

        backing.ReturnCount.ShouldBe(1, "the owner must be single-release even when Dispose is called twice");
    }

    [Fact]
    public void Owner_MemoryAfterDispose_Throws()
    {
        var pool = new ArrayPixelPool(new TrackingArrayPool());
        var owner = pool.Rent(256);
        owner.Dispose();

        Should.Throw<ObjectDisposedException>(() => _ = owner.Memory);
    }

    [Fact]
    public void LargeBuffer_IsReusedUntilTrim_ThenAllocatedAgain()
    {
        var pool = new ArrayPixelPool(new TrackingArrayPool());
        var length = ArrayPixelPool.ExactThreshold + 1;

        var first = pool.Rent(length);
        var firstArray = BackingArrayOf(first);
        first.Dispose();                                   // retained in the exact pool

        var second = pool.Rent(length);
        BackingArrayOf(second).ShouldBeSameAs(firstArray, "disposing a large buffer must retain it for reuse");
        second.Dispose();

        pool.Trim();                                       // drop every retained large buffer

        var third = pool.Rent(length);
        BackingArrayOf(third).ShouldNotBeSameAs(firstArray, "after Trim the next rent must allocate anew");
        third.Dispose();
    }

    [Fact]
    public void Name_DefaultsToTheConcreteTypeName()
    {
        IPixelPool pool = new ArrayPixelPool(new TrackingArrayPool());
        pool.Name.ShouldBe(nameof(ArrayPixelPool));
    }

    /// <summary>The byte[] behind an owner's Memory, for identity checks (no copy, no field access).</summary>
    private static byte[] BackingArrayOf(IMemoryOwner<byte> owner)
    {
        MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)owner.Memory, out var segment).ShouldBeTrue();
        return segment.Array!;
    }

    /// <summary>An ArrayPool that records Rent/Return so the pool's ownership contract is observable.
    /// It never reuses arrays itself — reuse of small arrays is ArrayPool.Shared's job, not this pool's —
    /// so a second small Rent is a distinct array and identity checks stay unambiguous.</summary>
    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        private readonly object _gate = new();
        public int RentCount;
        public int ReturnCount;
        public readonly List<byte[]> Rented = [];
        public readonly List<byte[]> Returned = [];

        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[Math.Max(1, minimumLength)];
            lock (_gate)
            {
                RentCount++;
                Rented.Add(array);
            }
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_gate)
            {
                ReturnCount++;
                Returned.Add(array);
            }
        }
    }
}
