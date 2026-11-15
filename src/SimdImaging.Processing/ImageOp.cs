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
/// Carrying the number on the operator makes both impossible to express wrongly: <see cref="ImageOps"/> derives
/// it from the parameters it is given, and there is no public way to build a step by hand.
/// </para>
/// </remarks>
public sealed class ImageOp
{
    internal ImageOp(string name, OpCategory category, int haloX, int haloY, OpKernel kernel)
    {
        Name = name;
        Category = category;
        HaloX = haloX;
        HaloY = haloY;
        Kernel = kernel;
    }

    /// <summary>Diagnostic name, used in plan reports.</summary>
    public string Name { get; }

    /// <summary>Whether each output pixel depends on one input pixel, a rectangle, or the whole image.</summary>
    public OpCategory Category { get; }

    /// <summary>Columns read on either side of the destination rectangle.</summary>
    public int HaloX { get; }

    /// <summary>Rows read above and below the destination rectangle.</summary>
    public int HaloY { get; }

    internal OpKernel Kernel { get; }
}
