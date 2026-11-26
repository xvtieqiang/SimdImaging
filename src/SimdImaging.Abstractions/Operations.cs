namespace SimdImaging.Abstractions;

/// <summary>What an operator needs from its surroundings: this is what lets the executor decide whether it can
/// process a row band or a tile, and how much of the source each destination tile reads.</summary>
public enum OpCategory : byte
{
    /// <summary>Each output pixel depends on the same input pixel only.</summary>
    Point,

    /// <summary>Each output pixel reads a rectangle of <c>HaloX</c> × <c>HaloY</c>
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
