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
internal sealed class OperatorStep(int index, ImageOp op, byte[]? lut = null) : RecipeStep(index, op.Name,
    op.Contract.Geometry)
{
    internal ImageOp Operator { get; } = op;

    /// <summary>The composed byte table when the step came from <see cref="ProcessingPlan.AddPointLut"/>; the
    /// compiler passes it to <see cref="PlanStep"/> so consecutive tables still fuse.</summary>
    internal byte[]? Lut { get; } = lut;
}

/// <summary>
/// A step that changes size or coordinates. It carries both execution forms it has: the in-memory reshape and,
/// when the change is row-local (crop, pad, horizontal flip, resize), the row-source wrapper.
/// </summary>
internal sealed class GeometryStep : RecipeStep
{
    internal GeometryStep(int index, string name, GeometryRule geometry, GeometryKind kind, Resampler? resampler,
        ReshapeGeometry reshape, WrapRowSource? wrap = null, MapGeometryRegions? mapRegions = null)
        : base(index, name, geometry)
    {
        Kind = kind;
        Resampler = resampler;
        Reshape = reshape ?? throw new ArgumentNullException(nameof(reshape));
        Wrap = wrap;
        MapRegions = mapRegions;
    }

    internal GeometryKind Kind { get; }

    /// <summary>The resampler a resize step uses; null for every other kind. The region path needs it because
    /// <c>Image.ResizeRegion</c> takes the resampler as an argument rather than closing over it.</summary>
    internal Resampler? Resampler { get; }

    internal ReshapeGeometry Reshape { get; }

    internal WrapRowSource? Wrap { get; }

    /// <summary>The inverse region mapping when the step can express one; null means the whole source.</summary>
    internal MapGeometryRegions? MapRegions { get; }

    /// <summary>True when a stream backend can run this step without materialising.</summary>
    internal bool CanWrap => Wrap is not null;
}

/// <summary>
/// A geometry step as the builder receives it: the execution forms without the position, which the builder assigns.
/// </summary>
internal delegate SourceRegionSet MapGeometryRegions(PixelRect destination, int sourceWidth, int sourceHeight,
    int destWidth, int destHeight);

internal sealed record GeometryDescriptor(string Name, GeometryRule Geometry, ReshapeGeometry Reshape,
    WrapRowSource? Wrap = null, GeometryKind Kind = GeometryKind.Other, Resampler? Resampler = null,
    MapGeometryRegions? MapRegions = null);

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

    internal PipelineRecipeBuilder Append(ImageOp op, byte[]? lut = null)
    {
        ArgumentNullException.ThrowIfNull(op);
        _steps.Add(new OperatorStep(_steps.Count, op, lut));
        _version++;
        return this;
    }

    internal PipelineRecipeBuilder Append(GeometryDescriptor geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        _steps.Add(new GeometryStep(_steps.Count, geometry.Name, geometry.Geometry, geometry.Kind,
            geometry.Resampler, geometry.Reshape, geometry.Wrap, geometry.MapRegions));
        _version++;
        return this;
    }

    internal PipelineRecipe Build() => new(_steps, _version);
}
