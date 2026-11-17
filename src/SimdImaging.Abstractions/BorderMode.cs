namespace SimdImaging.Abstractions;

/// <summary>How a kernel reads outside the image.</summary>
public enum BorderMode
{
    /// <summary>Repeat the edge pixel. The default, and what the 3×3 fast path has always done.</summary>
    Clamp = 0,

    /// <summary>Reflect about the edge pixel: <c>x[-1]</c> is <c>x[1]</c>.</summary>
    Mirror = 1,

    /// <summary>Wrap around to the opposite edge.</summary>
    Wrap = 2,

    /// <summary>Read as zero, or as the filter's own constant when it takes one.</summary>
    /// <remarks>The constant lives on the operator's options, which is one layer up: naming the type here would make
    /// Abstractions depend on Processing, which the dependency direction forbids.</remarks>
    Constant = 3,
}
