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
    SectionTransition = 11,
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
        IReadOnlyList<SectionState> states, ExecutionBackend backend, CompileOptions options, int outputWidth,
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
            ? EstimateStream(recipe, steps, states, options, terms, unmanaged)
            : EstimateFrame(recipe, steps, states, options, outputWidth, outputHeight, terms, unmanaged);
    }

    private static WorkspacePlan EstimateFrame(PipelineRecipe recipe, IReadOnlyList<PlanStep> steps,
        IReadOnlyList<SectionState> states, CompileOptions options, int outputWidth, int outputHeight,
        List<WorkspaceTerm> terms, List<UnmanagedTerm> unmanaged)
    {
        var bandRows = options.BandRows > 0 ? options.BandRows : ProcessingPlan.DefaultBandRows;

        // A format section's output is one frame of the new format, and the sections after it run in that format.
        var width = options.SourceWidth;
        var height = options.SourceHeight;
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
                if (!declared.IsDeclared || !geometry.Geometry.SizeKnown)
                {
                    // An undeclared workspace is a lower bound; so is a geometry step whose output size is only
                    // known after reading the pixels (Trim, EntropyCrop, SeamCarve). Both make the prediction a
                    // floor, and a strict ceiling must refuse rather than pretend.
                    geometryUndeclared++;
                }
                else if (declared.Estimate is { } estimateGeometry)
                {
                    geometryScratch += estimateGeometry(width, height, height, options.SourceFormat);
                }

                continue;
            }

            if (step is OperatorStep { Operator.Contract.Output.Format: { } outputFormat })
            {
                formatTerms.Add(new WorkspaceTerm(WorkspaceTermKind.FormatStep,
                    (long)width * height * outputFormat.BytesPerPixel(), $"{width}x{height} {outputFormat}"));
                finalFormat = outputFormat;
            }
        }

        // Each operator run is estimated at the size and format it actually sees. Taking the maximum of the
        // sections is the right peak: sections run one after another, so their windows are never alive together.
        long maxPlan = 0;
        long maxPerWorker = 0;
        var maxWorkers = 1;
        long maxWholeImage = 0;
        var undeclared = 0;
        foreach (var state in states)
        {
            if (state.Kind != CompiledSectionKind.OperatorRun || state.StepCount == 0)
                continue;

            var slice = new PlanStep[state.StepCount];
            for (var i = 0; i < slice.Length; i++)
                slice[i] = steps[state.FirstStep + i];

            var estimate = MemoryEstimator.Estimate(state.InputWidth, state.InputHeight, state.InputFormat, slice,
                bandRows, allowFusion: state.Fused, options.Execute.MaxDegreeOfParallelism);
            maxPlan = Math.Max(maxPlan, estimate.PlanScratchBytes);
            maxPerWorker = Math.Max(maxPerWorker, estimate.PerWorkerBytes);
            maxWorkers = Math.Max(maxWorkers, estimate.Workers);
            maxWholeImage = Math.Max(maxWholeImage, estimate.WholeImageScratchBytes);
            undeclared += estimate.UndeclaredSteps;
        }

        // A geometry or format section holds the previous frame while it produces the next one. That pair is the
        // real peak, not either frame alone, and it is engine memory, so it belongs in the bound.
        long transition = 0;
        foreach (var state in states)
        {
            if (state.Kind is not (CompiledSectionKind.Geometry or CompiledSectionKind.Format))
                continue;

            var inBytes = (long)state.InputWidth * state.InputHeight * state.InputFormat.BytesPerPixel();
            var outBytes = (long)state.OutputWidth * state.OutputHeight * state.OutputFormat.BytesPerPixel();
            transition = Math.Max(transition, inBytes + outBytes);
        }

        terms.Add(new WorkspaceTerm(WorkspaceTermKind.Source,
            (long)options.SourceWidth * options.SourceHeight * options.SourceFormat.BytesPerPixel(),
            "decoded source"));
        terms.Add(new WorkspaceTerm(WorkspaceTermKind.Destination,
            (long)outputWidth * outputHeight * finalFormat.BytesPerPixel(), "result image"));
        terms.AddRange(formatTerms);
        if (maxPlan > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.Window, maxPlan, "largest operator-run window set"));
        if (maxPerWorker > 0)
        {
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.PerWorkerScratch, maxPerWorker * maxWorkers,
                $"{maxPerWorker} x {maxWorkers} workers"));
        }

        if (maxWholeImage > 0)
        {
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.WholeImageScratch, maxWholeImage,
                "whole-image operator scratch"));
        }

        if (transition > 0)
        {
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.SectionTransition, transition,
                "largest geometry/format input+output frame pair"));
        }

        if (geometryScratch > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.GeometryScratch, geometryScratch, "geometry scratch"));

        return new WorkspacePlan(terms, unmanaged, undeclared + geometryUndeclared);
    }

    private static WorkspacePlan EstimateStream(PipelineRecipe recipe, IReadOnlyList<PlanStep> steps,
        IReadOnlyList<SectionState> states, CompileOptions options, List<WorkspaceTerm> terms,
        List<UnmanagedTerm> unmanaged)
    {
        var workers = Math.Max(1, options.Execute.MaxDegreeOfParallelism > 0
            ? options.Execute.MaxDegreeOfParallelism
            : Environment.ProcessorCount);
        var bandRowsOption = options.BandRows > 0 ? options.BandRows : ProcessingPlan.DefaultBandRows;

        // Every operator run has its own width, height and format; a plan after a resize must be estimated at the
        // resized geometry, not at the source. Sections run one at a time, so the peak is the largest window set
        // and the largest per-worker scratch among them.
        long windowBytes = 0;
        long perWorker = 0;
        var undeclared = 0;
        foreach (var state in states)
        {
            if (state.Kind != CompiledSectionKind.OperatorRun || state.StepCount == 0)
                continue;

            var slice = new PlanStep[state.StepCount];
            var halo = 0;
            for (var i = 0; i < slice.Length; i++)
            {
                slice[i] = steps[state.FirstStep + i];
                halo += slice[i].HaloY;
            }

            var plan = ProcessingPlan.FromCompiled(slice, bandRowsOption);
            var bandRows = SequentialBands.For(plan, state.InputWidth, state.InputHeight, state.InputFormat,
                options.Execute.MaxDegreeOfParallelism);
            var windowRows = Math.Min(state.InputHeight, bandRows + (2 * halo));
            var windows = slice.Length > 1 ? 4 : 2;
            var sectionWindowBytes = (long)windows * state.InputWidth * windowRows *
                                     state.InputFormat.BytesPerPixel();
            if (sectionWindowBytes > windowBytes)
            {
                windowBytes = sectionWindowBytes;
                terms.RemoveAll(term => term.Kind == WorkspaceTermKind.Window);
                terms.Add(new WorkspaceTerm(WorkspaceTermKind.Window, windowBytes,
                    $"{windows} windows of {state.InputWidth}x{windowRows}"));
            }

            foreach (var step in slice)
            {
                if (step.Scratch is not { } scratch)
                {
                    undeclared++;
                    continue;
                }

                perWorker = Math.Max(perWorker, scratch(state.InputWidth, state.InputHeight, windowRows,
                    state.InputFormat));
            }
        }

        if (windowBytes == 0)
        {
            // A plan with no operator section still copies rows through one window pair.
            var bandRows = bandRowsOption;
            var windowRows = Math.Min(options.SourceHeight, bandRows);
            windowBytes = 2L * options.SourceWidth * windowRows * options.SourceFormat.BytesPerPixel();
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.Window, windowBytes,
                $"2 windows of {options.SourceWidth}x{windowRows}"));
        }

        if (perWorker > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.PerWorkerScratch, perWorker * workers,
                $"{perWorker} x {workers} workers"));

        AddResizeTerms(recipe, options, terms);

        foreach (var step in recipe.Steps)
        {
            if (step is GeometryStep geometry &&
                (!geometry.Descriptor.WorkspaceModel.IsDeclared || !geometry.Geometry.SizeKnown))
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
        long ring = 0, staging = 0, batch = 0, worker = 0;
        var ringNote = string.Empty;
        var stagingNote = string.Empty;
        var batchNote = string.Empty;
        var workerNote = string.Empty;

        foreach (var step in recipe.Steps)
        {
            if (step is not GeometryStep geometry)
                continue;

            if (!geometry.Geometry.SizeKnown)
                break;

            var (destWidth, destHeight) = geometry.Geometry.Map(width, height);
            if (geometry.Kind == GeometryKind.Resize && geometry.Resampler is { } resampler)
            {
                var weightsX = WeightTable.Build(width, destWidth, resampler);
                var weightsY = WeightTable.Build(height, destHeight, resampler);
                var nearest = resampler == Resampler.Nearest && options.SourceFormat == WorkingFormat.Rgba8
                    && Resample.TryNearestTap(weightsX, out _) && Resample.TryNearestTap(weightsY, out _);

                var workspace = ResizeWorkspace.Estimate(width, height, destWidth, destHeight, weightsY.Taps,
                    nearest, options.SourceFormat, options.Execute);

                if (workspace.RingBytes > ring)
                {
                    ring = workspace.RingBytes;
                    ringNote = $"{workspace.RingRows} rows of {destWidth}";
                }

                if (workspace.StagingBytes > staging)
                {
                    staging = workspace.StagingBytes;
                    stagingNote = $"{workspace.ChunkRows} source rows";
                }

                if (workspace.ReadyBytes > batch)
                {
                    batch = workspace.ReadyBytes;
                    batchNote = $"{workspace.ReadyRows} destination rows";
                }

                if (workspace.WorkerRowBytes > worker)
                {
                    worker = workspace.WorkerRowBytes;
                    workerNote = $"{workspace.Workers} workers";
                }
            }

            width = destWidth;
            height = destHeight;
        }

        // Resizes run one at a time, so the peak is the largest ring/staging/batch among them, not their sum.
        if (ring > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeRing, ring, ringNote));
        if (staging > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeStaging, staging, stagingNote));
        if (batch > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeBatch, batch, batchNote));
        if (worker > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeWorkerRows, worker, workerNote));
    }
}
