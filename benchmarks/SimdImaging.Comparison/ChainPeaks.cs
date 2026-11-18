using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// The chains worth measuring for fusion's memory argument: deep point chains, a 3×3 family, and a blur followed
/// by points. Each is the shape a caller actually writes, not a synthetic pipeline.
/// </summary>
internal static class ChainPeaks
{
    /// <summary>Same steps, different band height — the knob the band-height experiment sweeps.</summary>
    public static ProcessingPlan WithBandRows(this ProcessingPlan plan, int rows)
    {
        var copy = new ProcessingPlan { BandRows = rows };
        foreach (var step in plan.Steps)
            copy.Add(step.Name, step.Category, step.Kernel, step.HaloX, step.HaloY);
        return copy;
    }

    /// <summary>Single operators, so the per-step allocation can be attributed.</summary>
    public static IEnumerable<(string Name, ProcessingPlan Plan)> Singles()
    {
        yield return ("单步 Sharpen", new ProcessingPlan().Add(PlanOps.Sharpen(0.6f)));
        yield return ("单步 Emboss", new ProcessingPlan().Add(PlanOps.Emboss(0.6f)));
        yield return ("单步 Sobel", new ProcessingPlan().Add(PlanOps.Sobel()));
        yield return ("单步 Median", new ProcessingPlan().Add(PlanOps.Median3x3()));
        yield return ("单步 Erode", new ProcessingPlan().Add(PlanOps.Erode3x3()));
        yield return ("单步 模糊 σ=3", new ProcessingPlan().Add(PlanOps.GaussianBlur(3f)));
        yield return ("单步 点", new ProcessingPlan().Add(PlanOps.Brightness(0.05f)));
    }

    public static IEnumerable<(string Name, ProcessingPlan Plan)> Cases()
    {
        yield return ("点 ×5", new ProcessingPlan()
            .Add(PlanOps.Brightness(0.05f))
            .Add(PlanOps.Contrast(1.1f))
            .Add(PlanOps.Invert())
            .Add(PlanOps.Brightness(0.02f))
            .Add(PlanOps.Contrast(0.9f)));

        yield return ("3×3 ×3", new ProcessingPlan()
            .Add(PlanOps.Sharpen(0.6f))
            .Add(PlanOps.Emboss(0.6f))
            .Add(PlanOps.Sobel()));

        yield return ("模糊 σ=3 + 3 点", new ProcessingPlan()
            .Add(PlanOps.GaussianBlur(3f))
            .Add(PlanOps.Brightness(0.05f))
            .Add(PlanOps.Contrast(1.1f))
            .Add(PlanOps.Invert()));
    }
}
