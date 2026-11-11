using System.Security.Cryptography;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Batch work — one plan, many images, several threads — is how this library actually gets used, and until
/// 2026-11-07 no test exercised it: every existing test calls an operator from a single thread. A concurrency
/// defect here would not throw, it would return different bytes on some runs, which is the worst failure
/// mode this repository has.
/// <para>
/// The shared state these tests probe: <c>ArrayPixelPool</c> (concurrent dictionaries and bags),
/// <c>FloatScratch</c> (<c>[ThreadStatic]</c>), <c>RowParallel</c>'s worker bookkeeping, and the row
/// transforms that <c>PlanSteps</c> closes over. All of it is meant to be safe; none of it was proven.
/// </para>
/// </summary>
public class ConcurrencyTests
{
    [Fact]
    public void Plan_ExecutedConcurrentlyFromManyThreads_AgreesByteForByte()
    {
        // 517×233 is prime-ish in both axes and not a multiple of anything SIMD-friendly, so band edges and
        // vector tails land on awkward boundaries — the places where shared scratch would corrupt first.
        const int width = 517, height = 233;

        using var image = Fixture(width, height);
        var source = image[0].Pixels.View;

        var plan = new ProcessingPlan()
            .Add("brightness", OpCategory.Point, PlanSteps.Point(v => PointOps.Brightness(v, 0.11f)))
            .Add("blur", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1.7f), 6, 6), 6, 6)
            .Add("equalize", OpCategory.Global, PlanSteps.Global(v => HistogramOps.Equalize(v)))
            .Add("contrast", OpCategory.Point, PlanSteps.Point(v => PointOps.Contrast(v, 1.3f)));

        var expected = Hash(plan.Execute(source));

        var results = new string[24];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 12 },
            i => results[i] = Hash(plan.Execute(source)));

        for (var i = 0; i < results.Length; i++)
            results[i].ShouldBe(expected, $"concurrent run {i} differed from the serial result");
    }

    [Fact]
    public void BatchOfDifferentImages_MatchesTheSerialResults()
    {
        const int width = 320, height = 180;
        const int count = 16;

        var images = new Image[count];
        var expected = new string[count];
        try
        {
            for (var i = 0; i < count; i++)
                images[i] = Fixture(width, height, seed: 1000 + i * 37);

            var plan = new ProcessingPlan()
                .Add("gamma", OpCategory.Point, PlanSteps.Point(v => PointOps.Gamma(v, 2.2f)))
                .Add("sharpen", OpCategory.Neighborhood,
                    PlanSteps.Neighborhood(v => FilterOps.GaussianSharpen(v, 0.6f, 1.2f), 4, 4), 4, 4);

            for (var i = 0; i < count; i++)
                expected[i] = Hash(plan.Execute(images[i][0].Pixels.View));

            var actual = new string[count];
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                actual[i] = Hash(plan.Execute(images[i][0].Pixels.View)));

            for (var i = 0; i < count; i++)
                actual[i].ShouldBe(expected[i], $"image {i} differed when batched");
        }
        finally
        {
            foreach (var image in images)
                image?.Dispose();
        }
    }

    [Fact]
    public void SeedNoise_IsReproducibleAcrossConcurrentRuns()
    {
        // The seeded RNG is the one operator whose output depends on call order rather than pixel position.
        // If its state were shared, concurrent runs would produce interleaved sequences.
        const int width = 256, height = 128;

        using var image = Fixture(width, height);
        var source = image[0].Pixels.View;

        var plan = new ProcessingPlan()
            .Add("noise", OpCategory.Global,
                PlanSteps.Global(v => NoiseOps.Noise(v, 0.08f, NoiseKind.Gaussian, seed: 424242u)));

        var expected = Hash(plan.Execute(source));

        var results = new string[12];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 6 },
            i => results[i] = Hash(plan.Execute(source)));

        foreach (var hash in results)
            hash.ShouldBe(expected, "seeded noise must be identical no matter how many runs overlap");
    }

    private static Image Fixture(int width, int height, int seed = 20261107)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        new Random(seed).NextBytes(image.WritablePixels);
        return image;
    }

    /// <summary>SHA-256 of the output bytes — the same comparison the vector-vs-scalar suite uses.</summary>
    private static string Hash(WorkingBuffer buffer)
    {
        using (buffer)
            return Convert.ToHexString(SHA256.HashData(buffer.View.Span));
    }
}
