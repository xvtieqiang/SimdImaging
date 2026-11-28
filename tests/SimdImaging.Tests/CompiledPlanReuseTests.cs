using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;

namespace SimdImaging.Tests;

/// <summary>
/// The compiled executor now keeps the nested <see cref="ProcessingPlan"/>s on the compiled plan instead of
/// rebuilding them on every run, and the runtime reuses a segmentation keyed on the decision inputs. Both are
/// caches, so the contract they must keep is that reuse changes no byte: a compiled plan run twice and a direct
/// plan reused across geometries must each match a freshly built reference.
/// </summary>
public sealed class CompiledPlanReuseTests
{
    private static Image Ramp(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                span[o] = (byte)(x * 7 + y * 3);
                span[o + 1] = (byte)(y * 11);
                span[o + 2] = (byte)(x * 5);
                span[o + 3] = 255;
            }
        }

        return image;
    }

    private static PipelineRecipe Recipe()
    {
        var builder = new PipelineRecipeBuilder();
        builder.Append(PlanOps.Invert());
        builder.Append(PlanOps.Contrast(1.2f));
        builder.Append(PlanOps.Grayscale());
        return builder.Build();
    }

    private static CompileOptions FrameOptions(int width, int height) => new()
    {
        SourceWidth = width,
        SourceHeight = height,
        SourceFormat = WorkingFormat.Rgba8,
        Source = SourceCapabilities.Seekable | SourceCapabilities.Replayable,
        Sink = SinkCapabilities.None,
    };

    [Fact]
    public void CompiledPlan_NestedPlansAreStableAndReuseChangesNoByte()
    {
        var compiled = PipelineCompiler.Compile(Recipe(), FrameOptions(320, 200));
        var sectionIndex = -1;
        for (var i = 0; i < compiled.Sections.Count; i++)
            if (compiled.Sections[i].Kind == CompiledSectionKind.OperatorRun)
                sectionIndex = i;
        sectionIndex.ShouldBeGreaterThanOrEqualTo(0);

        var sectionPlan = compiled.SectionPlan(sectionIndex);
        compiled.WholePlan.ShouldNotBeNull();
        compiled.SectionPlan(sectionIndex).ShouldBeSameAs(sectionPlan, "the nested plan must be a cached instance");

        using var image = Ramp(320, 200);
        var options = new ExecuteOptions { Diagnostics = true };
        using var first = CompiledPlanExecutor.ExecuteFrame(compiled, image[0].Pixels.View, options: options);
        using var second = CompiledPlanExecutor.ExecuteFrame(compiled, image[0].Pixels.View, options: options);

        first.View.Span.ToArray().ShouldBe(second.View.Span.ToArray());
        first.View.Span.ToArray().ShouldNotBe(image[0].Pixels.View.Span.ToArray());
    }

    [Fact]
    public void DirectPlan_ReusedAcrossGeometries_MatchesFreshPlansByteForByte()
    {
        // The segmentation cache is keyed on width/height/format/options. A plan built once but executed at two
        // sizes must not let the first size's decision leak into the second.
        var plan = new ProcessingPlan();
        plan.Add(PlanOps.Invert());
        plan.Add(PlanOps.Contrast(1.2f));

        foreach (var (width, height) in new[] { (320, 200), (160, 96), (640, 400) })
        {
            using var image = Ramp(width, height);
            using var reused = plan.Execute(image[0].Pixels.View);

            var fresh = new ProcessingPlan();
            fresh.Add(PlanOps.Invert());
            fresh.Add(PlanOps.Contrast(1.2f));
            using var reference = fresh.Execute(image[0].Pixels.View);

            reused.View.Span.ToArray().ShouldBe(reference.View.Span.ToArray());
        }
    }

    /// <summary>
    /// A plan that is executed and then extended must re-derive its shape. The fold, the step arrays and the
    /// segment count are cached per <c>_version</c>, and <c>Add</c> bumps it — but nothing asserted that the bump
    /// is observed. A cache that kept the first shape would run two steps where the plan now has three, and the
    /// bytes would be quietly wrong rather than throwing.
    /// <para>
    /// The check is a byte comparison against a fresh plan carrying the same steps: the first run warms the cache,
    /// the second must not reuse it. The segment count is asserted too, because a stale shape could in principle
    /// produce the right pixels on a short chain while still describing the wrong work.
    /// </para>
    /// </summary>
    [Fact]
    public void ExecuteThenAdd_InvalidatesTheCachedShape()
    {
        using var image = Ramp(96, 72);

        var plan = new ProcessingPlan();
        plan.Add(PlanOps.Invert());

        using var first = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { Diagnostics = true }, out var firstReport);
        firstReport!.SegmentCount.ShouldBe(1);

        plan.Add(PlanOps.Contrast(1.2f));

        using var second = plan.ExecuteWithOptions(image[0].Pixels.View,
            new ExecuteOptions { Diagnostics = true }, out var secondReport);
        secondReport!.SegmentCount.ShouldBe(1, "two point steps fuse into one segment");

        // The extended plan must match a plan built from the start with both steps.
        var fresh = new ProcessingPlan();
        fresh.Add(PlanOps.Invert());
        fresh.Add(PlanOps.Contrast(1.2f));
        using var reference = fresh.Execute(image[0].Pixels.View);

        second.View.Span.ToArray().ShouldBe(reference.View.Span.ToArray());

        // And the second run is affected by the new step: a stale shape would have returned the first result.
        second.View.Span.ToArray().ShouldNotBe(first.View.Span.ToArray());
    }
}
