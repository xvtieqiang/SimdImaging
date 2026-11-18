using System.Buffers;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Exception-injection coverage for buffer ownership in <see cref="ProcessingPlan.Execute"/>. A counting
/// <see cref="IPixelPool"/> is the observable evidence: every rent must be paired with exactly one owner
/// disposal, on both the normal and every throwing path. Since M10 the fused path rents one full-size
/// output plus two band windows per worker, so the counts are asserted against that model
/// (<see cref="CountingPool.FullRentCount"/> = image-sized, <see cref="CountingPool.WindowRentCount"/> =
/// band-sized) rather than a magic total.
/// </summary>
public class ProcessingPlanLeakTests
{
    private const int Width = 8;
    private const int Height = 6;
    private const int FullBytes = Width * Height * 4;

    private static OpKernel CopyRows => (source, destination, rect, _) =>
    {
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            source.Row(y).CopyTo(destination.Row(y));
    };

    private static readonly OpKernel ThrowingKernel = (_, _, _, _) =>
        throw new InvalidOperationException("injected kernel failure");

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch
        {
            // Parallel.For wraps worker exceptions in AggregateException; the shape is irrelevant
            // here, only that control left Execute through the exceptional path.
            return true;
        }
    }

    [Fact]
    public void Execute_NormalRun_PairsEveryRentWithExactlyOneDispose()
    {
        var pool = new CountingPool();
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan { BandRows = 2 }
            .Add("first", OpCategory.Point, CopyRows)
            .Add("second", OpCategory.Point, CopyRows)
            .Add("third", OpCategory.Global, CopyRows);
        // The counts below describe the fused rent pattern, so fusion is asked for explicitly now that the
        // executor fuses on its own only where it pays.
        var options = new ExecuteOptions { MaxDegreeOfParallelism = 2, ForceFusion = true };

        using (plan.ExecuteWithOptions(source.View, options, pool))
        {
            // Six rows / two-row bands = three bands, capped at two workers: the fused pair rents one
            // full output plus two windows per worker; the global step then rents its own output.
            pool.FullRentCount.ShouldBe(2);
            pool.WindowRentCount.ShouldBe(4);
            pool.DisposeCount.ShouldBe(pool.RentCount - 1); // only the returned frame is still alive
        }

        pool.DisposeCount.ShouldBe(pool.RentCount);
        pool.DuplicateDisposeCount.ShouldBe(0);
        pool.AllOwnersDisposedExactlyOnce().ShouldBeTrue();
    }

    [Fact]
    public void Execute_FusedPair_IsByteIdenticalToTheStepByStepPath()
    {
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        new Random(90210).NextBytes(source.View.Span);
        var plan = new ProcessingPlan { BandRows = 2 }
            .Add("first", OpCategory.Point, CopyRows)
            .Add("second", OpCategory.Neighborhood, CopyRows, 1, 1);

        using var fused = plan.ExecuteWithOptions(source.View,
            new ExecuteOptions { MaxDegreeOfParallelism = 2 });
        using var stepByStep = plan.ExecuteWithOptions(source.View,
            new ExecuteOptions { AllowFusion = false });

        fused.View.Span.ToArray().ShouldBe(stepByStep.View.Span.ToArray());
    }

    [Fact]
    public void Execute_GlobalKernelThrows_ReleasesPendingAndPreviousFrames()
    {
        var pool = new CountingPool();
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan()
            .Add("ok", OpCategory.Point, CopyRows)
            .Add("throws", OpCategory.Global, ThrowingKernel);

        Throws(() => plan.Execute(source.View, pool)).ShouldBeTrue();

        pool.FullRentCount.ShouldBe(2);
        pool.WindowRentCount.ShouldBe(0);
        pool.DisposeCount.ShouldBe(pool.RentCount);
        pool.DuplicateDisposeCount.ShouldBe(0);
        pool.AllOwnersDisposedExactlyOnce().ShouldBeTrue();
    }

    [Fact]
    public void Execute_BandedKernelThrows_ReleasesPendingAndPreviousFrames()
    {
        var pool = new CountingPool();
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan { BandRows = 2 }
            .Add("ok", OpCategory.Point, CopyRows)
            .Add("band-throws", OpCategory.Neighborhood, ThrowingKernel, 1, 1);
        var options = new ExecuteOptions { MaxDegreeOfParallelism = 1 };

        Throws(() => plan.ExecuteWithOptions(source.View, options, pool)).ShouldBeTrue();

        // One fused output plus one window pair; the segment's output must be released on the throw.
        pool.FullRentCount.ShouldBe(1);
        pool.WindowRentCount.ShouldBe(2);
        pool.DisposeCount.ShouldBe(pool.RentCount);
        pool.DuplicateDisposeCount.ShouldBe(0);
        pool.AllOwnersDisposedExactlyOnce().ShouldBeTrue();
    }

    [Fact]
    public void Execute_ObserverThrows_ReleasesPendingAndPreviousFrames()
    {
        var pool = new CountingPool();
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan()
            .Add("ok", OpCategory.Point, CopyRows)
            .Add("also-ok", OpCategory.Point, CopyRows);
        PlanObserver observer = (index, _, _) =>
        {
            if (index == 1) throw new InvalidOperationException("injected observer failure");
        };

        Throws(() => plan.Execute(source.View, pool, observer)).ShouldBeTrue();

        // The observer forces the step-by-step path, so each step rents one full frame; step 0's frame
        // is `owned`, step 1's is still `pending` when the observer throws. Both must be released.
        pool.FullRentCount.ShouldBe(2);
        pool.WindowRentCount.ShouldBe(0);
        pool.DisposeCount.ShouldBe(pool.RentCount);
        pool.DuplicateDisposeCount.ShouldBe(0);
        pool.AllOwnersDisposedExactlyOnce().ShouldBeTrue();
    }

    [Fact]
    public void Execute_EmptyPlanCopy_IsPairedAndByteExact()
    {
        var pool = new CountingPool();
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        new Random(4711).NextBytes(source.View.Span);
        var expected = source.View.Span.ToArray();

        using (var copy = new ProcessingPlan().Execute(source.View, pool))
        {
            copy.View.Span.ToArray().ShouldBe(expected);
            pool.DisposeCount.ShouldBe(0);
        }

        pool.RentCount.ShouldBe(1);
        pool.DisposeCount.ShouldBe(1);
        pool.DuplicateDisposeCount.ShouldBe(0);
    }

    /// <summary>Deterministic, thread-safe pool: fused segments rent from band workers, so every counter
    /// transition happens under one gate and disposal is recorded centrally.</summary>
    private sealed class CountingPool : IPixelPool
    {
        private readonly List<CountingOwner> _owners = [];
        private readonly Lock _gate = new();

        public int RentCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int DuplicateDisposeCount { get; private set; }
        public int FullRentCount { get; private set; }
        public int WindowRentCount { get; private set; }

        public IMemoryOwner<byte> Rent(int length)
        {
            lock (_gate)
            {
                RentCount++;
                if (length == FullBytes)
                    FullRentCount++;
                else
                    WindowRentCount++;

                var owner = new CountingOwner(new byte[length], this);
                _owners.Add(owner);
                return owner;
            }
        }

        public bool AllOwnersDisposedExactlyOnce()
        {
            lock (_gate)
            {
                foreach (var owner in _owners)
                    if (owner.DisposeCalls != 1) return false;
                return true;
            }
        }

        private void OnDispose(CountingOwner owner)
        {
            lock (_gate)
            {
                owner.DisposeCalls++;
                DisposeCount++;
                if (owner.DisposeCalls != 1)
                    DuplicateDisposeCount++;
            }
        }

        private sealed class CountingOwner(byte[] array, CountingPool pool) : IMemoryOwner<byte>
        {
            public int DisposeCalls { get; set; }

            public Memory<byte> Memory => array;

            public void Dispose() => pool.OnDispose(this);
        }
    }
}
