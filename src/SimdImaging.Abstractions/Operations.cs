using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Abstractions;

/// <summary>What an operator needs from its surroundings: this is what lets the executor decide whether it can
/// process a row band or a tile, and how much of the source each destination tile reads.</summary>
public enum OpCategory : byte
{
    /// <summary>Each output pixel depends on the same input pixel only.</summary>
    Point,

    /// <summary>Each output pixel reads a rectangle of <see cref="IImageOp.HaloX"/> × <see cref="IImageOp.HaloY"/>
    /// around it (convolution, median, morphology).</summary>
    Neighborhood,

    /// <summary>Two 1-D passes: horizontal then vertical (Gaussian, separable resize).</summary>
    Separable,

    /// <summary>The source coordinate is a function of the destination coordinate (rotate, affine, warp).</summary>
    Geometric,

    /// <summary>Needs a whole-image statistic before writing a pixel (histogram, quantize, CLAHE).</summary>
    Global,

    /// <summary>Two inputs combined into one output (composition, blending).</summary>
    Composite,
}

/// <summary>Which working formats an operator accepts. Most accept all three; a few are integer-only or
/// float-only, and declaring that lets the planner refuse loudly instead of silently degrading.</summary>
[Flags]
public enum WorkingFormatMask : byte
{
    /// <summary>No format (an unset value).</summary>
    None = 0,

    /// <summary><see cref="WorkingFormat.Rgba8"/>.</summary>
    Rgba8 = 1,

    /// <summary><see cref="WorkingFormat.Rgba16"/>.</summary>
    Rgba16 = 2,

    /// <summary><see cref="WorkingFormat.Rgba32F"/>.</summary>
    Rgba32F = 4,

    /// <summary>All three.</summary>
    All = Rgba8 | Rgba16 | Rgba32F,
}

/// <summary>
/// One image operation. Implementations are immutable descriptors: they hold parameters, never buffers, and
/// expose enough geometry for a planner to fuse, tile and parallelize them without knowing what they do.
/// </summary>
public interface IImageOp
{
    /// <summary>A stable name, used in <c>PlanReport</c> and test names (<c>Resize</c>, <c>GaussianBlur</c>).</summary>
    string Name { get; }

    /// <summary>What the executor may assume about the operator's data flow.</summary>
    OpCategory Category { get; }

    /// <summary>The working formats this operator implements. Asking for another must throw
    /// <see cref="NotSupportedException"/> at plan time, not fall back to another depth.</summary>
    WorkingFormatMask Formats { get; }

    /// <summary>Columns of source read on each side of a destination pixel; 0 for <see cref="OpCategory.Point"/>.</summary>
    int HaloX { get; }

    /// <summary>Rows of source read on each side of a destination pixel; 0 for <see cref="OpCategory.Point"/>.</summary>
    int HaloY { get; }

    /// <summary>
    /// The source rectangle a destination rectangle reads. Must include the halo, so a tiled executor can read
    /// exactly one region per output tile. Returns <paramref name="destination"/> for point-wise operators.
    /// </summary>
    PixelRect MapSourceRect(PixelRect destination, int sourceWidth, int sourceHeight);
}
