using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// A point step through a plan must produce exactly what the operator produces when called directly. The plan path
/// is being changed to write its destination instead of renting a band buffer and copying it, and a byte-domain table
/// built from a 256-pixel probe is the mechanism — so the thing that could go wrong is the table, not the loop.
/// </summary>
public class PointDirectParityTests
{
    private static Image Pixels(WorkingFormat format = WorkingFormat.Rgba8)
    {
        var image = Image.Create(37, 23, format);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)((i * 29) + (i >> 3));

        return image;
    }

    public static TheoryData<string> Ops => ["invert", "brightness", "contrast", "gamma", "posterize"];

    private static ImageOp Op(string id) => id switch
    {
        "invert" => PlanOps.Invert(),
        "brightness" => PlanOps.Brightness(0.07f),
        "contrast" => PlanOps.Contrast(1.23f),
        "gamma" => PlanOps.Gamma(0.85f),
        "posterize" => PlanOps.Posterize(6),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown operator."),
    };

    private static Image Direct(Image source, string id) => id switch
    {
        "invert" => source.Pipe().Invert().ToImage(),
        "brightness" => source.Pipe().Brightness(0.07f).ToImage(),
        "contrast" => source.Pipe().Contrast(1.23f).ToImage(),
        "gamma" => source.Pipe().Gamma(0.85f).ToImage(),
        "posterize" => source.Pipe().Posterize(6).ToImage(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown operator."),
    };

    [Theory]
    [MemberData(nameof(Ops))]
    public void PlanPath_MatchesTheOperator_Rgba8(string id)
    {
        using var source = Pixels();
        var plan = new ProcessingPlan().Add(Op(id));

        using var viaPlan = source.Apply(plan);
        using var direct = Direct(source, id);

        viaPlan.WritablePixels.ToArray().ShouldBe(direct.WritablePixels.ToArray());
    }

    /// <summary>Sixteen bits keeps the float path, so the table branch must decline rather than misapply.</summary>
    [Theory]
    [MemberData(nameof(Ops))]
    public void PlanPath_MatchesTheOperator_Rgba16(string id)
    {
        using var source = Pixels(WorkingFormat.Rgba16);
        var plan = new ProcessingPlan().Add(Op(id));

        using var viaPlan = source.Apply(plan);
        using var direct = Direct(source, id);

        viaPlan.WritablePixels.ToArray().ShouldBe(direct.WritablePixels.ToArray());
    }

    /// <summary>
    /// Above the pixel floor the table path runs, below it the general adapter does. Both must agree with the operator
    /// itself — the small images above only cover the second branch, so this one uses 512x512 (262 K pixels).
    /// </summary>
    [Theory]
    [MemberData(nameof(Ops))]
    public void LargeImage_TakesTheTablePath_AndStillMatches(string id)
    {
        var image = Image.Create(512, 512, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)((i * 17) + (i >> 5));

        using var source = image;
        var plan = new ProcessingPlan().Add(Op(id));

        using var viaPlan = source.Apply(plan);
        using var direct = Direct(source, id);

        viaPlan.WritablePixels.ToArray().ShouldBe(direct.WritablePixels.ToArray());
    }

    /// <summary>And the two branches agree with each other, so the switch is a switch of path and nothing else.</summary>
    [Fact]
    public void TheTwoBranches_Agree()
    {
        using var source = Image.Create(512, 512, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)(i * 13);

        PlanOps.ForceAdapterPoint = true;
        byte[] throughAdapter;
        try
        {
            using var viaAdapter = source.Apply(new ProcessingPlan().Add(PlanOps.Contrast(1.17f)));
            throughAdapter = viaAdapter.WritablePixels.ToArray();
        }
        finally
        {
            PlanOps.ForceAdapterPoint = false;
        }

        using var viaTable = source.Apply(new ProcessingPlan().Add(PlanOps.Contrast(1.17f)));

        viaTable.WritablePixels.ToArray().ShouldBe(throughAdapter);
    }

    /// <summary>And through the facade, where a single operator is the common case.</summary>
    [Theory]
    [MemberData(nameof(Ops))]
    public void FacadePath_MatchesTheOperator(string id)
    {
        using var source = Pixels();

        using var viaPipe = id switch
        {
            "invert" => source.Pipe().Invert().ToImage(),
            "brightness" => source.Pipe().Brightness(0.07f).ToImage(),
            "contrast" => source.Pipe().Contrast(1.23f).ToImage(),
            "gamma" => source.Pipe().Gamma(0.85f).ToImage(),
            "posterize" => source.Pipe().Posterize(6).ToImage(),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown operator."),
        };

        using var direct = Direct(source, id);

        viaPipe.WritablePixels.ToArray().ShouldBe(direct.WritablePixels.ToArray());
    }
}
