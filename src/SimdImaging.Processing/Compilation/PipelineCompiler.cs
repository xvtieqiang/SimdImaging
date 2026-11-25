using SimdImaging.Abstractions;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Fusion;

namespace SimdImaging.Processing.Compilation;

/// <summary>
/// Turns a <see cref="PipelineRecipe"/> plus a source/sink/budget description into an immutable
/// <see cref="CompiledExecutionPlan"/>. It decides geometry boundaries, fusion and the backend; it does not run
/// anything, and every execution path it chooses is one the existing executors already implement.
/// </summary>
internal static class PipelineCompiler
{
    internal static CompiledExecutionPlan Compile(PipelineRecipe recipe, CompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(options.BandRows);

        var explanation = new List<string>();
        var steps = new List<PlanStep>();
        var sections = new List<CompiledSection>();
        var width = options.SourceWidth;
        var height = options.SourceHeight;
        var sizeKnown = true;
        var bytesPerPixel = options.SourceFormat.BytesPerPixel();

        ValidateFormats(recipe, options.SourceFormat);

        var index = 0;
        while (index < recipe.Count)
        {
            if (recipe.Steps[index] is OperatorStep)
            {
                var first = steps.Count;
                var recipeFirst = index;
                while (index < recipe.Count && recipe.Steps[index] is OperatorStep op)
                {
                    steps.Add(new PlanStep(op.Operator));
                    explanation.Add($"step {index} {op.Name}: {op.Operator.Category}, " +
                        $"{op.Operator.Contract.Footprint.Describe}, {op.Operator.Contract.Requirements.Partition}");
                    index++;
                }

                var count = steps.Count - first;
                var fused = DecideFusion(steps, first, count, width, height, bytesPerPixel, options, explanation);
                sections.Add(new CompiledSection(CompiledSectionKind.OperatorRun, first, count, recipeFirst)
                {
                    Fused = fused,
                });
                continue;
            }

            var geometry = (GeometryStep)recipe.Steps[index];
            ApplyGeometrySize(geometry, ref width, ref height, ref sizeKnown);
            explanation.Add($"step {index} {geometry.Name}: {Describe(geometry.Geometry)}, " +
                (geometry.CanWrap ? "row-wrappable" : "materialises"));
            sections.Add(new CompiledSection(CompiledSectionKind.Geometry, -1, 0, index));
            index++;
        }

        var tileRefusal = options.TileRequested ? RefuseTile(recipe, options) : (TileRefusal?)null;
        var streamRefusal = ClassifyStream(recipe, steps, options, out var streamBlocked);
        var regionRefusal = ClassifyRegion(recipe, steps, options, out var regionBlocked);

        ExecutionBackend backend;
        if (tileRefusal is not null)
        {
            backend = ExecutionBackend.TileRefused;
            explanation.Add($"backend: TileRefused ({tileRefusal})");
        }
        else if (options.Region is { } region && regionBlocked is null && regionRefusal == RegionRefusal.None)
        {
            backend = ExecutionBackend.Region;
            explanation.Add($"backend: Region ({region.Width}x{region.Height})");
        }
        else if (streamBlocked is null && streamRefusal == StreamRefusal.None)
        {
            backend = ExecutionBackend.Stream;
            explanation.Add("backend: Stream");
        }
        else
        {
            backend = ExecutionBackend.Frame;
            explanation.Add("backend: Frame");
        }

        if (streamRefusal != StreamRefusal.None)
            explanation.Add($"stream refused: {streamRefusal}");
        else if (streamBlocked is not null && tileRefusal is null)
            explanation.Add($"stream unavailable: {streamBlocked}");

        if (options.Region is not null)
        {
            if (regionRefusal != RegionRefusal.None)
                explanation.Add($"region refused: {regionRefusal}");
            else if (regionBlocked is not null)
                explanation.Add($"region unavailable: {regionBlocked}");
        }

        explanation.Add(sizeKnown ? $"output: {width}x{height}" : "output: size depends on content");

        return new CompiledExecutionPlan(recipe, steps, sections, backend, streamRefusal, regionRefusal,
            tileRefusal, width, height, sizeKnown, explanation);
    }

    private static void ValidateFormats(PipelineRecipe recipe, WorkingFormat format)
    {
        var mask = Mask(format);
        foreach (var step in recipe.Steps)
        {
            if (step is OperatorStep op && (op.Operator.Contract.Formats & mask) == 0)
                throw new NotSupportedException(
                    $"Step {step.Index} '{step.Name}' does not support {format} " +
                    $"(it declares {op.Operator.Contract.Formats}). Chain the operator differently or convert first.");
        }
    }

    private static bool DecideFusion(IReadOnlyList<PlanStep> steps, int first, int count, int width, int height,
        int bytesPerPixel, CompileOptions options, List<string> explanation)
    {
        var allowed = options.Execute.AllowFusion && !options.ObserveSteps;
        var fused = allowed &&
            (options.Execute.ForceFusion || FusionDecision.ShouldFuse(steps, first, count, width, height, bytesPerPixel));

        var reason = !options.Execute.AllowFusion ? "fusion disabled"
            : options.ObserveSteps ? "observer needs full steps"
            : fused ? "fusion chosen"
            : "stepwise";
        explanation.Add($"steps {first}-{first + count - 1}: {(fused ? "fused" : "stepwise")} ({reason})");
        return fused;
    }

    private static void ApplyGeometrySize(GeometryStep step, ref int width, ref int height, ref bool known)
    {
        switch (step.Geometry)
        {
            case SameSizeRule:
                return;

            case FixedSizeRule fixedSize:
                width = fixedSize.Width;
                height = fixedSize.Height;
                return;

            case ContentDerivedRule:
                known = false;
                return;

            default:
                throw new NotSupportedException(
                    $"The geometry rule {step.Geometry.GetType().Name} at step {step.Index} has no size rule.");
        }
    }

    /// <summary>Returns the first step-driven refusal; <paramref name="blocked"/> names a non-refusal reason the
    /// stream cannot run (observer, sink, source), or null when nothing blocked it.</summary>
    private static StreamRefusal ClassifyStream(PipelineRecipe recipe, IReadOnlyList<PlanStep> steps,
        CompileOptions options, out string? blocked)
    {
        blocked = null;
        if (options.ObserveSteps)
        {
            blocked = "an observer asks for every step's full output";
            return StreamRefusal.None;
        }

        if ((options.Sink & SinkCapabilities.RowWritable) == 0)
        {
            blocked = "the destination does not accept rows";
            return StreamRefusal.None;
        }

        if ((options.Source & (SourceCapabilities.ForwardOnly | SourceCapabilities.Seekable)) == 0)
        {
            blocked = "the source cannot produce rows";
            return StreamRefusal.None;
        }

        foreach (var step in recipe.Steps)
        {
            if (step is GeometryStep geometry && !geometry.CanWrap)
                return StreamRefusal.GeometricStep;
        }

        return StreamingExecutor.Classify(steps, out _);
    }

    private static RegionRefusal ClassifyRegion(PipelineRecipe recipe, IReadOnlyList<PlanStep> steps,
        CompileOptions options, out string? blocked)
    {
        blocked = null;
        if (options.Region is null)
            return RegionRefusal.None;

        if ((options.Source & SourceCapabilities.RandomRegion) == 0)
        {
            blocked = "the source cannot read a rectangle without decoding it whole";
            return RegionRefusal.None;
        }

        if (options.ObserveSteps)
        {
            blocked = "an observer asks for every step's full output";
            return RegionRefusal.None;
        }

        foreach (var step in recipe.Steps)
        {
            if (step is GeometryStep)
                return RegionRefusal.GeometricStep;
        }

        return RegionExecutor.Classify(steps, hasResize: false, out _);
    }

    private static TileRefusal RefuseTile(PipelineRecipe recipe, CompileOptions options)
    {
        if ((options.Sink & SinkCapabilities.RandomRegionWrite) == 0)
            return TileRefusal.NoRegionSink;

        foreach (var step in recipe.Steps)
        {
            if (step is not OperatorStep op)
                return TileRefusal.NotTileable;

            if (op.Operator.Contract.Footprint.IsBlockAligned)
                return TileRefusal.BlockAlignedOperator;
            if (op.Operator.Category == OpCategory.Global)
                return TileRefusal.GlobalStep;
            if (op.Operator.Category == OpCategory.Composite)
                return TileRefusal.CompositeStep;
        }

        // No operator declares tile partitioning yet; the Tile backend has nothing to run.
        return TileRefusal.NotTileable;
    }

    private static string Describe(GeometryRule geometry) => geometry switch
    {
        SameSizeRule => "same size",
        FixedSizeRule fixedSize => $"{fixedSize.Width}x{fixedSize.Height}",
        ContentDerivedRule => "content-derived size",
        _ => geometry.GetType().Name,
    };

    private static WorkingFormatMask Mask(WorkingFormat format) => format switch
    {
        WorkingFormat.Rgba8 => WorkingFormatMask.Rgba8,
        WorkingFormat.Rgba16 => WorkingFormatMask.Rgba16,
        WorkingFormat.Rgba32F => WorkingFormatMask.Rgba32F,
        _ => WorkingFormatMask.None,
    };
}
