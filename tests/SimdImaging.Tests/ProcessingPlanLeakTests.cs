using System.Buffers;
using System.Collections.Concurrent;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;
using SimdImaging.Processing.Contracts;

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
            // full output plus ONE window per worker — a halo-free segment ping-pongs between the output and
            // that window — and the global step then rents its own output.
            pool.FullRentCount.ShouldBe(2);
            pool.WindowRentCount.ShouldBe(2);
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
    public void RowParallel_DefaultDegreeAndWorkspaceResolutionShareTheTestOverride()
    {
        var previousDegree = RowParallel.DegreeOverride;
        var previousRows = RowParallel.RowsPerWorkerOverride;
        try
        {
            RowParallel.DegreeOverride = 2;
            RowParallel.RowsPerWorkerOverride = 1;

            RowParallel.ResolveDegree(0).ShouldBe(2);
            RowParallel.ResolveWorkers(20_000, 4, 0).ShouldBe(2);
            RowParallel.ResolveWorkers(20_000, 4, 3).ShouldBe(3);
            RowParallel.ResolveWorkers(100, 4, 3).ShouldBe(1,
                "small work stays single-threaded even when the requested degree is larger");
        }
        finally
        {
            RowParallel.DegreeOverride = previousDegree;
            RowParallel.RowsPerWorkerOverride = previousRows;
        }
    }

    [Fact]
    public void Execute_WholeImagePartition_IsInvokedOnceWithTheFullRectangle()
    {
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        var calls = 0;
        var contract = OperatorContract.Create("whole-partition", OpCategory.Neighborhood,
            WorkingFormatMask.All, IdentityFootprint.Instance, WorkspaceModel.None,
            requirements: ExecutionRequirements.WholeImage);
        var op = new ImageOp(contract, new KernelImplementations((input, output, destination, _) =>
        {
            Interlocked.Increment(ref calls);
            destination.ShouldBe(new PixelRect(0, 0, Width, Height));
            CopyRows(input, output, destination, destination);
        }));

        using var result = new ProcessingPlan { BandRows = 1 }.Add(op).ExecuteWithOptions(source.View,
            new ExecuteOptions { MaxDegreeOfParallelism = 4 });

        calls.ShouldBe(1);
        result.View.Span.ToArray().ShouldBe(source.View.Span.ToArray());
    }

    [Fact]
    public void Execute_SingleThreadContract_RunsOneWholeImageCall()
    {
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        var active = 0;
        var maxActive = 0;
        var calls = 0;
        var contract = OperatorContract.Create("single-thread", OpCategory.Neighborhood,
            WorkingFormatMask.All, IdentityFootprint.Instance, WorkspaceModel.None,
            requirements: ExecutionRequirements.RowBands with { Threading = ThreadSafety.SingleThread });
        var op = new ImageOp(contract, new KernelImplementations((input, output, destination, _) =>
        {
            Interlocked.Increment(ref calls);
            destination.ShouldBe(new PixelRect(0, 0, Width, Height));
            var now = Interlocked.Increment(ref active);
            UpdateMaximum(ref maxActive, now);
            Thread.SpinWait(20_000);
            CopyRows(input, output, destination, destination);
            Interlocked.Decrement(ref active);
        }));

        using var result = new ProcessingPlan { BandRows = 1 }.Add(op).ExecuteWithOptions(source.View,
            new ExecuteOptions { MaxDegreeOfParallelism = 4 });

        calls.ShouldBe(1);
        maxActive.ShouldBe(1);
        result.View.Span.ToArray().ShouldBe(source.View.Span.ToArray());
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    [Fact]
    public void Execute_FusedHaloChain_IsByteIdenticalAcrossBandHeightsAndWorkerCounts()
    {
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        new Random(77123).NextBytes(source.View.Span);
        using var reference = new ProcessingPlan { BandRows = 2 }
            .Add("vertical-a", OpCategory.Neighborhood, VerticalThreeForTest, haloY: 1)
            .Add("vertical-b", OpCategory.Neighborhood, VerticalThreeForTest, haloY: 1)
            .ExecuteWithOptions(source.View,
                new ExecuteOptions { AllowFusion = false, MaxDegreeOfParallelism = 1 });
        var expected = reference.View.Span.ToArray();

        foreach (var bandRows in new[] { 1, 2, 3, 5, 16 })
        foreach (var degree in new[] { 1, 2, 4 })
        {
            using var actual = new ProcessingPlan { BandRows = bandRows }
                .Add("vertical-a", OpCategory.Neighborhood, VerticalThreeForTest, haloY: 1)
                .Add("vertical-b", OpCategory.Neighborhood, VerticalThreeForTest, haloY: 1)
                .ExecuteWithOptions(source.View,
                    new ExecuteOptions { ForceFusion = true, MaxDegreeOfParallelism = degree });
            actual.View.Span.ToArray().ShouldBe(expected,
                $"fused halo chain changed with bandRows={bandRows}, degree={degree}");
        }
    }

    private static void VerticalThreeForTest(PixelView source, PixelView destination,
        PixelRect destinationRect, PixelRect _)
    {
        for (var y = destinationRect.Y; y < destinationRect.Y + destinationRect.Height; y++)
        for (var x = destinationRect.X; x < destinationRect.X + destinationRect.Width; x++)
        for (var channel = 0; channel < 4; channel++)
        {
            var sum = 0;
            for (var offset = -1; offset <= 1; offset++)
                sum += source.Row(Math.Clamp(y + offset, 0, source.Height - 1))[x * 4 + channel];
            destination.Row(y)[x * 4 + channel] = (byte)(sum / 3);
        }
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

    [Fact]
    public void TrailingResize_RejectsWorkingSetBeforeRentingDestination()
    {
        var pool = new CountingPool();
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Resize(4, 3, SimdImaging.Processing.Resampler.Bicubic);

        Should.Throw<InvalidOperationException>(() =>
            plan.ExecuteWithOptions(source.View,
                new ExecuteOptions { MaxWorkingBytes = 500, MaxDegreeOfParallelism = 1 }, pool));

        // The empty-plan copy is allowed (192 bytes); the resize estimate is refused before its output is rented.
        pool.RentCount.ShouldBe(1);
        pool.DisposeCount.ShouldBe(1);
        pool.DuplicateDisposeCount.ShouldBe(0);
    }

    [Fact]
    public void TrailingResize_Cancellation_ReleasesBothIntermediateAndDestination()
    {
        var pool = new CountingPool();
        using var source = WorkingBuffer.Rent(Width, Height, WorkingFormat.Rgba8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var plan = new ProcessingPlan().Resize(4, 3, SimdImaging.Processing.Resampler.Nearest);

        Should.Throw<OperationCanceledException>(() =>
            plan.ExecuteWithOptions(source.View,
                new ExecuteOptions { CancellationToken = cancellation.Token, MaxDegreeOfParallelism = 1 },
                pool));

        pool.DisposeCount.ShouldBe(pool.RentCount);
        pool.DuplicateDisposeCount.ShouldBe(0);
        pool.AllOwnersDisposedExactlyOnce().ShouldBeTrue();
    }

    /// <summary>Deterministic, thread-safe pool: fused segments rent from band workers, so every counter
    /// transition happens under one gate and disposal is recorded centrally.</summary>
    [Fact]
    public void WholeImageOperator_UsesExecuteOptionsMaxDegreeOfParallelism()
    {
        using var image = Image.Create(256, 256, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan { BandRows = 256 }.Add(PlanOps.Clahe(tilesX: 4, tilesY: 8));

        using var serial = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { MaxDegreeOfParallelism = 1 });
        RowParallel.LastWorkerCount.ShouldBe(1);

        using var parallel = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { MaxDegreeOfParallelism = 2 });
        RowParallel.LastWorkerCount.ShouldBe(2);
        parallel.View.Span.ToArray().ShouldBe(serial.View.Span.ToArray());
    }
    [Fact]
    public void StepwiseBandsAndWorkspaceEstimateFollowExecuteOptionsDegree()
    {
        const int width = 64;
        const int height = 1080;
        var previousDegree = RowParallel.DegreeOverride;
        var observedRows = new ConcurrentBag<int>();
        try
        {
            RowParallel.DegreeOverride = 8;
            var contract = OperatorContract.Create("stepwise-band-probe", OpCategory.Point,
                WorkingFormatMask.All, IdentityFootprint.Instance,
                WorkspaceModel.Of((w, _, rows, format) => (long)w * rows * format.BytesPerPixel()),
                requirements: ExecutionRequirements.RowBands);
            var op = new ImageOp(contract, new KernelImplementations((source, destination, rect, _) =>
            {
                observedRows.Add(rect.Height);
                for (var y = 0; y < rect.Height; y++)
                    source.Row(rect.Y + y).Slice(rect.X * 4, rect.Width * 4)
                        .CopyTo(destination.Row(rect.Y + y).Slice(rect.X * 4));
            }));
            var plan = new ProcessingPlan().Add(op);
            using var source = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba8);

            var estimate = MemoryEstimator.Estimate(source.View, plan, new ExecuteOptions { MaxDegreeOfParallelism = 2 });
            using var result = plan.ExecuteWithOptions(source.View,
                new ExecuteOptions { AllowFusion = false, MaxDegreeOfParallelism = 2 });

            observedRows.Order().ToArray().ShouldBe(new[] { height / 2, height / 2 });
            estimate.Workers.ShouldBe(2);
            estimate.PerWorkerBytes.ShouldBe((long)width * (height / 2) * WorkingFormat.Rgba8.BytesPerPixel());
            estimate.PerWorkerPeakBytes.ShouldBe(2L * estimate.PerWorkerBytes);
        }
        finally
        {
            RowParallel.DegreeOverride = previousDegree;
        }
    }
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
