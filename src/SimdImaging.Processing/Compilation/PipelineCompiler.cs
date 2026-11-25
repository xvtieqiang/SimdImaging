using Sdcb.SimdCodecs.Abstractions;
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
        var states = new List<SectionState>();
        var width = options.SourceWidth;
        var height = options.SourceHeight;
        var sizeKnown = true;
        var currentFormat = options.SourceFormat;

        var index = 0;
        while (index < recipe.Count)
        {
            // A format-changing step is a boundary of its own: the plan kernel writes into a destination of the
            // source format, so this step executes as a whole-image reshape and the next run starts in the new
            // format.
            if (recipe.Steps[index] is OperatorStep formatStep &&
                formatStep.Operator.Contract.Output.Format is { } outputFormat)
            {
                ValidateFormat(formatStep, currentFormat);
                explanation.Add($"step {index} {formatStep.Name}: format {currentFormat} -> {outputFormat}, materialises");
                sections.Add(new CompiledSection(CompiledSectionKind.Format, -1, 0, index));
                states.Add(new SectionState(CompiledSectionKind.Format, -1, 0, index, width, height, width, height,
                    currentFormat, outputFormat));
                currentFormat = outputFormat;
                index++;
                continue;
            }

            if (recipe.Steps[index] is OperatorStep)
            {
                var first = steps.Count;
                var recipeFirst = index;
                var runWidth = width;
                var runHeight = height;
                var runFormat = currentFormat;
                while (index < recipe.Count &&
                       recipe.Steps[index] is OperatorStep op &&
                       op.Operator.Contract.Output.Format is null)
                {
                    ValidateFormat(op, currentFormat);
                    steps.Add(new PlanStep(op.Operator, op.Lut, op.Second));
                    explanation.Add($"step {index} {op.Name}: {op.Operator.Category}, " +
                        $"{op.Operator.Contract.Footprint.Describe}, {op.Operator.Contract.Requirements.Partition}");
                    index++;
                }

                var count = steps.Count - first;
                var fused = DecideFusion(steps, first, count, width, height,
                    currentFormat.BytesPerPixel(), options, explanation);
                sections.Add(new CompiledSection(CompiledSectionKind.OperatorRun, first, count, recipeFirst)
                {
                    Fused = fused,
                });
                states.Add(new SectionState(CompiledSectionKind.OperatorRun, first, count, recipeFirst, runWidth,
                    runHeight, runWidth, runHeight, runFormat, currentFormat));
                continue;
            }

            var geometry = (GeometryStep)recipe.Steps[index];
            var geometryWidth = width;
            var geometryHeight = height;
            var geometryFormat = currentFormat;
            ApplyGeometrySize(geometry, ref width, ref height, ref sizeKnown);
            explanation.Add($"step {index} {geometry.Name}: {geometry.Geometry.Describe}, " +
                (geometry.CanWrap ? "row-wrappable" : "materialises"));
            sections.Add(new CompiledSection(CompiledSectionKind.Geometry, -1, 0, index));
            states.Add(new SectionState(CompiledSectionKind.Geometry, -1, 0, index, geometryWidth, geometryHeight,
                width, height, geometryFormat, currentFormat));
            index++;
        }

        TileRefusal? tileRefusal = null;
        var tilePossible = false;
        if (options.TileRequested)
        {
            var refusal = RefuseTile(recipe, options);
            if (refusal == TileRefusal.None)
                tilePossible = true;
            else
                tileRefusal = refusal;
        }
        var streamRefusal = ClassifyStream(recipe, steps, options, out var streamBlocked);
        var regionRefusal = ClassifyRegion(recipe, steps, options, out var regionBlocked);
        var (stepInfo, regionCost) = DeriveStepInfo(recipe, steps, states, options, width, height);
        if (options.Region is not null && regionRefusal == RegionRefusal.None
            && options.MaxRegionReadAmplification > 0
            && regionCost.Amplification > options.MaxRegionReadAmplification)
        {
            regionRefusal = RegionRefusal.RegionCost;
        }

        ExecutionBackend backend;
        if (tilePossible)
        {
            backend = ExecutionBackend.Tile;
            explanation.Add("backend: Tile");
        }
        else if (tileRefusal is not null)
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

        foreach (var info in stepInfo)
        {
            var ports = string.Join("; ", info.SourceRegions.Select(port =>
                $"port {port.Port} {port.Region.X},{port.Region.Y} {port.Region.Width}x{port.Region.Height}"));
            explanation.Add($"step {info.Index} {info.Name}: output {info.Width}x{info.Height} {info.Format}, " +
                $"source {ports}, x{info.Amplification:0.00}");
        }

        explanation.Add(sizeKnown ? $"output: {width}x{height}" : "output: size depends on content");

        var workspace = WorkspacePlanner.Estimate(recipe, steps, sections, backend, options, width, height);
        if (options.Execute.MaxWorkingBytes > 0 && workspace.BoundedBytes > options.Execute.MaxWorkingBytes)
        {
            throw new InvalidOperationException(
                $"The plan's working set is at most {workspace.BoundedBytes} bytes, above " +
                $"ExecuteOptions.MaxWorkingBytes = {options.Execute.MaxWorkingBytes}. Lower the band height, split " +
                "the chain, or raise the limit.");
        }

        var bandRows = options.BandRows > 0 ? options.BandRows : ProcessingPlan.DefaultBandRows;
        return new CompiledExecutionPlan(recipe, steps, sections, backend, streamRefusal, regionRefusal,
            tileRefusal, width, height, sizeKnown, bandRows, workspace, stepInfo, regionCost, explanation);
    }

    private readonly record struct SectionState(CompiledSectionKind Kind, int FirstStep, int StepCount,
        int RecipeIndex, int InputWidth, int InputHeight, int OutputWidth, int OutputHeight,
        WorkingFormat InputFormat, WorkingFormat OutputFormat);

    /// <summary>
    /// Walks the sections backwards from the requested output rectangle. An operator step maps its output region
    /// through its footprint; a geometry step through its inverse mapping; a format change leaves it alone. Each
    /// step's read is normalized and clamped, and the chain cost is the final source area over the output area.
    /// </summary>
    private static (IReadOnlyList<CompiledStepInfo> Info, RegionCost Cost) DeriveStepInfo(PipelineRecipe recipe,
        IReadOnlyList<PlanStep> steps, IReadOnlyList<SectionState> states, CompileOptions options, int width,
        int height)
    {
        var outputRegion = options.Region ?? new PixelRect(0, 0, width, height);
        if (options.Region is null && options.TileRequested && options.TileWidth > 0 && options.TileHeight > 0)
        {
            outputRegion = new PixelRect(0, 0, Math.Min(options.TileWidth, width),
                Math.Min(options.TileHeight, height));
        }

        var region = SourceRegionSet.Of(outputRegion).Clamp(width, height);
        var infos = new List<CompiledStepInfo>();
        for (var s = states.Count - 1; s >= 0; s--)
        {
            var state = states[s];
            if (state.Kind == CompiledSectionKind.OperatorRun)
            {
                for (var j = state.FirstStep + state.StepCount - 1; j >= state.FirstStep; j--)
                {
                    var step = steps[j];
                    var outputArea = region.Area;
                    var set = step.Definition.Contract.Footprint
                        .MapSourceRegions(region.BoundingBox, state.InputWidth, state.InputHeight)
                        .Normalize().Clamp(state.InputWidth, state.InputHeight);

                    var ports = new List<PortRegion>();
                    foreach (var rect in set.Regions)
                        ports.Add(new PortRegion(0, rect));

                    for (var p = 1; p < step.Definition.Contract.Inputs.Count; p++)
                    {
                        if (step.Definition.Contract.Inputs[p].Footprint is not { } portFootprint)
                            continue;
                        var portSet = portFootprint
                            .MapSourceRegions(region.BoundingBox, state.InputWidth, state.InputHeight)
                            .Normalize().Clamp(state.InputWidth, state.InputHeight);
                        foreach (var rect in portSet.Regions)
                            ports.Add(new PortRegion(p, rect));
                    }

                    infos.Add(new CompiledStepInfo(j, step.Name, false, state.OutputWidth, state.OutputHeight,
                        state.OutputFormat, ports, set.Area, outputArea,
                        StepRequirements.Of(step.Definition.Contract)));
                    region = set;
                }
            }
            else if (state.Kind == CompiledSectionKind.Geometry)
            {
                var geometry = (GeometryStep)recipe.Steps[state.RecipeIndex];
                var outputArea = region.Area;
                var set = geometry.MapRegions?.Invoke(region.BoundingBox, state.InputWidth, state.InputHeight,
                        state.OutputWidth, state.OutputHeight)
                    ?? SourceRegionSet.Of(new PixelRect(0, 0, state.InputWidth, state.InputHeight));
                set = set.Normalize().Clamp(state.InputWidth, state.InputHeight);

                var ports = new List<PortRegion>();
                foreach (var rect in set.Regions)
                    ports.Add(new PortRegion(0, rect));
                infos.Add(new CompiledStepInfo(state.RecipeIndex, geometry.Name, true, state.OutputWidth,
                    state.OutputHeight, state.OutputFormat, ports, set.Area, outputArea,
                    StepRequirements.Boundary(geometry.CanWrap ? Partitioning.RowBands : Partitioning.WholeImage)));
                region = set;
            }
            else
            {
                var ports = new List<PortRegion>();
                foreach (var rect in region.Regions)
                    ports.Add(new PortRegion(0, rect));
                infos.Add(new CompiledStepInfo(state.RecipeIndex, "format change", false, state.OutputWidth,
                    state.OutputHeight, state.OutputFormat, ports, region.Area, region.Area,
                    StepRequirements.Boundary(Partitioning.WholeImage)));
            }
        }

        infos.Reverse();
        var finalArea = (long)outputRegion.Width * outputRegion.Height;
        return (infos, new RegionCost(region.Area, finalArea, region.Count));
    }

    /// <summary>
    /// Whether the stream backend can run this recipe, without needing the source dimensions: the file-side paths
    /// ask this before they open a row reader, and the full <see cref="Compile"/> asks the same method again with
    /// the decoded size for the sections. <paramref name="blocked"/> names a non-refusal reason (observer, sink,
    /// source) or is null.
    /// </summary>
    internal static StreamRefusal CanStream(PipelineRecipe recipe, CompileOptions options, out string? blocked)
    {
        var steps = new List<PlanStep>(recipe.Count);
        foreach (var step in recipe.Steps)
        {
            if (step is OperatorStep op)
                steps.Add(new PlanStep(op.Operator));
        }

        return ClassifyStream(recipe, steps, options, out blocked);
    }

    private static void ValidateFormat(OperatorStep step, WorkingFormat format)
    {
        var mask = Mask(format);
        if ((step.Operator.Contract.Formats & mask) == 0)
            throw new NotSupportedException(
                $"Step {step.Index} '{step.Name}' does not support {format} " +
                $"(it declares {step.Operator.Contract.Formats}). Chain the operator differently or convert first.");
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
        if (!step.Geometry.SizeKnown)
        {
            known = false;
            return;
        }

        (width, height) = step.Geometry.Map(width, height);
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
            if (step is OperatorStep { Operator.Contract.Output.Format: not null })
                return StreamRefusal.FormatChange;
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
            if (step is OperatorStep { Operator.Contract.Output.Format: not null })
                return RegionRefusal.FormatChange;
            if (step is GeometryStep)
                return RegionRefusal.GeometricStep;
        }

        return RegionExecutor.Classify(steps, hasResize: false, out _);
    }

    private static TileRefusal RefuseTile(PipelineRecipe recipe, CompileOptions options)
    {
        if ((options.Sink & SinkCapabilities.RandomRegionWrite) == 0)
            return TileRefusal.NoRegionSink;

        var planSteps = new List<PlanStep>(recipe.Count);
        foreach (var step in recipe.Steps)
        {
            if (step is not OperatorStep op)
                return TileRefusal.NotTileable;

            if (op.Operator.Contract.Output.Format is not null)
                return TileRefusal.FormatChange;
            if (op.Operator.Contract.Footprint.IsBlockAligned)
                return TileRefusal.BlockAlignedOperator;
            if (op.Operator.Category == OpCategory.Global)
                return TileRefusal.GlobalStep;
            if (op.Operator.Category == OpCategory.Composite)
                return TileRefusal.CompositeStep;
            if (!op.Operator.Contract.Requirements.SupportsTiles)
            {
                return op.Operator.Contract.Footprint.CoversWholeImage
                    ? TileRefusal.WholeSourceStep
                    : TileRefusal.NotTileable;
            }

            planSteps.Add(new PlanStep(op.Operator));
        }

        var refusal = RegionExecutor.Classify(planSteps, hasResize: false, out _);
        return refusal switch
        {
            RegionRefusal.None => TileRefusal.None,
            RegionRefusal.StepNeedsWholeSource => TileRefusal.WholeSourceStep,
            _ => TileRefusal.NotTileable,
        };
    }

    private static WorkingFormatMask Mask(WorkingFormat format) => format switch
    {
        WorkingFormat.Rgba8 => WorkingFormatMask.Rgba8,
        WorkingFormat.Rgba16 => WorkingFormatMask.Rgba16,
        WorkingFormat.Rgba32F => WorkingFormatMask.Rgba32F,
        _ => WorkingFormatMask.None,
    };
}
