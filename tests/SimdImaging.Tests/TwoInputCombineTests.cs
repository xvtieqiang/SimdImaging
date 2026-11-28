using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The two-input combine table (sharpen, glow) is per-step state, not per-band. These cases pin both halves of that
/// claim: the bytes must not depend on how many bands the step was cut into, and the table's cost must not either.
/// <para>
/// The second half is the one that regressed quietly. A table built inside the kernel delegate is allocated once per
/// band, so a 17-band 1080p sharpen allocated 1554 KiB against 66 KiB for the same operator on one band — a 24x
/// amplification that no byte comparison would catch, because the pixels are identical.
/// </para>
/// </summary>
public sealed class TwoInputCombineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    public void Sharpen_IsByteIdenticalAcrossBandHeights(int bandRows)
    {
        var reference = Run(sharp: true, bandRows: 72);
        var banded = Run(sharp: true, bandRows: bandRows);

        banded.ShouldBe(reference,
            $"a {bandRows}-row band must produce the same bytes as one whole band");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void Glow_IsByteIdenticalAcrossBandHeights(int bandRows)
    {
        var reference = Run(sharp: false, bandRows: 72);
        var banded = Run(sharp: false, bandRows: bandRows);

        banded.ShouldBe(reference);
    }

    /// <summary>
    /// The table's contribution, isolated by comparing against an operator with no table at the same band count.
    /// <para>
    /// Measuring raw allocation would not answer this: the executor allocates a small fixed amount per band for its
    /// own state, so a many-band run always allocates more than a one-band run even when nothing is rebuilt. What
    /// must not happen is 64 KiB per band, so the bound is expressed as "sharpen minus a table-free blur", which
    /// cancels that per-band floor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1024, 768, 256)]
    [InlineData(1920, 1080, 64)]
    public void Sharpen_DoesNotAllocateATablePerBand(int width, int height, int bandRows)
    {
        var sharpen = Allocated(new ProcessingPlan { BandRows = bandRows }.Add(PlanOps.Sharpen(0.6f, 1f)), width, height);
        var blur = Allocated(new ProcessingPlan { BandRows = bandRows }.Add(PlanOps.GaussianBlur(1f)), width, height);
        var bandCount = (height + bandRows - 1) / bandRows;

        // A per-band table would put 64 KiB per band between the two figures. Allow one table's worth of slack: the
        // sharpen path builds its probe once, and that belongs to the table's first-use cost, not to a rebuild.
        const long oneTable = 64 * 1024;
        var extra = sharpen - blur;
        extra.ShouldBeLessThan(oneTable,
            $"sharpen allocated {extra} bytes more than a table-free blur over {bandCount} bands; "
            + $"one table per band would be {oneTable} bytes per band");
    }

    private static long Allocated(ProcessingPlan plan, int width, int height)
    {
        using var image = Ramp(width, height);
        var view = image[0].Pixels.View;

        // Warm the pool, the lazy table and the JIT, or the first call's cost lands in the reading.
        for (var i = 0; i < 3; i++)
            using (plan.Execute(view)) { }

        var before = GC.GetTotalAllocatedBytes(precise: true);
        using (plan.Execute(view)) { }
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    private static byte[] Run(bool sharp, int bandRows)
    {
        using var image = Ramp(96, 72);
        var plan = new ProcessingPlan { BandRows = bandRows }
            .Add(sharp ? PlanOps.Sharpen(0.6f, 1f) : PlanOps.Glow(0.5f, 2f, 0.6f));
        using var result = plan.Execute(image[0].Pixels.View);
        return result.View.Span.ToArray();
    }

    private static Image Ramp(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i * 31 + (i >> 7) * 17) & 0xFF);
        return image;
    }
}
