using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Compilation;

/// <summary>One engine-managed term of a workspace estimate.</summary>
internal enum WorkspaceTermKind : byte
{
    Source = 0,
    Destination = 1,
    Window = 2,
    PerWorkerScratch = 3,
    WholeImageScratch = 4,
    ResizeRing = 5,
    ResizeStaging = 6,
    ResizeBatch = 7,
    ResizeWorkerRows = 8,
    FormatStep = 9,
    GeometryScratch = 10,
}

internal readonly record struct WorkspaceTerm(WorkspaceTermKind Kind, long Bytes, string Note);

/// <summary>Memory the engine does not own and therefore cannot budget.</summary>
internal readonly record struct UnmanagedTerm(string Name, string Note);

/// <summary>
/// What one compiled plan is predicted to hold, by term, plus the list of things it cannot see. This is a report,
/// not an enforcement mechanism: <c>MaxWorkingBytes</c> still refuses inside the executors, using the same arithmetic
/// these terms come from.
/// </summary>
internal sealed class WorkspacePlan
{
    internal WorkspacePlan(IReadOnlyList<WorkspaceTerm> terms, IReadOnlyList<UnmanagedTerm> unmanaged,
        int undeclaredSteps)
    {
        Terms = Array.AsReadOnly(terms.ToArray());
        Unmanaged = Array.AsReadOnly(unmanaged.ToArray());
        UndeclaredSteps = undeclaredSteps;
        BoundedBytes = terms.Sum(term => term.Bytes);
    }

    internal IReadOnlyList<WorkspaceTerm> Terms { get; }

    internal IReadOnlyList<UnmanagedTerm> Unmanaged { get; }

    /// <summary>Sum of the engine-managed terms. Sequential stages make this an upper bound, not a simultaneous peak.</summary>
    internal long BoundedBytes { get; }

    /// <summary>Steps whose operator declared no workspace: the figure is then a lower bound.</summary>
    internal int UndeclaredSteps { get; }

    internal bool IsLowerBound => UndeclaredSteps > 0;
}

/// <summary>Builds a <see cref="WorkspacePlan"/> from the same helpers the executors use.</summary>
internal static class WorkspacePlanner
{
    internal static WorkspacePlan Estimate(PipelineRecipe recipe, IReadOnlyList<PlanStep> steps,
        IReadOnlyList<CompiledSection> sections, ExecutionBackend backend, CompileOptions options, int outputWidth,
        int outputHeight)
    {
        var terms = new List<WorkspaceTerm>();
        var unmanaged = new List<UnmanagedTerm>
        {
            new("weight tables", "reconstruction weights cached process-wide, bounded by MemoryBudget"),
            new("float scratch", "per-thread idle retention, bounded by MemoryBudget"),
            new("pixel pool", "retained large blocks, bounded by MemoryBudget"),
            new("codec buffers", "decoder and encoder rows are outside the engine's allocator"),
        };

        if (backend == ExecutionBackend.Tile)
        {
            unmanaged.Add(new UnmanagedTerm("tile dimensions",
                "the caller's tile size sets the real window; this report uses the frame estimate, which is larger"));
        }

        return backend == ExecutionBackend.Stream
            ? EstimateStream(recipe, steps, options, terms, unmanaged)
            : EstimateFrame(recipe, steps, sections, options, outputWidth, outputHeight, terms, unmanaged);
    }

    private static WorkspacePlan EstimateFrame(PipelineRecipe recipe, IReadOnlyList<PlanStep> steps,
        IReadOnlyList<CompiledSection> sections, CompileOptions options, int outputWidth, int outputHeight,
        List<WorkspaceTerm> terms, List<UnmanagedTerm> unmanaged)
    {
        var bandRows = options.BandRows > 0 ? options.BandRows : ProcessingPlan.DefaultBandRows;
        var fused = sections.Any(section => section.Fused);

        // A format section's output is one frame of the new format, and the sections after it run in that format.
        // Estimate the plan scratch with the widest format in the chain; over-reporting is the safe direction.
        var width = options.SourceWidth;
        var height = options.SourceHeight;
        var widest = options.SourceFormat;
        var finalFormat = options.SourceFormat;
        var formatTerms = new List<WorkspaceTerm>();
        var geometryUndeclared = 0;
        var geometryScratch = 0L;
        foreach (var step in recipe.Steps)
        {
            if (step is GeometryStep geometry)
            {
                if (geometry.Geometry.SizeKnown)
                    (width, height) = geometry.Geometry.Map(width, height);

                var declared = geometry.Descriptor.WorkspaceModel;
                if (!declared.IsDeclared)
                    geometryUndeclared++;
                else if (geometry.Geometry.SizeKnown && declared.Estimate is { } estimateGeometry)
                    geometryScratch += estimateGeometry(width, height, height, options.SourceFormat);
                continue;
            }

            if (step is OperatorStep { Operator.Contract.Output.Format: { } outputFormat })
            {
                formatTerms.Add(new WorkspaceTerm(WorkspaceTermKind.FormatStep,
                    (long)width * height * outputFormat.BytesPerPixel(), $"{width}x{height} {outputFormat}"));
                if (outputFormat.BytesPerPixel() > widest.BytesPerPixel())
                    widest = outputFormat;
                finalFormat = outputFormat;
            }
        }

        var estimate = MemoryEstimator.Estimate(options.SourceWidth, options.SourceHeight, widest,
            steps, bandRows, allowFusion: fused, options.Execute.MaxDegreeOfParallelism);

        terms.Add(new WorkspaceTerm(WorkspaceTermKind.Source,
            (long)options.SourceWidth * options.SourceHeight * options.SourceFormat.BytesPerPixel(),
            "decoded source"));
        terms.Add(new WorkspaceTerm(WorkspaceTermKind.Destination,
            (long)outputWidth * outputHeight * finalFormat.BytesPerPixel(), "result image"));
        terms.AddRange(formatTerms);
        if (estimate.PlanScratchBytes > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.Window, estimate.PlanScratchBytes, "plan buffers"));
        if (estimate.PerWorkerBytes > 0)
        {
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.PerWorkerScratch, estimate.PerWorkerBytes * estimate.Workers,
                $"{estimate.PerWorkerBytes} x {estimate.Workers} workers"));
        }

        if (estimate.WholeImageScratchBytes > 0)
        {
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.WholeImageScratch, estimate.WholeImageScratchBytes,
                "whole-image operator scratch"));
        }

        if (geometryScratch > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.GeometryScratch, geometryScratch, "geometry scratch"));

        if (sections.Any(section => section.Kind == CompiledSectionKind.Geometry))
        {
            unmanaged.Add(new UnmanagedTerm("geometry intermediates",
                "a geometry step's reshape buffer is released before the next section and is not in MemoryEstimator"));
        }

        return new WorkspacePlan(terms, unmanaged, estimate.UndeclaredSteps + geometryUndeclared);
    }

    private static WorkspacePlan EstimateStream(PipelineRecipe recipe, IReadOnlyList<PlanStep> steps,
        CompileOptions options, List<WorkspaceTerm> terms, List<UnmanagedTerm> unmanaged)
    {
        var bandRowsOption = options.BandRows > 0 ? options.BandRows : ProcessingPlan.DefaultBandRows;
        var plan = ProcessingPlan.FromCompiled(steps, bandRowsOption);
        var bandRows = SequentialBands.For(plan, options.SourceWidth, options.SourceHeight, options.SourceFormat,
            options.Execute.MaxDegreeOfParallelism);

        var halo = 0;
        foreach (var step in steps)
            halo += step.HaloY;

        var windowRows = Math.Min(options.SourceHeight, bandRows + (2 * halo));
        var windows = steps.Count > 1 ? 4 : 2;
        var windowBytes = (long)windows * options.SourceWidth * windowRows * options.SourceFormat.BytesPerPixel();
        terms.Add(new WorkspaceTerm(WorkspaceTermKind.Window, windowBytes,
            $"{windows} windows of {options.SourceWidth}x{windowRows}"));

        var workers = Math.Max(1, options.Execute.MaxDegreeOfParallelism > 0
            ? options.Execute.MaxDegreeOfParallelism
            : Environment.ProcessorCount);
        long perWorker = 0;
        var undeclared = 0;
        foreach (var step in steps)
        {
            if (step.Scratch is not { } scratch)
            {
                undeclared++;
                continue;
            }

            perWorker = Math.Max(perWorker, scratch(options.SourceWidth, options.SourceHeight, windowRows,
                options.SourceFormat));
        }

        if (perWorker > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.PerWorkerScratch, perWorker * workers,
                $"{perWorker} x {workers} workers"));

        AddResizeTerms(recipe, options, terms);

        foreach (var step in recipe.Steps)
        {
            if (step is GeometryStep { Descriptor.WorkspaceModel.IsDeclared: false })
                undeclared++;
        }

        unmanaged.Add(new UnmanagedTerm("decoder and encoder rows",
            "the row reader and encoder own their buffers; ProcessReport says whether either streamed"));
        unmanaged.Add(new UnmanagedTerm("destination",
            "the sink writes as rows arrive; there is no engine-held destination image"));
        return new WorkspacePlan(terms, unmanaged, undeclared);
    }

    private static void AddResizeTerms(PipelineRecipe recipe, CompileOptions options, List<WorkspaceTerm> terms)
    {
        var width = options.SourceWidth;
        var height = options.SourceHeight;
        foreach (var step in recipe.Steps)
        {
            if (step is not GeometryStep geometry)
                continue;

            if (!geometry.Geometry.SizeKnown)
                return;

            var (destWidth, destHeight) = geometry.Geometry.Map(width, height);
            if (geometry.Kind == GeometryKind.Resize && geometry.Resampler is { } resampler)
            {
                var weightsX = WeightTable.Build(width, destWidth, resampler);
                var weightsY = WeightTable.Build(height, destHeight, resampler);
                var nearest = resampler == Resampler.Nearest && options.SourceFormat == WorkingFormat.Rgba8
                    && Resample.TryNearestTap(weightsX, out _) && Resample.TryNearestTap(weightsY, out _);

                var workspace = ResizeWorkspace.Estimate(width, height, destWidth, destHeight, weightsY.Taps,
                    nearest, options.SourceFormat, options.Execute);

                terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeRing, workspace.RingBytes,
                    $"{workspace.RingRows} rows of {destWidth}"));
                terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeStaging, workspace.StagingBytes,
                    $"{workspace.ChunkRows} source rows"));
                terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeBatch, workspace.ReadyBytes,
                    $"{workspace.ReadyRows} destination rows"));
                if (workspace.WorkerRowBytes > 0)
                {
                    terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeWorkerRows, workspace.WorkerRowBytes,
                        $"{workspace.Workers} workers"));
                }

                return;
            }

            width = destWidth;
            height = destHeight;
        }
    }
}
