using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Fusion;

namespace SimdImaging.Tests;

/// <summary>
/// The halo recursion in <see cref="FusedExecutor.RunWindow"/> is the one place where a silent off-by-one
/// changes every output byte without throwing. These tests pin the contract from two sides: a matrix of
/// non-uniform and large halos compared byte for byte with the unfused path, and the window invariant itself.
/// </summary>
public sealed class FusedWindowInvariantTests
{
    private const int Width = 8;
    private const int Seed = 77123;

    private readonly record struct StepSpec(int Halo, bool Point);

    private static readonly StepSpec[][] Chains =
    [
        [new(1, false), new(1, false)],
        [new(1, false), new(3, false)],
        [new(3, false), new(1, false)],
        [new(5, false), new(1, false)],
        [new(1, false), new(5, false)],
        [new(3, false), new(5, false), new(2, false)],
        [new(5, false), new(1, false), new(0, true)],
        [new(0, true), new(2, false), new(4, false)],
        [new(2, false), new(0, true), new(3, false)],
        [new(4, false), new(4, false), new(4, false)],
        [new(1, false), new(2, false), new(3, false), new(4, false)],
    ];

    private static readonly int[] Heights = [1, 2, 3, 6, 7, 9];
    private static readonly int[] BandRows = [1, 2, 3, 4, 5, 16];
    private static readonly int[] Degrees = [1, 2, 4];

    [Fact]
    public void Fused_MatchesUnfused_AcrossNonUniformHalosBandsAndDegrees()
    {
        var failures = new List<string>();

        foreach (var chain in Chains)
        foreach (var height in Heights)
        foreach (var bandRows in BandRows)
        {
            using var source = TestImages.Gradient(Width, height, WorkingFormat.Rgba8);
            new Random(Seed + height).NextBytes(source[0].Pixels.View.Span);

            byte[] expected;
            using (var reference = Build(chain, bandRows).ExecuteWithOptions(source[0].Pixels.View,
                       new ExecuteOptions { AllowFusion = false, MaxDegreeOfParallelism = 1 }))
            {
                expected = reference.View.Span.ToArray();
            }

            foreach (var degree in Degrees)
            {
                using var actual = Build(chain, bandRows).ExecuteWithOptions(source[0].Pixels.View,
                    new ExecuteOptions { ForceFusion = true, MaxDegreeOfParallelism = degree });
                if (!actual.View.Span.SequenceEqual(expected))
                {
                    failures.Add(
                        $"halos=[{string.Join(",", chain.Select(step => step.Halo))}] " +
                        $"height={height} bandRows={bandRows} degree={degree}");
                    if (failures.Count == 10)
                        break;
                }
            }

            if (failures.Count == 10)
                break;
        }

        failures.ShouldBeEmpty("a fused window changed bytes at:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Fused_MatchesUnfused_ForASingleColumnAndSingleRow()
    {
        foreach (var (width, height) in new[] { (1, 1), (1, 7), (7, 1) })
        {
            using var source = TestImages.Gradient(width, height, WorkingFormat.Rgba8);
            new Random(Seed + width + height).NextBytes(source[0].Pixels.View.Span);
            var chain = new[] { new StepSpec(3, false), new StepSpec(5, false) };

            using var reference = Build(chain, bandRows: 1)
                .ExecuteWithOptions(source[0].Pixels.View,
                    new ExecuteOptions { AllowFusion = false, MaxDegreeOfParallelism = 1 });
            foreach (var degree in Degrees)
            {
                using var actual = Build(chain, bandRows: 2)
                    .ExecuteWithOptions(source[0].Pixels.View,
                        new ExecuteOptions { ForceFusion = true, MaxDegreeOfParallelism = degree });
                actual.View.Span.ToArray().ShouldBe(reference.View.Span.ToArray(),
                    $"fused {width}x{height} changed bytes at degree {degree}");
            }
        }
    }

    [Fact]
    public void RunWindow_RejectsScratchTallerThanTheWindow()
    {
        using var source = WorkingBuffer.Rent(Width, 6, WorkingFormat.Rgba8);
        using var output = WorkingBuffer.Rent(Width, 6, WorkingFormat.Rgba8);
        using var scratchA = WorkingBuffer.Rent(Width, 6, WorkingFormat.Rgba8);
        using var scratchB = WorkingBuffer.Rent(Width, 6, WorkingFormat.Rgba8);
        var plan = Build([new(1, false), new(1, false)], bandRows: 2);

        var sourceWindow = source.View.Slice(new PixelRect(0, 0, Width, 4));
        var outputWindow = output.View.Slice(new PixelRect(0, 0, Width, 4));

        var error = Should.Throw<InvalidOperationException>(() => FusedExecutor.RunWindow(
            Width, 6, sourceWindow, outputWindow, winTop: 0, top: 0, bottom: 2,
            plan.Steps, first: 0, count: 2, haloTotal: 2, scratchA.View, scratchB.View));
        error.Message.ShouldContain("exactly");
    }

    private static ProcessingPlan Build(StepSpec[] chain, int bandRows)
    {
        var plan = new ProcessingPlan { BandRows = bandRows };
        for (var i = 0; i < chain.Length; i++)
        {
            var spec = chain[i];
            plan.Add($"step-{i}", spec.Point ? OpCategory.Point : OpCategory.Neighborhood,
                spec.Point ? PointKernel : VerticalBox(spec.Halo), haloY: spec.Halo);
        }

        return plan;
    }

    private static void PointKernel(PixelView source, PixelView destination, PixelRect rect, PixelRect _)
    {
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            source.Row(y).Slice(rect.X * 4, rect.Width * 4)
                .CopyTo(destination.Row(y).Slice(rect.X * 4, rect.Width * 4));
        }
    }

    /// <summary>The kernel a window must serve: it reads ±radius rows around every destination row and clamps
    /// against the view it was handed, exactly like the real separable kernels.</summary>
    private static OpKernel VerticalBox(int radius) => (source, destination, rect, _) =>
    {
        var tapCount = (2 * radius) + 1;
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        for (var x = rect.X; x < rect.X + rect.Width; x++)
        for (var channel = 0; channel < 4; channel++)
        {
            var sum = 0;
            for (var offset = -radius; offset <= radius; offset++)
                sum += source.Row(Math.Clamp(y + offset, 0, source.Height - 1))[(x * 4) + channel];
            destination.Row(y)[(x * 4) + channel] = (byte)(sum / tapCount);
        }
    };
}
