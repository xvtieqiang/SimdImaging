using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Contracts;

/// <summary>The smallest unit the executor may divide the work into.</summary>
internal enum Partitioning : byte
{
    /// <summary>The whole image is one unit; banding would change the answer or is not implemented.</summary>
    WholeImage = 0,

    /// <summary>Output rows are independent once their halo is available.</summary>
    RowBands = 1,

    /// <summary>Two-dimensional tiles are independent. Declared by no operator yet; the Tile backend refuses it.</summary>
    Tiles = 2,
}

/// <summary>How far a step's state reaches.</summary>
internal enum StateScope : byte
{
    /// <summary>No state beyond the current pixel and its window.</summary>
    None = 0,

    /// <summary>Per-worker buffers only, e.g. a band of float planes.</summary>
    PerWorker = 1,

    /// <summary>State gathered from the whole image before it is written, e.g. a histogram.</summary>
    PerImage = 2,

    /// <summary>State advanced by the sequence of calls, e.g. a seeded noise stream.</summary>
    PerCall = 3,
}

/// <summary>Whether output rows may be produced out of order.</summary>
internal enum ExecutionOrder : byte
{
    Any = 0,

    /// <summary>The step consumes its input in source order; reordering changes the result.</summary>
    SourceOrder = 1,
}

/// <summary>What the step requires of concurrent callers.</summary>
internal enum ThreadSafety : byte
{
    /// <summary>Concurrent calls with separate destinations are safe.</summary>
    Concurrent = 0,

    /// <summary>The step must run alone.</summary>
    SingleThread = 1,
}

/// <summary>What the step promises about identical inputs.</summary>
internal enum Determinism : byte
{
    /// <summary>An output pixel is a function of its coordinates and the input; order does not matter.</summary>
    CoordinateDeterministic = 0,

    /// <summary>The result follows from a seeded sequence consumed in a fixed order.</summary>
    SeededSequence = 1,

    /// <summary>The result depends on an order the plan does not fix.</summary>
    OrderDependent = 2,
}

/// <summary>How many passes over the input a step needs.</summary>
internal enum PassModel : byte
{
    /// <summary>One read and one write.</summary>
    Single = 0,

    /// <summary>Two 1-D passes (rows then columns) whose combined result is the answer.</summary>
    SeparableRows = 1,

    /// <summary>A whole-image statistic is gathered before any output pixel is written.</summary>
    GatherThenApply = 2,
}

/// <summary>The colour domain an operator computes in.</summary>
internal enum PixelDomain : byte
{
    /// <summary>Encoded sample values, as stored (sRGB-encoded for the current formats).</summary>
    Encoded = 0,

    /// <summary>Linear light.</summary>
    Linear = 1,

    /// <summary>Unbounded values (HDR), not clamped to the storage range between steps.</summary>
    Unbounded = 2,
}

/// <summary>What an operator does with the alpha channel.</summary>
internal enum AlphaPolicy : byte
{
    /// <summary>Alpha passes through untouched.</summary>
    Preserve = 0,

    /// <summary>Samples are premultiplied before the computation and unpremultiplied after.</summary>
    Premultiplied = 1,

    /// <summary>Alpha is computed like any other channel, e.g. a median.</summary>
    Recomputes = 2,
}

/// <summary>Where rounding happens.</summary>
internal enum Precision : byte
{
    /// <summary>Integer arithmetic; the result is exact for the declared formats.</summary>
    IntegerExact = 0,

    /// <summary>Float work with a single rounding at the end; the order is frozen by the SIMD tiers.</summary>
    SingleRoundFloat = 1,
}

/// <summary>The orthogonal properties that decide which backends can run a step. Separate from
/// <see cref="OpCategory"/>, which only says how far one pixel reads.</summary>
internal readonly record struct ExecutionRequirements(
    Partitioning Partition,
    StateScope State,
    ExecutionOrder Order,
    ThreadSafety Threading,
    Determinism Determinism)
{
    /// <summary>The common case: independent rows, no state, order-free, deterministic by coordinates.</summary>
    internal static ExecutionRequirements RowBands { get; } = new(
        Partitioning.RowBands, StateScope.None, ExecutionOrder.Any, ThreadSafety.Concurrent,
        Determinism.CoordinateDeterministic);

    /// <summary>One whole-image unit, no state.</summary>
    internal static ExecutionRequirements WholeImage { get; } = RowBands with { Partition = Partitioning.WholeImage };
}

/// <summary>The colour and alpha rules an optimisation must not change.</summary>
internal readonly record struct PixelSemantics(PixelDomain Domain, AlphaPolicy Alpha, Precision Precision)
{
    /// <summary>Encoded samples, alpha preserved, one rounding after float work — the default.</summary>
    internal static PixelSemantics EncodedFloat { get; } = new(
        PixelDomain.Encoded, AlphaPolicy.Preserve, Precision.SingleRoundFloat);

    /// <summary>Encoded samples, alpha preserved, exact integer arithmetic (byte tables).</summary>
    internal static PixelSemantics EncodedInteger { get; } = new(
        PixelDomain.Encoded, AlphaPolicy.Preserve, Precision.IntegerExact);

    /// <summary>Encoded samples where alpha is filtered like the colour channels.</summary>
    internal static PixelSemantics EncodedFloatAlphaComputed { get; } = EncodedFloat with
    {
        Alpha = AlphaPolicy.Recomputes,
    };
}

/// <summary>The role of one input.</summary>
internal enum PortRole : byte
{
    /// <summary>The image being transformed.</summary>
    Source = 0,

    /// <summary>A second image composited over the source.</summary>
    Overlay = 1,

    /// <summary>A guide image (guided filter, joint bilateral).</summary>
    Guide = 2,

    /// <summary>A mask that selects where the step applies.</summary>
    Mask = 3,
}

/// <summary>One input a step reads.</summary>
internal readonly record struct InputPort(string Name, PortRole Role, WorkingFormatMask Formats, bool Required);

/// <summary>What a step produces. Every planned step today produces one same-size image of the source format.</summary>
internal readonly record struct OutputPort(string Name, bool SameSize, bool SameFormat);

/// <summary>How a step's output relates to its input geometry.</summary>
internal abstract record GeometryRule
{
    /// <summary>No size or coordinate change.</summary>
    internal static GeometryRule SameSize { get; } = new SameSizeRule();

    /// <summary>False when the output size depends on pixels (trim, entropy crop, seam carve).</summary>
    internal abstract bool SizeKnown { get; }

    /// <summary>The output size for a given input size.</summary>
    internal abstract (int Width, int Height) Map(int width, int height);

    /// <summary>One line for plan explanations.</summary>
    internal abstract string Describe { get; }
}

internal sealed record SameSizeRule : GeometryRule
{
    internal override bool SizeKnown => true;

    internal override (int Width, int Height) Map(int width, int height) => (width, height);

    internal override string Describe => "same size";
}

/// <summary>A fixed output size.</summary>
internal sealed record FixedSizeRule(int Width, int Height) : GeometryRule
{
    internal override bool SizeKnown => true;

    internal override (int Width, int Height) Map(int width, int height) => (Width, Height);

    internal override string Describe => $"{Width}x{Height}";
}

/// <summary>A size that depends on the input size, arithmetically (aspect-preserving resize, padding).</summary>
internal sealed record RelativeRule(Func<int, int, (int Width, int Height)> MapSize, string Text) : GeometryRule
{
    internal override bool SizeKnown => true;

    internal override (int Width, int Height) Map(int width, int height) => MapSize(width, height);

    internal override string Describe => Text;
}

/// <summary>The axes swap: a quarter turn or a transpose.</summary>
internal sealed record SwappedSizeRule : GeometryRule
{
    internal override bool SizeKnown => true;

    internal override (int Width, int Height) Map(int width, int height) => (height, width);

    internal override string Describe => "swapped axes";
}

/// <summary>A size derived from the content (trim, entropy crop, seam carve). The compiler reports the size as
/// unknown and refuses the backends that need it up front.</summary>
internal sealed record ContentDerivedRule : GeometryRule
{
    internal override bool SizeKnown => false;

    internal override (int Width, int Height) Map(int width, int height) => (width, height);

    internal override string Describe => "content-derived size";
}
