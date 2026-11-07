namespace SimdImaging.Processing;

/// <summary>How two segments of a stroked path meet on the outside of a corner.</summary>
public enum LineJoin : byte
{
    /// <summary>Extends the two edges until they meet; falls back to <see cref="Bevel"/> past the miter limit.</summary>
    Miter,

    /// <summary>A circular arc.</summary>
    Round,

    /// <summary>A straight cut across the corner.</summary>
    Bevel,
}

/// <summary>How an open stroked path ends.</summary>
public enum LineCap : byte
{
    /// <summary>Ends exactly at the endpoint.</summary>
    Butt,

    /// <summary>A semicircle of the stroke radius.</summary>
    Round,

    /// <summary>A square extension of half the width.</summary>
    Square,
}

/// <summary>
/// Stroke parameters. Dash lengths and the offset are in pixels along the path, not multiples of the width.
/// </summary>
public readonly struct StrokeOptions
{
    /// <summary>Creates the options.</summary>
    /// <param name="width">Stroke width in pixels; must be positive.</param>
    /// <param name="join">Corner style.</param>
    /// <param name="cap">End style for open subpaths.</param>
    /// <param name="miterLimit">Longest miter, as a multiple of the width; below 1 there is no miter.</param>
    /// <param name="dashPattern">Alternating on/off lengths; empty for a solid stroke. An odd count is repeated
    /// once, so <c>[4, 2, 1]</c> behaves as <c>[4, 2, 1, 4, 2, 1]</c>.</param>
    /// <param name="dashOffset">Distance into the pattern at which the stroke starts.</param>
    public StrokeOptions(float width = 1f, LineJoin join = LineJoin.Miter, LineCap cap = LineCap.Butt,
        float miterLimit = 4f, ReadOnlyMemory<float> dashPattern = default, float dashOffset = 0f)
    {
        Width = width;
        Join = join;
        Cap = cap;
        MiterLimit = miterLimit;
        DashPattern = dashPattern;
        DashOffset = dashOffset;
    }

    /// <summary>Stroke width in pixels.</summary>
    public float Width { get; }

    /// <summary>Corner style.</summary>
    public LineJoin Join { get; }

    /// <summary>End style.</summary>
    public LineCap Cap { get; }

    /// <summary>Miter limit as a multiple of <see cref="Width"/>.</summary>
    public float MiterLimit { get; }

    /// <summary>Alternating on/off lengths in pixels; empty means solid.</summary>
    public ReadOnlyMemory<float> DashPattern { get; }

    /// <summary>Distance into the pattern at which the stroke starts.</summary>
    public float DashOffset { get; }
}
