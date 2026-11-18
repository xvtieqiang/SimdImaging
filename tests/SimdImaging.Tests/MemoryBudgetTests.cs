using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// The budgets themselves: what the scratch pool keeps, what the weight cache keeps, and that changing
/// either cannot change a single output byte.
/// </summary>
public class MemoryBudgetTests : IDisposable
{
    public MemoryBudgetTests() => MemoryBudget.ResetForTests();

    public void Dispose() => MemoryBudget.ResetForTests();

    [Fact]
    public void Scratch_ReusesABufferAndCountsTheHit()
    {
        FloatScratch.Return(new float[4096]);
        var rented = FloatScratch.Rent(4096);

        rented.Length.ShouldBe(4096);
        MemoryBudget.Snapshot().Scratch.Hits.ShouldBe(1);
        MemoryBudget.Snapshot().Scratch.Misses.ShouldBe(0);
    }

    [Fact]
    public void Scratch_BestFitDoesNotHandASmallRequestTheBigBuffer()
    {
        FloatScratch.Return(new float[100_000]);
        FloatScratch.Return(new float[1024]);

        var small = FloatScratch.Rent(512);
        small.Length.ShouldBe(1024, "a row-sized request must not take the resize-sized buffer");

        var big = FloatScratch.Rent(50_000);
        big.Length.ShouldBe(100_000);
    }

    [Fact]
    public void Scratch_DoesNotRetainABufferAboveTheSingleBufferCap()
    {
        MemoryBudget.SetLimits(scratchRetainedBytes: 8L << 20, scratchBufferBytes: 64L << 10);

        FloatScratch.Return(new float[256 * 1024]);   // 1 MB, over the 64 KiB cap

        var stats = MemoryBudget.Snapshot().Scratch;
        stats.Buffers.ShouldBe(0);
        stats.RetainedBytes.ShouldBe(0);
        stats.Evictions.ShouldBe(1);
    }

    [Fact]
    public void Scratch_StaysWithinTheRetainedBudget()
    {
        MemoryBudget.SetLimits(scratchRetainedBytes: 3L << 20, scratchBufferBytes: 1L << 20);

        for (var i = 0; i < 5; i++)
            FloatScratch.Return(new float[256 * 1024]);   // 1 MB each

        var stats = MemoryBudget.Snapshot().Scratch;
        stats.RetainedBytes.ShouldBeLessThanOrEqualTo(3L << 20);
        stats.Buffers.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public void Scratch_TrimReleasesEverythingRetained()
    {
        FloatScratch.Return(new float[4096]);
        FloatScratch.Return(new float[8192]);

        MemoryBudget.Trim();

        var stats = MemoryBudget.Snapshot().Scratch;
        stats.Buffers.ShouldBe(0);
        stats.RetainedBytes.ShouldBe(0);
    }

    [Fact]
    public void WeightCache_HitsAfterTheFirstBuild()
    {
        var first = WeightTable.Build(320, 160, Resampler.Bicubic);
        var again = WeightTable.Build(320, 160, Resampler.Bicubic);

        again.ShouldBeSameAs(first);

        var stats = MemoryBudget.Snapshot().WeightCache;
        stats.Hits.ShouldBe(1);
        stats.Misses.ShouldBe(1);
        stats.Entries.ShouldBe(1);
        stats.Bytes.ShouldBe(first.Bytes);
        stats.Bytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void WeightCache_EvictsTheLeastRecentlyUsedEntry()
    {
        MemoryBudget.SetLimits(weightCacheBytes: 64L << 20, weightCacheEntries: 2);

        WeightTable.Build(100, 50, Resampler.Bilinear);
        WeightTable.Build(200, 100, Resampler.Bilinear);
        WeightTable.Build(100, 50, Resampler.Bilinear);   // hit: the first entry becomes the recent one
        WeightTable.Build(300, 150, Resampler.Bilinear);  // evicts the second

        MemoryBudget.Snapshot().WeightCache.Entries.ShouldBe(2);

        var misses = MemoryBudget.Snapshot().WeightCache.Misses;
        WeightTable.Build(100, 50, Resampler.Bilinear);   // still cached
        MemoryBudget.Snapshot().WeightCache.Misses.ShouldBe(misses);

        WeightTable.Build(200, 100, Resampler.Bilinear);  // was evicted, must rebuild
        MemoryBudget.Snapshot().WeightCache.Misses.ShouldBe(misses + 1);
    }

    [Fact]
    public void WeightCache_StaysWithinTheByteBudget()
    {
        var probe = WeightTable.Build(1000, 500, Resampler.Lanczos3);
        MemoryBudget.SetLimits(weightCacheBytes: probe.Bytes * 2, weightCacheEntries: 64);

        for (var i = 1; i <= 6; i++)
            WeightTable.Build(1000 + i, 500, Resampler.Lanczos3);

        MemoryBudget.Snapshot().WeightCache.Bytes.ShouldBeLessThanOrEqualTo(probe.Bytes * 2);
    }

    [Fact]
    public void SetLimits_RejectsASingleBufferAboveTheRetainedBudget()
    {
        Should.Throw<ArgumentException>(() =>
            MemoryBudget.SetLimits(scratchRetainedBytes: 1 << 20, scratchBufferBytes: 2 << 20));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MemoryBudget.SetLimits(scratchRetainedBytes: -1));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MemoryBudget.SetLimits(weightCacheEntries: 0));
    }

    [Fact]
    public void ABudgetCannotChangeAnOutputByte()
    {
        using var source = Image.Create(96, 64, WorkingFormat.Rgba8);
        var pixels = source.WritablePixels;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i * 53 + 7) & 0xFF);

        using var first = source.Pipe().Resize(37, 23, Resampler.Bicubic).ToImage();
        var baseline = first.WritablePixels.ToArray();

        // Starve both caches: every rent allocates, every build recomputes.
        MemoryBudget.SetLimits(scratchRetainedBytes: 0, scratchBufferBytes: 0,
            weightCacheBytes: 0, weightCacheEntries: 1);

        using var starvedImage = source.Pipe().Resize(37, 23, Resampler.Bicubic).ToImage();
        starvedImage.WritablePixels.ToArray().ShouldBe(baseline);

        MemoryBudget.ResetForTests();
        using var restored = source.Pipe().Resize(37, 23, Resampler.Bicubic).ToImage();
        restored.WritablePixels.ToArray().ShouldBe(baseline);
    }
}
