using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Contracts;

/// <summary>
/// Everything a step promises about itself: its ports, its geometry, what it reads, how it may be divided, how many
/// passes it needs, what it allocates, and which pixel rules an optimisation may not change.
/// <para>
/// This is metadata only. It holds no buffers, no execution state and no delegate; the kernels live in
/// <see cref="KernelImplementations"/> and the two are paired by <see cref="ImageOp"/>. The split matters because a
/// contract must be cheap to copy and safe to cache, while a kernel is a closure over parameters.
/// </para>
/// <para>
/// Created only inside this assembly and only through <see cref="Create"/>, which rejects the combinations that are
/// known to be lies (a point step with a halo, a whole-image step that claims row partitioning). There is no public
/// constructor: a caller who could assemble a contract could assemble a wrong one, and a wrong halo does not throw —
/// it produces a wrong pixel at a band boundary.
/// </para>
/// </summary>
internal sealed class OperatorContract
{
    private OperatorContract(
        string name,
        OpCategory category,
        WorkingFormatMask formats,
        IReadOnlyList<InputPort> inputs,
        OutputPort output,
        GeometryRule geometry,
        IInputFootprint footprint,
        int haloX,
        int haloY,
        ExecutionRequirements requirements,
        PassModel pass,
        WorkspaceModel workspace,
        PixelSemantics semantics)
    {
        Name = name;
        Category = category;
        Formats = formats;
        Inputs = inputs;
        Output = output;
        Geometry = geometry;
        Footprint = footprint;
        HaloX = haloX;
        HaloY = haloY;
        Requirements = requirements;
        Pass = pass;
        Workspace = workspace;
        Semantics = semantics;
    }

    internal string Name { get; }

    internal OpCategory Category { get; }

    internal WorkingFormatMask Formats { get; }

    internal IReadOnlyList<InputPort> Inputs { get; }

    internal OutputPort Output { get; }

    internal GeometryRule Geometry { get; }

    internal IInputFootprint Footprint { get; }

    /// <summary>Columns a band adapter must widen its window by. Usually the footprint's radius; a whole-source
    /// operator such as the general convolution still needs its radius for the window it does read.</summary>
    internal int HaloX { get; }

    /// <summary>Rows a band adapter must widen its window by.</summary>
    internal int HaloY { get; }

    internal ExecutionRequirements Requirements { get; }

    internal PassModel Pass { get; }

    internal WorkspaceModel Workspace { get; }

    internal PixelSemantics Semantics { get; }

    /// <summary>Builds a contract and rejects the combinations that cannot be true.</summary>
    /// <exception cref="ArgumentException">A field is empty or a combination is contradictory.</exception>
    internal static OperatorContract Create(
        string name,
        OpCategory category,
        WorkingFormatMask formats,
        IInputFootprint footprint,
        WorkspaceModel workspace,
        ExecutionRequirements? requirements = null,
        PassModel pass = PassModel.Single,
        PixelSemantics? semantics = null,
        InputPort? input = null,
        InputPort? input2 = null,
        WorkingFormat? outputFormat = null,
        OutputPort? output = null,
        GeometryRule? geometry = null,
        int? haloX = null,
        int? haloY = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(footprint);
        ArgumentNullException.ThrowIfNull(workspace);

        var resolvedHaloX = haloX ?? footprint.HaloX;
        var resolvedHaloY = haloY ?? footprint.HaloY;
        ArgumentOutOfRangeException.ThrowIfNegative(resolvedHaloX);
        ArgumentOutOfRangeException.ThrowIfNegative(resolvedHaloY);

        if (footprint.Kind == InputFootprintKind.Identity && (resolvedHaloX != 0 || resolvedHaloY != 0))
            throw new ArgumentException(
                $"The operator '{name}' declares an identity footprint with a {resolvedHaloX}x{resolvedHaloY} halo; " +
                "an identity read cannot be wider than the destination.", nameof(haloX));

        if (formats == WorkingFormatMask.None)
            throw new ArgumentException(
                $"The operator '{name}' declares no supported format; None means 'cannot run', which no plan step is.",
                nameof(formats));

        if (category == OpCategory.Geometric)
            throw new ArgumentException(
                "A Geometric step changes the image size, which a same-size plan cannot represent; geometry belongs to " +
                "the recipe, not to an operator step.", nameof(category));

        if (category == OpCategory.Point && footprint.Kind != InputFootprintKind.Identity)
            throw new ArgumentException(
                $"The point operator '{name}' reads a {footprint.Describe} footprint; a point step reads one pixel.",
                nameof(footprint));

        if (input2 is not null && category != OpCategory.Composite)
            throw new ArgumentException(
                $"The '{name}' operator declares a second input but is {category}; only a Composite step reads two.",
                nameof(input2));

        var resolvedRequirements = requirements ?? (category is OpCategory.Global or OpCategory.Composite
            ? ExecutionRequirements.WholeImage
            : ExecutionRequirements.RowBands);

        if (category is OpCategory.Global or OpCategory.Composite &&
            resolvedRequirements.Partition != Partitioning.WholeImage)
            throw new ArgumentException(
                $"The whole-image operator '{name}' declares {resolvedRequirements.Partition} partitioning.",
                nameof(requirements));

        if (category is OpCategory.Point or OpCategory.Neighborhood or OpCategory.Separable &&
            resolvedRequirements.Partition == Partitioning.WholeImage && footprint.Kind == InputFootprintKind.Halo)
            throw new ArgumentException(
                $"The banded operator '{name}' declares whole-image partitioning with a halo footprint.",
                nameof(requirements));

        if (footprint.IsBlockAligned && resolvedRequirements.Partition != Partitioning.WholeImage)
            throw new ArgumentException(
                $"The block-aligned operator '{name}' declares {resolvedRequirements.Partition} partitioning; a block " +
                "crosses band boundaries, so no constant window is correct until a block-aware executor exists.",
                nameof(requirements));

        var primary = input ?? new InputPort("source", PortRole.Source, formats, Required: true);
        var inputs = input2 is { } overlay ? new[] { primary, overlay } : new[] { primary };

        return new OperatorContract(
            name,
            category,
            formats,
            inputs,
            output ?? new OutputPort("output", SameSize: true, SameFormat: outputFormat is null, outputFormat),
            geometry ?? GeometryRule.SameSize,
            footprint,
            resolvedHaloX,
            resolvedHaloY,
            resolvedRequirements,
            pass,
            workspace,
            semantics ?? PixelSemantics.EncodedFloat);
    }
}
