using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Contracts;

namespace SimdImaging.Processing;

/// <summary>
/// An operator: its contract (what it promises about ports, footprint, partitioning, passes, workspace and pixel
/// semantics) and the kernels that implement it.
/// </summary>
/// <remarks>
/// <para>
/// The support radius is a property of the operator, not a fact the caller should have to supply. Before this
/// type, a plan step was <c>Add(name, category, kernel, haloX, haloY)</c> and getting the halo wrong did not
/// throw — it produced a wrong pixel at a band boundary. Two such mistakes happened in one evening:
/// <c>Sharpen</c> blurs internally and needs <c>ceil(3σ)</c> rows rather than the one a 3x3 convolution needs,
/// and <c>Pixelate</c> is block-aligned, so no constant halo expresses it at all.
/// </para>
/// <para>
/// Carrying the number on the operator makes both impossible to express wrongly: <see cref="PlanOps"/> derives
/// it from the parameters it is given, and there is no public way to build a step by hand. The contract is the
/// single definition — <c>PlanStep</c> reads it, the streaming classifier reads it, and the workspace estimator
/// reads it, instead of each keeping its own copy of the same five fields.
/// </para>
/// </remarks>
public sealed class ImageOp
{
    internal ImageOp(OperatorContract contract, KernelImplementations kernels)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        Kernels = kernels ?? throw new ArgumentNullException(nameof(kernels));

        // A format-changing step is executed as a whole-image reshape; a plan kernel writes into a destination of
        // the source format and cannot change it. Refuse the combination at construction instead of at a boundary.
        if (contract.Output.Format is not null && kernels.WholeImage is null && kernels.WholeImageSecond is null)
            throw new ArgumentException(
                $"The operator '{contract.Name}' changes the working format to {contract.Output.Format} but has no " +
                "whole-image kernel.", nameof(kernels));

        // A second-input kernel needs a declared port to describe what it reads; the region derivation and the plan
        // explanation both read the port, so an undeclared second input would be an unexplained read.
        if ((kernels.SecondKernel is not null || kernels.WholeImageSecond is not null) && contract.Inputs.Count < 2)
            throw new ArgumentException(
                $"The operator '{contract.Name}' has a second-input kernel but declares one input port.", nameof(kernels));
    }

    internal OperatorContract Contract { get; }

    internal KernelImplementations Kernels { get; }

    /// <summary>Diagnostic name, used in plan reports.</summary>
    public string Name => Contract.Name;

    /// <summary>Whether each output pixel depends on one input pixel, a rectangle, or the whole image.</summary>
    public OpCategory Category => Contract.Category;

    /// <summary>Columns read on either side of the destination rectangle.</summary>
    public int HaloX => Contract.HaloX;

    /// <summary>Rows read above and below the destination rectangle.</summary>
    public int HaloY => Contract.HaloY;

    /// <summary>
    /// The operator as "write these rows into that destination", when it has that form.
    /// </summary>
    /// <remarks>
    /// Without it the band adapter has to let the operator rent a window-sized buffer and then copy the rows it owns
    /// out of it — one mid-sized allocation and one full-image copy per band per step.
    /// </remarks>
    internal IntoKernel? Into => Kernels.Into;

    /// <summary>
    /// The operator as one whole-image call, when it has one.
    /// </summary>
    /// <remarks>
    /// A plan of one step has nothing to fuse and nothing to band: the operator already parallelises internally,
    /// and building a plan around it costs a rent, a band split and a kernel dispatch per band for no gain.
    /// Measured at 1920×1080 that was 10.7% on a single invert — the difference between `image.Invert()` and
    /// `image.Pipe().Invert().ToImage()` when both were still on the surface. This delegate is what lets the one
    /// path left behave like the direct call it replaced.
    /// </remarks>
    internal Func<PixelView, WorkingBuffer>? Direct => Kernels.WholeImage;

    /// <summary>
    /// The operator's own working set for a view of <c>width × rows</c>, or <c>null</c> when it has not declared
    /// one.
    /// </summary>
    /// <remarks>
    /// <c>null</c> is not zero. A budget that treats an undeclared operator as free reports success while the
    /// process runs past it, so <c>ExecuteOptions.MaxWorkingBytes</c> refuses a plan that contains one unless the
    /// caller sets <c>AllowUnestimatedScratch</c>.
    /// </remarks>
    internal Func<int, int, int, WorkingFormat, long>? Scratch => Contract.Workspace.Estimate;

    /// <summary>The operator's kernel reads the whole source image, not just its window.</summary>
    internal bool NeedsWholeSource => Contract.Footprint.CoversWholeImage;

    internal OpKernel Kernel => Kernels.PlanKernel;

    /// <summary>
    /// A point step defined by a 4×256 byte table. It has no whole-image form: the table path already writes the
    /// destination directly, which is why the single-step fast path would add a rent and nothing else.
    /// </summary>
    internal static ImageOp PointLut(string name, byte[] table) =>
        new(OperatorContract.Create(name, OpCategory.Point, WorkingFormatMask.Rgba8,
                IdentityFootprint.Instance, WorkspaceModel.None, semantics: PixelSemantics.EncodedInteger),
            new KernelImplementations(ProcessingPlan.BuildLutKernel(table)));

    /// <summary>
    /// Builds an ad-hoc operator for tests and for the internal <c>Add(name, category, kernel, …)</c> overload.
    /// The declaration is deliberately conservative: an unknown working set is <see cref="WorkspaceModel.Undeclared"/>,
    /// never zero, and a whole-image category gets a whole-source footprint.
    /// </summary>
    internal static ImageOp AdHoc(string name, OpCategory category, int haloX, int haloY, OpKernel kernel,
        Func<int, int, int, WorkingFormat, long>? scratch, bool needsWholeSource)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentOutOfRangeException.ThrowIfNegative(haloX);
        ArgumentOutOfRangeException.ThrowIfNegative(haloY);
        var whole = needsWholeSource || category is OpCategory.Global or OpCategory.Composite;
        var footprint = whole
            ? (IInputFootprint)WholeSourceFootprint.Instance
            : haloX == 0 && haloY == 0
                ? IdentityFootprint.Instance
                : new HaloFootprint(haloX, haloY);

        var contract = OperatorContract.Create(
            name,
            category,
            WorkingFormatMask.All,
            footprint,
            scratch is null ? WorkspaceModel.Undeclared : WorkspaceModel.Of(scratch),
            haloX: haloX,
            haloY: haloY);

        return new ImageOp(contract, new KernelImplementations(kernel));
    }
}
