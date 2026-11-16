using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// Measures what each filter actually needs, instead of trusting what its caller declares.
/// </summary>
/// <remarks>
/// <para>
/// A fused band window is only correct if it fetches at least as far as every step reads. Getting that number
/// wrong does not throw — it produces a wrong pixel at a band boundary, which is the worst kind of bug to debug.
/// I got it wrong twice in one evening: <c>Sharpen</c> is a blur-based unsharp and needs <c>ceil(3σ) = 3</c> rows,
/// not the single row of a 3x3 convolution, and <c>Pixelate</c> is block-aligned, so no halo at all expresses it.
/// </para>
/// <para>
/// So this test does not restate the declarations — it derives them. For each operator it runs the banded plan at
/// every halo from zero upwards and takes the smallest one whose output matches the whole-image call. That value
/// is then compared with what the operator's documentation claims. A mistake like the one above now fails here,
/// naming the operator and both numbers, rather than surfacing as a stray pixel weeks later.
/// </para>
/// </remarks>
public class HaloDeclarationTests
{
    private const int Width = 41;
    private const int Height = 29;
    private const int BandRows = 4;
    private const int MaxHalo = 12;

    public static TheoryData<string> Cases => ["blur-s1", "blur-s2", "box-r1", "box-r2", "sharpen", "emboss", "sobel"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void DeclaredHalo_IsTheSmallestThatMatches(string id)
    {
        var (op, declared) = Build(id);
        using var source = Pattern();

        var full = Stepped(source, op);

        var measured = -1;
        for (var halo = 0; halo <= MaxHalo; halo++)
        {
            var banded = Banded(source, op, halo);
            if (banded.AsSpan().SequenceEqual(full))
            {
                measured = halo;
                break;
            }
        }

        measured.ShouldBe(declared, $"{id}: the smallest halo that reproduces the whole-image result");
    }

    /// <summary>
    /// Block-aligned operators have no such number: a block spans rows a band boundary can cut anywhere, so the
    /// only correct declaration is the whole-image category. This asserts that no halo under the limit works.
    /// </summary>
    [Fact]
    public void Pixelate_CannotBeExpressedByAnyHalo()
    {
        using var source = Pattern();
        var full = Stepped(source, v => FilterOps.Pixelate(v, 5));

        for (var halo = 0; halo < 8; halo++)
        {
            var banded = Banded(source, v => FilterOps.Pixelate(v, 5), halo);
            banded.AsSpan().SequenceEqual(full).ShouldBeFalse($"pixelate matched at halo {halo}, contrary to the claim");
        }
    }

    /// <summary>
    /// The same question asked through the public API instead of hand-declared halos: every factory in
    /// <see cref="ImageOps"/> builds a step that knows its own support, so a banded plan must equal the direct
    /// call for all of them — including the two that tripped the hand-written version.
    /// </summary>
    [Fact]
    public void EveryImageOpsFactory_BandsToTheSameBytesAsADirectCall()
    {
        using var source = Pattern();

        var ops = new (string Name, ImageOp Op, Func<PixelView, WorkingBuffer> Direct)[]
        {
            ("brightness", PlanOps.Brightness(0.05f), v => PointOps.Brightness(v, 0.05f)),
            ("contrast", PlanOps.Contrast(1.1f), v => PointOps.Contrast(v, 1.1f)),
            ("gamma", PlanOps.Gamma(1.4f), v => PointOps.Gamma(v, 1.4f)),
            ("invert", PlanOps.Invert(), v => PointOps.Invert(v)),
            ("grayscale", PlanOps.Grayscale(), v => PointOps.Grayscale(v)),
            ("sepia", PlanOps.Sepia(1f), v => PointOps.Sepia(v, 1f)),
            ("saturation", PlanOps.Saturation(1.3f), v => PointOps.Saturation(v, 1.3f)),
            ("posterize", PlanOps.Posterize(5), v => PointOps.Posterize(v, 5)),
            ("levels", PlanOps.Levels(0f, 1f, 1.2f), v => PointOps.Levels(v, 0f, 1f, 1.2f)),
            ("blur", PlanOps.GaussianBlur(1.5f), v => FilterOps.GaussianBlur(v, 1.5f)),
            ("box", PlanOps.BoxBlur(2), v => FilterOps.BoxBlur(v, 2)),
            ("sharpen", PlanOps.Sharpen(), v => FilterOps.Sharpen(v)),
            ("emboss", PlanOps.Emboss(), v => FilterOps.Emboss(v)),
            ("sobel", PlanOps.Sobel(), v => FilterOps.Sobel(v)),
            ("pixelate", PlanOps.Pixelate(5), v => FilterOps.Pixelate(v, 5)),
            ("equalize", PlanOps.Equalize(), v => HistogramOps.Equalize(v)),

            // The rest of the neighbourhood family, classified by reading each kernel's radius out of its
            // implementation rather than by measuring behaviour.
            ("median", PlanOps.Median3x3(), v => FilterOps.Median3x3(v)),
            ("erode", PlanOps.Erode3x3(), v => FilterOps.Erode3x3(v)),
            ("dilate", PlanOps.Dilate3x3(), v => FilterOps.Dilate3x3(v)),
            ("open", PlanOps.Open3x3(), v => FilterOps.Open3x3(v)),
            ("close", PlanOps.Close3x3(), v => FilterOps.Close3x3(v)),
            ("bilateral", PlanOps.Bilateral(2), v => FilterOps.Bilateral(v, 2, 0f, 0.1f)),
            ("motion", PlanOps.MotionBlur(5), v => FilterOps.MotionBlur(v, 5, 0f)),
            ("bokeh", PlanOps.BokehBlur(3), v => FilterOps.BokehBlur(v, 3)),
            ("guided", PlanOps.GuidedFilter(3), v => GuidedFilterOps.GuidedFilter(v, 3, 1e-4f)),
            ("oilpaint", PlanOps.OilPaint(2, 12), v => OilPaintOps.OilPaint(v, 2, 12)),
            ("nlm", PlanOps.NonLocalMeans(2), v => NonLocalMeansOps.NonLocalMeans(v, 2, 1, 0.1f)),

            // Whole-image steps. They still belong in a chain - the step costs one intermediate image, the same
            // as calling it directly, and the rest of the chain keeps its single pass.
            ("vignette", PlanOps.Vignette(0.75f, 1f), v => FilterOps.Vignette(v, 0.75f, 1f)),
            ("normalize", PlanOps.Normalize(), v => PointOps.Normalize(v)),
            ("adaptive-threshold", PlanOps.AdaptiveThreshold(4, 0.05f), v => ThresholdOps.AdaptiveThreshold(v, 4, 0.05f)),
            ("clahe", PlanOps.Clahe(4, 4, 3f), v => ClaheOps.Clahe(v, 4, 4, 3f)),
            ("dehaze", PlanOps.Dehaze(3, 0.95f, 0.1f, 4), v => DehazeOps.Dehaze(v, 3, 0.95f, 0.1f, 4)),
        };

        var failures = new List<string>();
        foreach (var (name, op, direct) in ops)
        {
            var plan = new ProcessingPlan { BandRows = BandRows }.Add(op);
            byte[] banded;
            try
            {
                using var result = source.Apply(plan);
                banded = result.WritablePixels.ToArray();
            }
            catch (Exception e)
            {
                failures.Add($"{name}: threw {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()}");
                continue;
            }

            if (!banded.AsSpan().SequenceEqual(Stepped(source, direct)))
                failures.Add($"{name}: banded differs from the direct call");
        }

        failures.ShouldBeEmpty(string.Join("\n", failures));
    }

    /// <summary>
    /// The form a caller actually writes: three steps, no halo, no categories — the same bytes as calling them
    /// one at a time.
    /// </summary>
    [Fact]
    public void Pipeline_WithThreeSteps_MatchesCallingThemSeparately()
    {
        using var source = Pattern();

        using var piped = source.Pipe()
            .Brightness(0.05f)
            .Sharpen()
            .Grayscale()
            .ToImage();

        using var first = source.Brightness(0.05f);
        using var second = first.Sharpen();
        using var third = second.Grayscale();

        piped.WritablePixels.ToArray().ShouldBe(third.WritablePixels.ToArray());
    }

    /// <summary>
    /// A size change in the middle of a chain is the library's problem: the caller writes one chain, the pipeline
    /// ends its band pass, resizes, and starts another. The bytes must match doing it by hand.
    /// </summary>
    [Fact]
    public void Pipeline_WithAResizeInTheMiddle_MatchesDoingItByHand()
    {
        using var source = Pattern();

        using var piped = source.Pipe()
            .Brightness(0.05f)
            .Resize(20, 14)
            .Sharpen()
            .ToImage();

        using var bright = source.Brightness(0.05f);
        using var resized = bright.Resize(20, 14);
        using var sharpened = resized.Sharpen();

        piped.Width.ShouldBe(20);
        piped.Height.ShouldBe(14);
        piped.WritablePixels.ToArray().ShouldBe(sharpened.WritablePixels.ToArray());
    }

    /// <summary>The declared values: what the library's own documentation says each operator needs.</summary>
    private static (Func<PixelView, WorkingBuffer> Op, int Halo) Build(string id) => id switch
    {
        // Gaussian support is ceil(3 sigma); the plan step must declare the same.
        "blur-s1" => (v => FilterOps.GaussianBlur(v, 1f), 3),
        "blur-s2" => (v => FilterOps.GaussianBlur(v, 2f), 6),
        "box-r1" => (v => FilterOps.BoxBlur(v, 1), 1),
        "box-r2" => (v => FilterOps.BoxBlur(v, 2), 2),
        // Sharpen is a blur-based unsharp: Gaussian sigma 1 by default, so three rows, not one.
        "sharpen" => (v => FilterOps.Sharpen(v), 3),
        "emboss" => (v => FilterOps.Emboss(v), 1),
        "sobel" => (v => FilterOps.Sobel(v), 1),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown case."),
    };

    private static byte[] Banded(Image source, Func<PixelView, WorkingBuffer> op, int halo)
    {
        var plan = new ProcessingPlan { BandRows = BandRows }
            .Add("op", OpCategory.Neighborhood, PlanSteps.Neighborhood(op, halo, halo), halo, halo);

        try
        {
            RowParallel.DegreeOverride = 1;
            using var result = source.Apply(plan);
            return result.WritablePixels.ToArray();
        }
        finally
        {
            RowParallel.DegreeOverride = 0;
        }
    }

    private static byte[] Stepped(Image source, Func<PixelView, WorkingBuffer> op)
    {
        using var result = op(source[0].Pixels.View);
        return result.View.Span.ToArray();
    }

    private static Image Pattern()
    {
        var image = Image.Create(Width, Height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        var random = new Random(7001);
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
