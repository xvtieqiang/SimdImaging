using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Compilation;

/// <summary>
/// Runs an immutable <see cref="CompiledExecutionPlan"/> on the backend it chose, using the executors that already
/// implement that backend. Both facades call this, so "the compiler said Stream" and "these rows were pulled" are the
/// same code path rather than two copies that agree by inspection.
/// </summary>
internal static class CompiledPlanExecutor
{
    /// <summary>
    /// Runs the plan over an in-memory image: operator sections become <see cref="ProcessingPlan"/>s, geometry
    /// sections call the reshape they carry. The source view is not disposed; the returned buffer is the caller's.
    /// </summary>
    internal static WorkingBuffer ExecuteFrame(CompiledExecutionPlan plan, PixelView source, IPixelPool? pool = null,
        ExecuteOptions? options = null, PlanObserver? observe = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new ExecuteOptions();

        var current = source;
        var sourceBytes = checked((long)source.Width * source.Height * source.Format.BytesPerPixel());
        WorkingBuffer? owned = null;
        IDisposable? retainedSourceLease = null;
        var retainedSourceReserved = false;
        var hasCompletedSection = false;
        try
        {
            for (var sectionIndex = 0; sectionIndex < plan.Sections.Count; sectionIndex++)
            {
                var section = plan.Sections[sectionIndex];
                // The caller's original source remains live after the first section has produced an owned frame.
                // Keep that baseline in the shared account while later sections operate on their own intermediates.
                if (hasCompletedSection && !retainedSourceReserved)
                {
                    retainedSourceLease = options.Workspace?.Reserve(sourceBytes, "compiled frame retained source");
                    retainedSourceReserved = true;
                    if (options.MaxWorkingBytes > 0 && sourceBytes >= options.MaxWorkingBytes)
                        throw new InvalidOperationException(
                            $"The retained source needs {sourceBytes} bytes, leaving no budget for the next frame section.");
                }

                WorkingBuffer next;
                if (section.Kind == CompiledSectionKind.OperatorRun)
                {
                    var sectionObserver = observe is null
                        ? null
                        : new PlanObserver((index, name, view) => observe(section.FirstStep + index, name, view));
                    var sectionOptions = options;
                    if (hasCompletedSection && options.MaxWorkingBytes > 0)
                        sectionOptions = options with
                        {
                            MaxWorkingBytes = options.MaxWorkingBytes - sourceBytes,
                        };

                    // The compiler already decided this run. Force the nested plan to make the same decision:
                    // without this the section flag was dead metadata and the runtime re-decided from scratch,
                    // so a compiled plan and a direct plan could fuse different runs.
                    sectionOptions = sectionOptions with
                    {
                        AllowFusion = options.AllowFusion && section.Fused,
                        ForceFusion = options.ForceFusion || section.Fused,
                    };
                    next = plan.SectionPlan(sectionIndex)!
                        .ExecuteWithOptions(current, sectionOptions, pool, sectionObserver);
                }
                else if (section.Kind == CompiledSectionKind.Format)
                {
                    // The whole-image form is the only one that can allocate a buffer in another format. The
                    // contract guarantees it exists for a step that declares an output format.
                    var formatStep = (OperatorStep)plan.Recipe.Steps[section.RecipeIndex];
                    var output = formatStep.Operator.Contract.Output.Format!.Value;
                    var formatOld = (long)current.Width * current.Height * current.Format.BytesPerPixel();
                    var formatNew = (long)current.Width * current.Height * output.BytesPerPixel();
                    var formatWorkspace = formatStep.Operator.Contract.Workspace;
                    var formatScratch = 0L;
                    if (formatWorkspace.HasWholeImageEstimate ||
                        formatWorkspace.PerCallEstimate is not null)
                        formatScratch = checked(formatScratch
                            + formatWorkspace.EstimateWholeImage(current.Width, current.Height, current.Format));
                    if (formatWorkspace.HasPerWorkerEstimate)
                    {
                        var workers = formatStep.Operator.Contract.Requirements.Threading == ThreadSafety.SingleThread
                            ? 1
                            : Math.Min(RowParallel.ResolveDegree(options.MaxDegreeOfParallelism), current.Height);
                        formatScratch = checked(formatScratch + checked(formatWorkspace.EstimatePerWorkerScratch(
                            current.Width, current.Height, current.Height, current.Format) * workers));
                    }
                    var formatWorkingSet = checked(formatOld + formatNew + formatScratch);
                    var totalFormatWorkingSet = checked(formatWorkingSet
                        + (hasCompletedSection ? sourceBytes : 0L));
                    CheckBudget(options, totalFormatWorkingSet, $"the format step '{formatStep.Name}'");
                    using (options.Workspace?.Reserve(formatWorkingSet, "format transition"))
                    {
                        var degree = formatStep.Operator.Contract.Requirements.Threading == ThreadSafety.SingleThread
                            ? 1
                            : options.MaxDegreeOfParallelism;
                        using var topLevel = RowParallel.EnterTopLevel(degree);
                        next = formatStep.Second is { } second &&
                               formatStep.Operator.Kernels.WholeImageSecond is { } two
                            ? two(current, second)
                            : formatStep.Operator.Kernels.WholeImage!(current);
                    }
                }
                else
                {
                    var geometry = (GeometryStep)plan.Recipe.Steps[section.RecipeIndex];
                    var oldBytes = (long)current.Width * current.Height * current.Format.BytesPerPixel();

                    // Map is the exact output size when the step knows it and an upper bound when it does not (the
                    // content-derived rules this build ships only crop). The declaration adds the step's own
                    // scratch, so the ceiling is checked against everything the reshape can allocate — before the
                    // allocation, not after it.
                    var (width, height) = geometry.Geometry.Map(current.Width, current.Height);
                    var geometryWorkspace = geometry.Descriptor.WorkspaceModel;
                    var scratch = 0L;
                    if (geometryWorkspace.HasWholeImageEstimate ||
                        geometryWorkspace.PerCallEstimate is not null)
                        scratch = checked(scratch
                            + geometryWorkspace.EstimateWholeImage(width, height, current.Format));
                    if (geometryWorkspace.HasPerWorkerEstimate)
                    {
                        var workers = geometry.Descriptor.Requirements.Threading == ThreadSafety.SingleThread
                            ? 1
                            : Math.Min(RowParallel.ResolveDegree(options.MaxDegreeOfParallelism), height);
                        scratch = checked(scratch + checked(geometryWorkspace.EstimatePerWorkerScratch(
                            width, height, height, current.Format) * workers));
                    }
                    var bytes = checked(oldBytes
                        + ((long)width * height * current.Format.BytesPerPixel()) + scratch);
                    var totalBytes = checked(bytes + (hasCompletedSection ? sourceBytes : 0L));
                    CheckBudget(options, totalBytes, $"the geometry step '{geometry.Name}'");
                    using (options.Workspace?.Reserve(bytes, "geometry transition"))
                    {
                        var degree = geometry.Descriptor.Requirements.Threading == ThreadSafety.SingleThread
                            ? 1
                            : options.MaxDegreeOfParallelism;
                        using var topLevel = RowParallel.EnterTopLevel(degree);
                        next = geometry.Reshape(current, pool);
                    }
                }

                owned?.Dispose();
                owned = next;
                current = next.View;
                hasCompletedSection = true;
            }
        }
        catch
        {
            try
            {
                owned?.Dispose();
            }
            finally
            {
                retainedSourceLease?.Dispose();
            }
            throw;
        }

        retainedSourceLease?.Dispose();

        // An empty plan is still a request for a picture of the caller's own. Reserve the copy destination
        // before renting it, just as the non-empty paths reserve their output before execution.
        if (owned is not null)
            return owned;

        var copyBytes = checked((long)source.Width * source.Height * source.Format.BytesPerPixel());
        CheckBudget(options, checked(sourceBytes + copyBytes), "the empty frame plan copy");
        using (options.Workspace?.Reserve(copyBytes, "empty frame plan destination"))
            return Transforms.Copy(source, pool);
    }

    /// <summary>
    /// Runs the plan one rectangle at a time through an <see cref="IRegionSource"/> and an
    /// <see cref="IRegionSink"/>; the per-tile work is <see cref="RegionExecutor"/>'s, the walk is
    /// <see cref="TileExecutor"/>'s.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plan did not choose the tile backend.</exception>
    internal static void ExecuteTiles(CompiledExecutionPlan plan, IRegionSource source, IRegionSink sink,
        int tileWidth, int tileHeight, ExecuteOptions? options = null, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Backend != ExecutionBackend.Tile)
            throw new InvalidOperationException(
                $"The compiler chose {plan.Backend}, so there is no tile walk to run ({plan.TileRefusal}).");

        TileExecutor.Execute(plan.WholePlan, source, sink, tileWidth, tileHeight, options, pool);
    }

    /// <summary>
    /// Builds the pull chain the stream backend promised: one <see cref="PlanRowSource"/> per operator section, one
    /// geometry wrapper per geometry section, all owned by the returned source. The caller disposes it when the sink
    /// has taken the last row.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plan did not choose the stream backend.</exception>
    internal static IRowSource CreateStreamChain(CompiledExecutionPlan plan, IRowSource source,
        ExecuteOptions? options = null, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new ExecuteOptions();

        if (plan.Backend != ExecutionBackend.Stream)
            throw new InvalidOperationException(
                $"The compiler chose {plan.Backend}, so there is no stream chain to build " +
                $"({plan.StreamRefusal}).");

        var owned = new List<IDisposable>();
        IRowSource current = source;
        try
        {
            for (var sectionIndex = 0; sectionIndex < plan.Sections.Count; sectionIndex++)
            {
                var section = plan.Sections[sectionIndex];
                if (section.Kind == CompiledSectionKind.Format)
                    throw new InvalidOperationException(
                        "A format-changing section has no row form; the compiler must not choose Stream for it.");

                if (section.Kind == CompiledSectionKind.OperatorRun)
                {
                    // The caller's pool reaches the stream windows too: a stage that silently fell back to the shared
                    // pool would be invisible to a counting or limiting pool the caller passed in for exactly this.
                    var stage = new PlanRowSource(plan.SectionPlan(sectionIndex)!, current, options, pool);
                    owned.Add(stage);
                    current = stage;
                    continue;
                }

                var geometry = (GeometryStep)plan.Recipe.Steps[section.RecipeIndex];
                var next = geometry.Wrap!(current, pool, options);
                if (next is IDisposable disposable)
                    owned.Add(disposable);
                current = next;
            }

            return new OwnedRowChain(current, owned);
        }
        catch
        {
            for (var i = owned.Count - 1; i >= 0; i--)
                owned[i].Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs the plan over one rectangle of an <see cref="IRegionSource"/>: the compiler picked the region backend and
    /// derived the source regions; the per-rectangle work is <see cref="RegionExecutor"/>'s.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plan did not choose the region backend.</exception>
    internal static RegionResult ExecuteRegion(CompiledExecutionPlan plan, IRegionSource source, PixelRect region,
        PixelView destination, ExecuteOptions? options = null, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Backend != ExecutionBackend.Region)
            throw new InvalidOperationException(
                $"The compiler chose {plan.Backend}, so there is no region run ({plan.RegionRefusal}).");

        return RegionExecutor.Execute(plan.WholePlan, source, region, destination, options, pool);
    }

    private static void CheckBudget(ExecuteOptions options, long bytes, string what)
    {
        if (options.MaxWorkingBytes > 0 && bytes > options.MaxWorkingBytes)
            throw new InvalidOperationException(
                $"{what} needs {bytes} bytes, above ExecuteOptions.MaxWorkingBytes = {options.MaxWorkingBytes}.");
    }

    /// <summary>
    /// The pull chain, wrapped so its ownership is unconditional: disposing it releases the stages it built and never
    /// the <see cref="IRowSource"/> it was given. An empty plan has no stages, and that is the case this wrapper used
    /// to skip — which made "dispose what BuildChain returned" mean "dispose the caller's decoder".
    /// </summary>
    internal sealed class OwnedRowChain(IRowSource inner, List<IDisposable> owned) : IRowSource, IDisposable
    {
        public int Width => inner.Width;
        public int Height => inner.Height;
        public WorkingFormat Format => inner.Format;
        public int ReadRows(Span<byte> destination, int stride, int maxRows) =>
            inner.ReadRows(destination, stride, maxRows);

        /// <summary>Bands produced by the busiest stage; a stage that has not run reports 0.</summary>
        internal int Bands
        {
            get
            {
                var bands = 0;
                foreach (var stage in owned)
                {
                    if (stage is PlanRowSource rows && rows.Bands > bands)
                        bands = rows.Bands;
                }

                return bands;
            }
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            for (var i = owned.Count - 1; i >= 0; i--)
                owned[i].Dispose();
        }
    }
}
