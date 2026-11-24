using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>
/// An operator together with what the executor needs to know about it: how far it reads, and whether it can be
/// banded at all.
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
/// it from the parameters it is given, and there is no public way to build a step by hand.
/// </para>
/// </remarks>
public sealed class ImageOp
{
    internal ImageOp(string name, OpCategory category, int haloX, int haloY, OpKernel kernel,
        Func<int, int, int, WorkingFormat, long>? scratch = null,
        Func<PixelView, WorkingBuffer>? direct = null,
        IntoKernel? into = null)
    {
        Name = name;
        Category = category;
        HaloX = haloX;
        HaloY = haloY;
        Kernel = kernel;
        Scratch = scratch;
        Direct = direct;
        Into = into;
    }

    /// <summary>
    /// The operator as "write these rows into that destination", when it has that form.
    /// </summary>
    /// <remarks>
    /// Without it the band adapter has to let the operator rent a window-sized buffer and then copy the rows it owns
    /// out of it — one mid-sized allocation and one full-image copy per band per step.
    /// </remarks>
    internal IntoKernel? Into { get; }

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
    internal Func<PixelView, WorkingBuffer>? Direct { get; }

    /// <summary>
    /// The operator's own working set for a view of <c>width × rows</c>, or <c>null</c> when it has not declared
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>null</c> is not zero. A budget that treats an undeclared operator as free reports success while the
    /// process runs past it, so <c>ExecuteOptions.MaxWorkingBytes</c> refuses a plan that contains one unless the
    /// caller sets <c>AllowUnestimatedScratch</c>.
    /// </para>
    /// <para>
    /// Only operators whose footprint is arithmetic have one: a per-worker band of float planes, a fixed ring of
    /// rows, a pair of single-channel planes. Where the number depends on internals that would have to be
    /// re-derived by hand every time the operator changes, there is deliberately no declaration.
    /// </para>
    /// </remarks>
    internal Func<int, int, int, WorkingFormat, long>? Scratch { get; }

    /// <summary>Diagnostic name, used in plan reports.</summary>
    public string Name { get; }

    /// <summary>Whether each output pixel depends on one input pixel, a rectangle, or the whole image.</summary>
    public OpCategory Category { get; }

    /// <summary>Columns read on either side of the destination rectangle.</summary>
    public int HaloX { get; }

    /// <summary>Rows read above and below the destination rectangle.</summary>
    public int HaloY { get; }

    /// <summary>The operator's kernel reads the whole source image, not just its window: Mirror and Wrap borders
    /// reflect or wrap about the image edge, so a band-only source cannot serve it.</summary>
    internal bool NeedsWholeSource { get; init; }

    internal OpKernel Kernel { get; }
}
