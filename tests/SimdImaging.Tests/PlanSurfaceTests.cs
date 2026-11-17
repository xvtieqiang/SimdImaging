using System.Buffers;
using System.Collections.ObjectModel;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// Two surfaces that look read-only and were not: the plan's step list, and the intermediate a geometry pass
/// replaces.
/// </summary>
public class PlanSurfaceTests
{
    /// <summary>Reading a plan's steps must not be a way to edit it.</summary>
    [Fact]
    public void PlanSteps_CannotBeCastBackToAList()
    {
        var plan = new ProcessingPlan().Add(PlanOps.Invert());
        plan.Steps.ShouldBeAssignableTo<IReadOnlyList<PlanStep>>();

        // A ReadOnlyCollection wraps the list; the underlying List is not reachable through a cast.
        plan.Steps.ShouldBeOfType<ReadOnlyCollection<PlanStep>>();
        Should.Throw<InvalidCastException>(() => _ = (List<PlanStep>)plan.Steps);
    }

    /// <summary>A geometry step after an operator pass must not strand the pass's output.</summary>
    [Fact]
    public void Geometry_AfterAPass_ReleasesTheIntermediate()
    {
        using var image = Image.Create(16, 12, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)(i * 11);

        var pool = new CountingPool();

        using (var result = image.Pipe(pool).Invert().Resize(8, 6, Resampler.Nearest).ToImage())
        {
            result.Width.ShouldBe(8);
        }

        // The invert pass's buffer is released by the geometry step, the geometry result by the using above, and
        // the copy of the source that the resize read. Nothing stays checked out.
        pool.Live.ShouldBe(0, "every buffer the pipeline rented must be back");
    }

    /// <summary>Counts what is checked out, so "released" is a number rather than a hope.</summary>
    private sealed class CountingPool : IPixelPool
    {
        private int _live;

        public int Live => Volatile.Read(ref _live);

        public IMemoryOwner<byte> Rent(int length)
        {
            Interlocked.Increment(ref _live);
            return new Owner(this, ArrayPool<byte>.Shared.Rent(length));
        }

        private sealed class Owner : IMemoryOwner<byte>
        {
            private readonly CountingPool _pool;
            private byte[]? _array;

            public Owner(CountingPool pool, byte[] array)
            {
                _pool = pool;
                _array = array;
            }

            public Memory<byte> Memory => Volatile.Read(ref _array) is { } a ? a : throw new ObjectDisposedException(nameof(Owner));

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _array, null) is not { } array)
                    return;

                ArrayPool<byte>.Shared.Return(array);
                Interlocked.Decrement(ref _pool._live);
            }
        }
    }
}

/// <summary>
/// Every entry point that runs a plan over one frame must refuse a multi-frame image rather than return frame 0.
/// Encoding already refuses, and so does Mutate; Apply used to be the exception.
/// </summary>
public class MultiFrameEntryTests
{
    private static Image TwoFrames()
    {
        var first = Image.Create(8, 6, WorkingFormat.Rgba8);
        var second = Image.Create(8, 6, WorkingFormat.Rgba8);
        first.WritablePixels.Fill(200);
        second.WritablePixels.Fill(10);

        return Image.ComposeAnimation(8, 6, [first, second]);
    }

    [Fact]
    public void Apply_RefusesAMultiFrameImage()
    {
        using var animation = TwoFrames();
        var plan = new ProcessingPlan().Add(PlanOps.Invert());

        var error = Should.Throw<NotSupportedException>(() => animation.Apply(plan));
        error.Message.ShouldContain("KeepFrames");
    }

    [Fact]
    public void ApplyWithOptions_RefusesItToo()
    {
        using var animation = TwoFrames();
        var plan = new ProcessingPlan().Add(PlanOps.Invert());

        Should.Throw<NotSupportedException>(() => animation.ApplyWithOptions(plan, new ExecuteOptions()));
    }

    [Fact]
    public void Mutate_RefusesItAsBefore()
    {
        using var animation = TwoFrames();

        Should.Throw<NotSupportedException>(() => animation.Mutate(p => p.Invert()));
    }

    /// <summary>A single frame still runs.</summary>
    [Fact]
    public void ASingleFrameStillRuns()
    {
        using var still = Image.Create(8, 6, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Invert());

        using var result = still.Apply(plan);

        result.FrameCount.ShouldBe(1);
    }
}

/// <summary>
/// The estimator's job is to promise what the plan will take. A point step on a large 16-bit image builds a
/// 512 KiB lookup table, so a declaration of zero was a promise it broke by half a megabyte.
/// </summary>
public class PointScratchEstimateTests
{
    private static ProcessingPlan OnePointStep() => new ProcessingPlan().Add(PlanOps.Invert());

    [Fact]
    public void Rgba16AboveTheFloor_DeclaresTheTable()
    {
        var estimate = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba16, OnePointStep().Steps);

        estimate.PerWorkerBytes.ShouldBeGreaterThanOrEqualTo(512 * 1024);
        estimate.UndeclaredSteps.ShouldBe(0);
    }

    /// <summary>Below the floor the operator stays on the float path and allocates no table.</summary>
    [Fact]
    public void Rgba16BelowTheFloor_DeclaresNothing()
    {
        var estimate = MemoryEstimator.Estimate(320, 200, WorkingFormat.Rgba16, OnePointStep().Steps);

        estimate.PerWorkerBytes.ShouldBe(0);
    }

    /// <summary>Eight-bit never builds the table at any size.</summary>
    [Fact]
    public void Rgba8_DeclaresNothing()
    {
        var estimate = MemoryEstimator.Estimate(1920, 1080, WorkingFormat.Rgba8, OnePointStep().Steps);

        estimate.PerWorkerBytes.ShouldBe(0);
    }
}
