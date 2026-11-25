using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;

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
        WorkingBuffer? owned = null;
        try
        {
            foreach (var section in plan.Sections)
            {
                WorkingBuffer next;
                if (section.Kind == CompiledSectionKind.OperatorRun)
                {
                    var steps = Slice(plan.Steps, section.FirstStep, section.StepCount);
                    var sectionObserver = observe is null
                        ? null
                        : new PlanObserver((index, name, view) => observe(section.FirstStep + index, name, view));
                    next = ProcessingPlan.FromCompiled(steps, plan.BandRows)
                        .ExecuteWithOptions(current, options, pool, sectionObserver);
                }
                else if (section.Kind == CompiledSectionKind.Format)
                {
                    // The whole-image form is the only one that can allocate a buffer in another format. The
                    // contract guarantees it exists for a step that declares an output format.
                    var formatStep = (OperatorStep)plan.Recipe.Steps[section.RecipeIndex];
                    next = formatStep.Operator.Kernels.WholeImage!(current);
                }
                else
                {
                    var geometry = (GeometryStep)plan.Recipe.Steps[section.RecipeIndex];
                    next = geometry.Reshape(current, pool);
                }

                owned?.Dispose();
                owned = next;
                current = next.View;
            }
        }
        catch
        {
            owned?.Dispose();
            throw;
        }

        // An empty plan is still a request for a picture of the caller's own.
        return owned ?? Transforms.Copy(source, pool);
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

        TileExecutor.Execute(ProcessingPlan.FromCompiled(plan.Steps, plan.BandRows), source, sink, tileWidth,
            tileHeight, options, pool);
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
        foreach (var section in plan.Sections)
        {
            if (section.Kind == CompiledSectionKind.Format)
                throw new InvalidOperationException(
                    "A format-changing section has no row form; the compiler must not choose Stream for it.");

            if (section.Kind == CompiledSectionKind.OperatorRun)
            {
                var steps = Slice(plan.Steps, section.FirstStep, section.StepCount);
                var stage = new PlanRowSource(ProcessingPlan.FromCompiled(steps, plan.BandRows), current, options);
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

        return owned.Count == 0 ? current : new OwnedRowChain(current, owned);
    }

    private static PlanStep[] Slice(IReadOnlyList<PlanStep> steps, int first, int count)
    {
        var slice = new PlanStep[count];
        for (var i = 0; i < count; i++)
            slice[i] = steps[first + i];
        return slice;
    }

    private sealed class OwnedRowChain(IRowSource inner, List<IDisposable> owned) : IRowSource, IDisposable
    {
        public int Width => inner.Width;
        public int Height => inner.Height;
        public WorkingFormat Format => inner.Format;
        public int ReadRows(Span<byte> destination, int stride, int maxRows) =>
            inner.ReadRows(destination, stride, maxRows);

        public void Dispose()
        {
            foreach (var disposable in owned)
                disposable.Dispose();
        }
    }
}
