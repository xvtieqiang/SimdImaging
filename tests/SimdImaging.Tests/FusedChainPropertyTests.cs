using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// Do fused and step-by-step execution agree for chains nobody wrote down? They do — once every operator's halo
/// is declared truthfully, which is the part that is easy to get wrong.
/// </summary>
/// <remarks>
/// <para>
/// Seventeen operators, parameters drawn per chain, three band heights, two worker counts: 120 chains compared
/// byte for byte against the same operators applied one at a time.
/// </para>
/// <para>
/// This test earned its keep by failing twice, and both failures were mine, not the executor's:
/// </para>
/// <list type="bullet">
/// <item><description><b>Sharpen</b> is a blur-based unsharp, so its support is the Gaussian's
/// <c>ceil(3σ) = 3</c>, not the 1 of a 3x3 convolution. Declared as 1, the band came up two rows short and the
/// result differed exactly at a band seam.</description></item>
/// <item><description><b>Pixelate</b> is block-aligned, not a sliding window. A block spans rows that a band
/// boundary can cut anywhere — band 4..7 with block 6 needs rows 0..11 — so no constant halo expresses it.
/// It has to be declared <see cref="OpCategory.Global"/>, which is the only correct category for it.</description></item>
/// </list>
/// <para>
/// That is the halo contract in one sentence: an operator that reads further than its caller admits cannot be
/// fused correctly, and the failure looks like a wrong pixel at a band boundary rather than like an exception.
/// </para>
/// </remarks>
public class FusedChainPropertyTests
{
    private const int Width = 67;
    private const int Height = 43;

    /// <summary>Every operator is a <c>PixelView → WorkingBuffer</c> call, which is all <see cref="PlanSteps"/> needs.</summary>
    private static readonly (string Name, OpCategory Category, int Halo, Func<Random, Func<PixelView, WorkingBuffer>> Build)[] Ops =
    [
        ("brightness", OpCategory.Point, 0, r => { var a = (float)r.NextDouble(); return v => PointOps.Brightness(v, a); }),
        ("contrast", OpCategory.Point, 0, r => { var a = 0.5f + (float)r.NextDouble(); return v => PointOps.Contrast(v, a); }),
        ("gamma", OpCategory.Point, 0, r => { var a = 0.5f + (float)r.NextDouble(); return v => PointOps.Gamma(v, a); }),
        ("invert", OpCategory.Point, 0, _ => v => PointOps.Invert(v)),
        ("sepia", OpCategory.Point, 0, r => { var a = (float)r.NextDouble(); return v => PointOps.Sepia(v, a); }),
        ("grayscale", OpCategory.Point, 0, _ => v => PointOps.Grayscale(v)),
        ("saturation", OpCategory.Point, 0, r => { var a = (float)(r.NextDouble() * 2); return v => PointOps.Saturation(v, a); }),
        ("posterize", OpCategory.Point, 0, r => { var levels = r.Next(2, 16); return v => PointOps.Posterize(v, levels); }),
        ("levels", OpCategory.Point, 0, r => { var gamma = 0.5f + (float)r.NextDouble(); return v => PointOps.Levels(v, 0f, 1f, gamma); }),
        ("tint", OpCategory.Point, 0, r =>
        {
            var colour = new RgbaColor((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256), 255);
            var amount = (float)r.NextDouble();
            return v => PointOps.Tint(v, colour, amount);
        }),

        ("blur", OpCategory.Neighborhood, 9, r =>
        {
            var sigma = 0.5f + (float)r.NextDouble() * 2;
            return v => FilterOps.GaussianBlur(v, sigma);
        }),
        ("box", OpCategory.Neighborhood, 3, r => { var radius = r.Next(1, 4); return v => FilterOps.BoxBlur(v, radius); }),
        // Sharpen is a blur-based unsharp: its support is the Gaussian's, ceil(3 sigma), not the 3x3 of a
        // convolution. Declaring 1 here is what made this test fail, and it is the mistake the halo contract
        // exists to catch - an operator that reads further than its caller admits.
        ("sharpen", OpCategory.Neighborhood, 3, _ => v => FilterOps.Sharpen(v)),
        ("emboss", OpCategory.Neighborhood, 1, _ => v => FilterOps.Emboss(v)),
        ("sobel", OpCategory.Neighborhood, 1, _ => v => FilterOps.Sobel(v)),
        // Pixelate is block-aligned, not a sliding window: a block spans rows that a band boundary can cut anywhere,
        // so no constant halo expresses it (band 4..7 with block 6 needs rows 0..11). Declared Global, which runs
        // it over the whole image - the only category that is correct for it.
        ("pixelate", OpCategory.Global, 0, r => { var block = r.Next(2, 7); return v => FilterOps.Pixelate(v, block); }),

        ("equalize", OpCategory.Global, 0, _ => v => HistogramOps.Equalize(v)),
    ];

    [Fact]
    public void RandomChains_MatchStepByStep()
    {
        var random = new Random(20261116);
        var failures = new List<string>();

        // Band heights either side of the halo (1, 3), a height that divides the image badly (7), and one wide
        // enough to make the halo irrelevant (64); worker counts 1 and 4 on each. Formats included because the
        // integer fast paths and the float path read and round differently - a chain that agrees in Rgba8 says
        // nothing about Rgba32F.
        foreach (var format in new[] { WorkingFormat.Rgba8, WorkingFormat.Rgba16, WorkingFormat.Rgba32F })
        foreach (var (bandRows, degree) in new[] { (1, 1), (3, 4), (7, 4), (64, 1) })
        {
            for (var trial = 0; trial < 25; trial++)
            {
                var length = random.Next(1, 8);
                var steps = new List<int>();
                var funcs = new List<Func<PixelView, WorkingBuffer>>();
                for (var i = 0; i < length; i++)
                {
                    var index = random.Next(0, Ops.Length);
                    steps.Add(index);
                    funcs.Add(Ops[index].Build(random));
                }

                using var source = Pattern(Width, Height, trial, format);
                var fused = RunFused(source, steps, bandRows, degree, funcs);
                var stepped = RunStepped(source, funcs);

                if (!fused.AsSpan().SequenceEqual(stepped))
                {
                    var first = 0;
                    while (first < fused.Length && fused[first] == stepped[first])
                        first++;

                    failures.Add($"{format} bandRows={bandRows} degree={degree} trial {trial} [{Describe(steps)}]: "
                                  + $"differs at byte {first} ({fused[first]} vs {stepped[first]})");
                }
            }
        }

        failures.ShouldBeEmpty(string.Join("\n", failures.Take(12)));
    }

    /// <summary>
    /// Whole-image and neighbourhood steps in one chain, crossing a band seam: the whole-image step runs once and
    /// the neighbourhood steps fetch their own halo on either side of it. Random chains reach this shape
    /// sometimes; this one always does.
    /// </summary>
    [Theory]
    [InlineData(WorkingFormat.Rgba8)]
    [InlineData(WorkingFormat.Rgba16)]
    [InlineData(WorkingFormat.Rgba32F)]
    public void GlobalAndNeighbourhoodSteps_Mix(WorkingFormat format)
    {
        using var source = Pattern(Width, Height, 3, format);

        var funcs = new List<Func<PixelView, WorkingBuffer>>
        {
            v => HistogramOps.Equalize(v),
            v => FilterOps.BoxBlur(v, 2),
            v => FilterOps.Pixelate(v, 5),
            v => FilterOps.Sharpen(v),
        };

        var steps = new List<int> { 16, 11, 15, 12 };   // equalize -> box -> pixelate -> sharpen
        var fused = RunFused(source, steps, 4, 4, funcs);
        var stepped = RunStepped(source, funcs);

        var first = 0;
        while (first < fused.Length && fused[first] == stepped[first])
            first++;

        fused.AsSpan().SequenceEqual(stepped).ShouldBeTrue(
            $"{format}: first difference at byte {first}");
    }

    /// <summary>The smallest case that reproduces it: one neighbourhood step, one band boundary.</summary>
    [Fact]
    public void ASingleSharpenStep_MatchesTheWholeImage()
    {
        var funcs = new List<Func<PixelView, WorkingBuffer>> { v => FilterOps.Sharpen(v) };
        using var source = Pattern(Width, Height, 16, WorkingFormat.Rgba8);

        RunFused(source, [12], 4, 1, funcs).ShouldBe(RunStepped(source, funcs));
    }

    private static byte[] RunFused(Image source, List<int> steps, int bandRows, int degree,
        IReadOnlyList<Func<PixelView, WorkingBuffer>> funcs)
    {
        var plan = new ProcessingPlan { BandRows = bandRows };
        for (var i = 0; i < steps.Count; i++)
        {
            var op = Ops[steps[i]];
            var kernel = op.Category switch
            {
                OpCategory.Point => PlanSteps.Point(funcs[i]),
                OpCategory.Neighborhood => PlanSteps.Neighborhood(funcs[i], op.Halo, op.Halo),
                _ => PlanSteps.Global(funcs[i]),
            };

            plan = plan.Add($"{op.Name}{i}", op.Category, kernel, op.Halo, op.Halo);
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

    /// <summary>The same operators, one call at a time, with no plan involved.</summary>
    private static byte[] RunStepped(Image source, IReadOnlyList<Func<PixelView, WorkingBuffer>> funcs)
    {
        var current = SimdImaging.Processing.Geometry.Transforms.Copy(source[0].Pixels.View, null);
        try
        {
            foreach (var op in funcs)
            {
                var next = op(current.View);
                current.Dispose();
                current = next;
            }

            return current.View.Span.ToArray();
        }
        finally
        {
            current.Dispose();
        }
    }

    private static string Describe(List<int> steps) => string.Join(" → ", steps.Select(i => Ops[i].Name));

    private static Image Pattern(int width, int height, int seed, WorkingFormat format)
    {
        var image = Image.Create(width, height, format);
        var span = image.WritablePixels;
        var random = new Random((seed * 31) + 7);
        var bytes = format.BytesPerPixel();
        for (var i = 0; i < span.Length; i += bytes)
        {
            switch (format)
            {
                case WorkingFormat.Rgba8:
                    span[i] = (byte)random.Next(256);
                    span[i + 1] = (byte)random.Next(256);
                    span[i + 2] = (byte)random.Next(256);
                    span[i + 3] = 255;
                    break;

                case WorkingFormat.Rgba16:
                    for (var c = 0; c < 3; c++)
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                            span.Slice(i + (c * 2), 2), (ushort)random.Next(65536));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                        span.Slice(i + 6, 2), ushort.MaxValue);
                    break;

                default:
                    for (var c = 0; c < 4; c++)
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                            span.Slice(i + (c * 4), 4), (float)random.NextDouble());
                    break;
            }
        }

        return image;
    }
}
