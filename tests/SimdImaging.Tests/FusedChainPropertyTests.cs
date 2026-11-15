using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// The hand-written equivalence tests cover shapes someone thought of: a chain of points, a point then a
/// neighbourhood, two neighbourhoods, a halo wider than the band. Callers combine operators in whatever order
/// their problem needs, so the question this answers is whether the fused executor agrees with the step-by-step
/// one for combinations nobody wrote down.
/// </summary>
/// <remarks>
/// <para>
/// Seeded and deterministic: the same seed produces the same chains, so a failure is reproducible rather than a
/// story about a run that once went red. Each chain is also run at several band heights and both worker counts,
/// because the fusion boundary is where the band machinery and the halo bookkeeping meet.
/// </para>
/// <para>
/// The comparison is byte-exact, not approximate. A fused step and a step-by-step application of the same
/// operators must produce the same image, or the plan is a different operator.
/// </para>
/// </remarks>
public class FusedChainPropertyTests
{
    private const int Width = 67;
    private const int Height = 43;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(4, 4)]
    [InlineData(64, 4)]
    public void RandomChains_MatchStepByStep(int bandRows, int degree)
    {
        var random = new Random(20261116);
        var failures = new List<string>();

        for (var trial = 0; trial < 40; trial++)
        {
            var steps = new List<int>();
            var length = random.Next(1, 8);
            for (var i = 0; i < length; i++)
                steps.Add(random.Next(0, Ops.Length));

            using var source = Pattern(Width, Height, trial);
            byte[] fused;
            byte[] stepped;

            try
            {
                fused = RunFused(source, steps, bandRows, degree);
                stepped = RunStepped(source, steps);
            }
            catch (Exception e)
            {
                failures.Add($"trial {trial} [{Describe(steps)}]: threw {e.GetType().Name}: {e.Message}");
                continue;
            }

            if (!fused.AsSpan().SequenceEqual(stepped))
            {
                var first = 0;
                while (first < fused.Length && fused[first] == stepped[first])
                    first++;

                failures.Add($"trial {trial} [{Describe(steps)}]: differs at byte {first} "
                              + $"({fused[first]} vs {stepped[first]})");
            }
        }

        failures.ShouldBeEmpty(string.Join("\n", failures));
    }

    /// <summary>Operator palette: two point steps, two neighbourhood ones, and a global one that must split.</summary>
    private static readonly (string Name, OpCategory Category, int Halo, Func<Image, Image> Facade, OpKernel Plan)[] Ops =
    [
        ("brightness", OpCategory.Point, 0,
            i => i.Brightness(0.05f), PlanSteps.Point(v => PointOps.Brightness(v, 0.05f))),
        ("contrast", OpCategory.Point, 0,
            i => i.Contrast(1.1f), PlanSteps.Point(v => PointOps.Contrast(v, 1.1f))),
        ("blur1", OpCategory.Neighborhood, 3,
            i => i.GaussianBlur(1f), PlanSteps.Neighborhood(v => FilterOps.GaussianBlur(v, 1f), 3, 3)),
        ("box2", OpCategory.Neighborhood, 2,
            i => i.BoxBlur(2), PlanSteps.Neighborhood(v => FilterOps.BoxBlur(v, 2), 2, 2)),
        ("equalize", OpCategory.Global, 0,
            i => i.EqualizeHistogram(), PlanSteps.Global(v => HistogramOps.Equalize(v))),
    ];

    private static byte[] RunFused(Image source, List<int> steps, int bandRows, int degree)
    {
        var plan = new ProcessingPlan { BandRows = bandRows };
        for (var i = 0; i < steps.Count; i++)
        {
            var op = Ops[steps[i]];
            plan = plan.Add($"{op.Name}{i}", op.Category, op.Plan, op.Halo, op.Halo);
        }

        try
        {
            RowParallel.DegreeOverride = degree;
            using var result = source.Apply(plan);
            return result.WritablePixels.ToArray();
        }
        finally
        {
            RowParallel.DegreeOverride = 0;
        }
    }

    private static byte[] RunStepped(Image source, List<int> steps)
    {
        Image current = source;
        var owns = false;
        try
        {
            foreach (var index in steps)
            {
                var next = Ops[index].Facade(current);
                if (owns)
                    current.Dispose();

                current = next;
                owns = true;
            }

            return current.WritablePixels.ToArray();
        }
        finally
        {
            if (owns)
                current.Dispose();
        }
    }

    private static string Describe(List<int> steps) => string.Join(" → ", steps.Select(i => Ops[i].Name));

    private static Image Pattern(int width, int height, int seed)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        var random = new Random(seed * 31 + 7);
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = (byte)random.Next(256);
            span[i + 1] = (byte)random.Next(256);
            span[i + 2] = (byte)random.Next(256);
            span[i + 3] = 255;
        }

        return image;
    }
}
