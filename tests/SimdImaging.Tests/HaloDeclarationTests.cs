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
