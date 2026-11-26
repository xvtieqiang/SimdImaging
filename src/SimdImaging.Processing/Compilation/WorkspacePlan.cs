using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Contracts;
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
    ResizeSourceRow = 9,
    FormatStep = 10,
    GeometryScratch = 11,
    SectionTransition = 12,
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

    /// <summary>Sum of the engine-managed terms. For a frame plan the sections run sequentially, so this is an upper
    /// bound rather than a simultaneous peak; for a stream plan the pull chain keeps every stage's windows and
    /// reservations alive at once, so the sum is the peak the workspace account sees.</summary>
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

        return backend switch
        {
            ExecutionBackend.Stream => EstimateStream(recipe, steps, states, options, terms, unmanaged),
            ExecutionBackend.Tile => EstimateTile(steps, options, terms, unmanaged),
            ExecutionBackend.Region => EstimateRegion(steps, options, terms, unmanaged),
            _ => EstimateFrame(recipe, steps, states, options, outputWidth, outputHeight, terms, unmanaged),
        };
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
                // Exact when the size is known, an upper bound when it is not (a content-derived crop only
                // shrinks). The declaration covers the step's own scratch; the input+output frame pair is the
                // SectionTransition term. A declared content-derived step is therefore bounded, not a lower bound.
                var (nextWidth, nextHeight) = geometry.Geometry.Map(width, height);
                var declared = geometry.Descriptor.WorkspaceModel;
                if (!declared.IsDeclared)
                {
                    geometryUndeclared++;
                }
                else if (declared.Estimate is { } estimateGeometry)
                {
                    geometryScratch += estimateGeometry(nextWidth, nextHeight, nextHeight, options.SourceFormat);
                }

                width = nextWidth;
                height = nextHeight;
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

    /// <summary>
    /// A region call owns one result rectangle and the RegionExecutor windows for its sequential bands. The full
    /// source is random-access input, not a resident frame, so it must not be charged as a frame allocation.
    /// </summary>
    private static WorkspacePlan EstimateRegion(IReadOnlyList<PlanStep> steps, CompileOptions options,
        List<WorkspaceTerm> terms, List<UnmanagedTerm> unmanaged)
    {
        var region = options.Region ?? throw new InvalidOperationException("Region backend needs a requested rectangle.");
        var haloX = 0;
        var haloY = 0;
        foreach (var step in steps)
        {
            haloX = checked(haloX + step.HaloX);
            haloY = checked(haloY + step.HaloY);
        }

        var sourceWindow = MapSourceWindow(steps, region, options.SourceWidth, options.SourceHeight);
        var windowWidth = sourceWindow.Width;
        var degree = options.Execute.MaxDegreeOfParallelism;
        var plan = ProcessingPlan.FromCompiled(steps,
            options.BandRows > 0 ? options.BandRows : ProcessingPlan.DefaultBandRows);
        var bandRows = SequentialBands.For(plan, windowWidth, region.Height, options.SourceFormat, degree);
        var windowHeight = (int)Math.Min(options.SourceHeight,
            (long)Math.Min(bandRows, region.Height) + 2L * haloY);

        var perWorker = 0L;
        var wholeImage = 0L;
        var undeclared = 0;
        foreach (var step in steps)
        {
            if (step.Scratch is not { } scratch)
            {
                undeclared++;
                continue;
            }

            var isWholeImage = step.Category is OpCategory.Global or OpCategory.Composite
                || step.Definition.Contract.Workspace.Scope == WorkspaceScope.WholeImage;
            if (isWholeImage)
                wholeImage = Math.Max(wholeImage,
                    scratch(options.SourceWidth, options.SourceHeight, options.SourceHeight, options.SourceFormat));
            else
                perWorker = Math.Max(perWorker,
                    scratch(windowWidth, options.SourceHeight, windowHeight, options.SourceFormat));
        }

        var windows = steps.Count > 1 ? 4 : steps.Count == 0 ? 0 : 2;
        var targetBytes = TargetBuffer.Bytes(region.Width, region.Height, options.SourceFormat);
        var windowBytes = (long)windows * windowWidth * windowHeight * options.SourceFormat.BytesPerPixel();
        var workers = Math.Max(1, degree > 0 ? degree : RowParallel.Degree);
        terms.Add(new WorkspaceTerm(WorkspaceTermKind.Destination, targetBytes, "requested result rectangle"));
        if (windowBytes > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.Window, windowBytes,
                $"{windows} windows of {windowWidth}x{windowHeight}"));
        if (perWorker > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.PerWorkerScratch, perWorker * workers,
                $"{perWorker} x {workers} region workers"));
        if (wholeImage > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.WholeImageScratch, wholeImage,
                "whole-image scratch declared by a region step"));

        unmanaged.Add(new UnmanagedTerm("region source",
            "random-access source and codec segments are owned outside the processing executor"));
        return new WorkspacePlan(terms, unmanaged, undeclared);
    }

    private static PixelRect MapSourceWindow(IReadOnlyList<PlanStep> steps, PixelRect destination,
        int width, int height)
    {
        var current = destination;
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            current = steps[i].Definition.Contract.Footprint
                .MapSourceRegions(current, width, height)
                .Normalize()
                .Clamp(width, height)
                .BoundingBox;
            if (current.Width == 0 || current.Height == 0)
                return current;
        }

        return current;
    }

    /// <summary>
    /// Tile execution owns one target tile and the RegionExecutor window set for that tile. It does not retain a
    /// decoded source frame or a full-frame destination, so reusing EstimateFrame here defeats the tile memory knob.
    /// </summary>
    private static WorkspacePlan EstimateTile(IReadOnlyList<PlanStep> steps, CompileOptions options,
        List<WorkspaceTerm> terms, List<UnmanagedTerm> unmanaged)
    {
        var width = Math.Min(options.SourceWidth, options.TileWidth > 0 ? options.TileWidth : options.SourceWidth);
        var height = Math.Min(options.SourceHeight, options.TileHeight > 0 ? options.TileHeight : options.SourceHeight);
        var haloX = 0;
        var haloY = 0;
        var perWorker = 0L;
        var wholeImage = 0L;
        var undeclared = 0;
        foreach (var step in steps)
        {
            haloX = checked(haloX + step.HaloX);
            haloY = checked(haloY + step.HaloY);
        }

        var windowWidth = (int)Math.Min(options.SourceWidth, (long)width + 2L * haloX);
        var windowHeight = (int)Math.Min(options.SourceHeight, (long)height + 2L * haloY);
        foreach (var step in steps)
        {
            if (step.Scratch is not { } scratch)
            {
                undeclared++;
                continue;
            }

            var isWholeImage = step.Category is OpCategory.Global or OpCategory.Composite
                || step.Definition.Contract.Workspace.Scope == WorkspaceScope.WholeImage;
            if (isWholeImage)
                wholeImage = Math.Max(wholeImage,
                    scratch(options.SourceWidth, options.SourceHeight, options.SourceHeight, options.SourceFormat));
            else
                perWorker = Math.Max(perWorker,
                    scratch(windowWidth, options.SourceHeight, windowHeight, options.SourceFormat));
        }
        var windows = steps.Count > 1 ? 4 : steps.Count == 0 ? 0 : 2;
        var tileBytes = TargetBuffer.Bytes(width, height, options.SourceFormat);
        var windowBytes = (long)windows * windowWidth * windowHeight * options.SourceFormat.BytesPerPixel();
        var workers = Math.Max(1, options.Execute.MaxDegreeOfParallelism > 0
            ? options.Execute.MaxDegreeOfParallelism
            : RowParallel.Degree);
        terms.Add(new WorkspaceTerm(WorkspaceTermKind.Destination, tileBytes, "one resident destination tile"));
        if (windowBytes > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.Window, windowBytes,
                $"{windows} windows of {windowWidth}x{windowHeight}"));
        if (perWorker > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.PerWorkerScratch, perWorker * workers,
                $"{perWorker} x {workers} tile workers"));
        if (wholeImage > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.WholeImageScratch, wholeImage,
                "whole-image scratch declared by a tile step"));

        unmanaged.Add(new UnmanagedTerm("region source",
            "the caller or codec owns the source; no full decoded frame is retained by the tile executor"));
        unmanaged.Add(new UnmanagedTerm("sink",
            "the caller owns the sink; encoder band memory is reported by the tile-to-encoder facade"));
        return new WorkspacePlan(terms, unmanaged, undeclared);
    }

    private static WorkspacePlan EstimateStream(PipelineRecipe recipe, IReadOnlyList<PlanStep> steps,
        IReadOnlyList<SectionState> states, CompileOptions options, List<WorkspaceTerm> terms,
        List<UnmanagedTerm> unmanaged)
    {
        var workers = Math.Max(1, options.Execute.MaxDegreeOfParallelism > 0
            ? options.Execute.MaxDegreeOfParallelism
            : RowParallel.Degree);
        var bandRowsOption = options.BandRows > 0 ? options.BandRows : ProcessingPlan.DefaultBandRows;

        // Every operator run has its own width, height and format; a plan after a resize must be estimated at the
        // resized geometry, not at the source. Unlike the frame path, the sections do not run one after another: a
        // pull chain keeps every stage's windows and workspace reservations alive at once, so the peak is their sum,
        // not the largest one. Scratch is summed for the same reason — each PlanRowSource reserves its own figure in
        // the shared account and releases it only when disposed.
        long windowBytes = 0;
        long perWorker = 0;
        long wholeImage = 0;
        var undeclared = 0;
        var sections = 0;
        var windowNote = string.Empty;
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
            windowBytes += (long)windows * state.InputWidth * windowRows * state.InputFormat.BytesPerPixel();
            var sectionCount = sections == 0 ? "1 operator section" : $"{sections + 1} operator sections";
            windowNote = $"{sectionCount}, largest {windows} windows of {state.InputWidth}x{windowRows}";
            sections++;

            var sectionPerWorker = 0L;
            var sectionWholeImage = 0L;
            foreach (var step in slice)
            {
                if (step.Scratch is not { } scratch)
                {
                    undeclared++;
                    continue;
                }

                // The same scope rule as MemoryEstimator and WorkingSetBudget: WholeImage is one copy, not one per
                // worker. A streaming stage has no whole-image step today, but the accounting must not disagree with
                // the other two executors about what a declaration means.
                var isWholeImage = step.Category is OpCategory.Global or OpCategory.Composite
                    || step.Definition.Contract.Workspace.Scope == WorkspaceScope.WholeImage;
                if (isWholeImage)
                {
                    sectionWholeImage = Math.Max(sectionWholeImage,
                        scratch(state.InputWidth, state.InputHeight, state.InputHeight, state.InputFormat));
                    continue;
                }

                sectionPerWorker = Math.Max(sectionPerWorker,
                    scratch(state.InputWidth, state.InputHeight, windowRows, state.InputFormat));
            }

            perWorker += sectionPerWorker;
            wholeImage += sectionWholeImage;
        }

        if (windowBytes == 0)
        {
            // A plan with no operator section still copies rows through one window pair.
            var bandRows = bandRowsOption;
            var windowRows = Math.Min(options.SourceHeight, bandRows);
            windowBytes = 2L * options.SourceWidth * windowRows * options.SourceFormat.BytesPerPixel();
            windowNote = $"2 windows of {options.SourceWidth}x{windowRows}";
        }

        terms.Add(new WorkspaceTerm(WorkspaceTermKind.Window, windowBytes, windowNote));

        if (perWorker > 0)
        {
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.PerWorkerScratch, perWorker * workers,
                $"{perWorker} x {workers} workers across {sections} sections"));
        }

        if (wholeImage > 0)
        {
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.WholeImageScratch, wholeImage,
                "whole-image scratch across sections"));
        }

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
        long ring = 0, staging = 0, batch = 0, worker = 0, sourceRow = 0;
        var ringNote = string.Empty;
        var stagingNote = string.Empty;
        var batchNote = string.Empty;
        var workerNote = string.Empty;
        var sourceRowNote = string.Empty;

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

                // CreateStreamChain constructs every geometry row source up front. Each resize keeps its ring,
                // staging, ready batch and source row alive until the outer chain is disposed, so these are
                // simultaneous resident allocations and must be summed rather than taking the largest stage.
                ring = checked(ring + workspace.RingBytes);
                staging = checked(staging + workspace.StagingBytes);
                batch = checked(batch + workspace.ReadyBytes);
                worker = checked(worker + workspace.WorkerRowBytes);
                sourceRow = checked(sourceRow + workspace.SourceRowBytes);
                ringNote = $"{ring / 1024} KiB across resize stages";
                stagingNote = $"{staging / 1024} KiB across resize stages";
                batchNote = $"{batch / 1024} KiB across resize stages";
                workerNote = $"{worker / 1024} KiB across resize stages";
                sourceRowNote = $"{sourceRow / 1024} KiB across resize stages";
            }

            width = destWidth;
            height = destHeight;
        }

        // Every resize row source is constructed before pulling begins, so all of their resident buffers overlap.
        if (ring > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeRing, ring, ringNote));
        if (staging > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeStaging, staging, stagingNote));
        if (batch > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeBatch, batch, batchNote));
        if (worker > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeWorkerRows, worker, workerNote));
        if (sourceRow > 0)
            terms.Add(new WorkspaceTerm(WorkspaceTermKind.ResizeSourceRow, sourceRow, sourceRowNote));
    }
}
