using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Contracts;

namespace SimdImaging.Processing.Compilation;

/// <summary>Reshapes a view into a new buffer: what a geometry step does when the image is in memory.</summary>
internal delegate WorkingBuffer ReshapeGeometry(PixelView source, IPixelPool? pool);

/// <summary>Presents a geometry step as a row source: what it does when the image is streamed.</summary>
internal delegate IRowSource WrapRowSource(IRowSource source, IPixelPool? pool, ExecuteOptions? execute);

/// <summary>
/// One step of a <see cref="PipelineRecipe"/>: either an operator or a geometry change.
/// </summary>
internal abstract class RecipeStep
{
    protected RecipeStep(int index, string name, GeometryRule geometry)
    {
        Index = index;
        Name = name;
        Geometry = geometry;
    }

    /// <summary>Position in the recipe, used in explanations and compiled sections.</summary>
    internal int Index { get; }

    internal string Name { get; }

    internal GeometryRule Geometry { get; }
}

/// <summary>A same-size operator step.</summary>
internal sealed class OperatorStep(int index, ImageOp op, byte[]? lut = null, PixelView? second = null)
    : RecipeStep(index, op.Name, op.Contract.Geometry)
{
    internal ImageOp Operator { get; } = op;

    /// <summary>The composed byte table when the step came from <see cref="ProcessingPlan.AddPointLut"/>; the
    /// compiler passes it to <see cref="PlanStep"/> so consecutive tables still fuse.</summary>
    internal byte[]? Lut { get; } = lut;

    /// <summary>The injected second input, when the step has one; it travels into the compiled steps.</summary>
    internal PixelView? Second { get; } = second;
}

/// <summary>
/// A step that changes size or coordinates. It carries both execution forms it has: the in-memory reshape and,
/// when the change is row-local (crop, pad, horizontal flip, resize), the row-source wrapper.
/// </summary>
internal sealed class GeometryStep : RecipeStep
{
    internal GeometryStep(int index, GeometryDescriptor descriptor)
        : base(index, descriptor.Name, descriptor.Geometry)
    {
        Descriptor = descriptor;
        Kind = descriptor.Kind;
        Resampler = descriptor.Resampler;
        Reshape = descriptor.Reshape ?? throw new ArgumentNullException(nameof(descriptor));
        Wrap = descriptor.Wrap;
        MapRegions = descriptor.MapRegions;
    }

    /// <summary>The declaration this step was built from: pass, state, order, determinism, alpha and workspace.</summary>
    internal GeometryDescriptor Descriptor { get; }

    internal GeometryKind Kind { get; }

    /// <summary>The resampler a resize step uses; null for every other kind. The region path needs it because
    /// <c>Image.ResizeRegion</c> takes the resampler as an argument rather than closing over it.</summary>
    internal Resampler? Resampler { get; }

    internal ReshapeGeometry Reshape { get; }

    internal WrapRowSource? Wrap { get; }

    /// <summary>The inverse region mapping when the step can express one; null means the whole source.</summary>
    internal MapGeometryRegions? MapRegions { get; }

    /// <summary>True when a stream backend can run this step without materialising. A row form is necessary but not
    /// sufficient: a step that declares per-image state or source ordering cannot be replayed by a row pump.</summary>
    internal bool CanWrap => Descriptor.Requirements.Partition == Partitioning.RowBands;
}

/// <summary>
/// A geometry step as the builder receives it: the execution forms without the position, which the builder assigns.
/// </summary>
internal delegate SourceRegionSet MapGeometryRegions(PixelRect destination, int sourceWidth, int sourceHeight,
    int destWidth, int destHeight);

/// <summary>
/// A geometry step's declaration, in the same vocabulary as <see cref="OperatorContract"/>: one source port, a
/// geometry rule, execution requirements, pass model, pixel semantics and workspace. The execution forms
/// (<see cref="Reshape"/>, <see cref="Wrap"/>, <see cref="MapRegions"/>) stay next to it but are not part of the
/// declaration, exactly as kernels are separate from an operator contract.
/// </summary>
internal sealed record GeometryDescriptor(string Name, GeometryRule Geometry, ReshapeGeometry Reshape,
    WrapRowSource? Wrap = null, GeometryKind Kind = GeometryKind.Other, Resampler? Resampler = null,
    MapGeometryRegions? MapRegions = null, PassModel Pass = PassModel.Single, StateScope State = StateScope.None,
    ExecutionOrder Order = ExecutionOrder.Any, Determinism Determinism = Determinism.CoordinateDeterministic,
    PixelSemantics? Semantics = null, WorkspaceModel? Workspace = null)
{
    /// <summary>The requirements a row pump must honour. State or ordering turn a row-wrappable step into a
    /// whole-image one, because the pump may interleave calls and cannot replay a previous band.</summary>
    internal ExecutionRequirements Requirements => new(
        Wrap is not null && State == StateScope.None && Order == ExecutionOrder.Any
            ? Partitioning.RowBands
            : Partitioning.WholeImage,
        State, Order, ThreadSafety.Concurrent, Determinism);

    /// <summary>What this step holds privately; a step that has not declared it is reported as a lower bound.</summary>
    internal WorkspaceModel WorkspaceModel => Workspace ?? Contracts.WorkspaceModel.None;

    internal PixelSemantics PixelSemantics => Semantics ?? Contracts.PixelSemantics.EncodedFloat;

    internal StepRequirements StepRequirements => new(Requirements.Partition, Pass, State, Order, Determinism,
        PixelSemantics);
}

/// <summary>The geometry operations the library exposes as steps. The compiler does not need this; the file-side
/// paths do, to recognise the one geometry step a region pipeline can carry.</summary>
internal enum GeometryKind : byte
{
    Other = 0,
    Resize = 1,
    Crop = 2,
    Pad = 3,
    FlipHorizontal = 4,
}

/// <summary>
/// An immutable chain of operator and geometry steps. Built once by a builder and then compiled as often as the
/// caller likes, against different sources, sinks and budgets.
/// </summary>
internal sealed class PipelineRecipe
{
    private readonly RecipeStep[] _steps;

    internal PipelineRecipe(IReadOnlyList<RecipeStep> steps, int version)
    {
        _steps = [.. steps];
        Version = version;
    }

    internal static PipelineRecipe Empty { get; } = new([], 0);

    internal IReadOnlyList<RecipeStep> Steps => _steps;

    internal int Count => _steps.Length;

    /// <summary>Bumped on every append; a compiled plan caches against it.</summary>
    internal int Version { get; }
}

/// <summary>Mutable while the caller is describing a chain. Never read during execution.</summary>
internal sealed class PipelineRecipeBuilder
{
    private readonly List<RecipeStep> _steps = [];
    private int _version;

    internal int Count => _steps.Count;

    internal int Version => _version;

    internal bool IsEmpty => _steps.Count == 0;

    /// <summary>The steps appended so far, in order. Read-only by contract; callers use it to answer questions like
    /// "is there already a resize", not to edit the chain.</summary>
    internal IReadOnlyList<RecipeStep> Steps => _steps;

    internal PipelineRecipeBuilder Append(ImageOp op, byte[]? lut = null, PixelView? second = null)
    {
        ArgumentNullException.ThrowIfNull(op);
        _steps.Add(new OperatorStep(_steps.Count, op, lut, second));
        _version++;
        return this;
    }

    internal PipelineRecipeBuilder Append(GeometryDescriptor geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        _steps.Add(new GeometryStep(_steps.Count, geometry));
        _version++;
        return this;
    }

    internal PipelineRecipe Build() => new(_steps, _version);
}
