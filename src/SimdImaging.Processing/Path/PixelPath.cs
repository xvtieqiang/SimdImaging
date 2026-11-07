namespace SimdImaging.Processing;

/// <summary>What a path command does. The set is deliberately small: everything else (arcs, rounded
/// rectangles) is expressed with cubics, which is what the flattener understands.</summary>
public enum PathVerb : byte
{
    /// <summary>Starts a new subpath at <see cref="PathCommand.End"/>.</summary>
    MoveTo,

    /// <summary>Straight segment to <see cref="PathCommand.End"/>.</summary>
    LineTo,

    /// <summary>Cubic Bézier to <see cref="PathCommand.End"/> with two control points.</summary>
    CubicTo,

    /// <summary>Quadratic Bézier to <see cref="PathCommand.End"/> with one control point.</summary>
    QuadraticTo,

    /// <summary>Closes the current subpath back to its start.</summary>
    Close,
}

/// <summary>A point in continuous path space. Separate from <see cref="SimdImaging.Abstractions.PixelPoint"/>
/// because geometry happens before any pixel snapping: rounding early would make flattened curves depend on
/// the canvas size.</summary>
public readonly record struct PathPoint(float X, float Y)
{
    /// <summary>Vector addition.</summary>
    public static PathPoint operator +(PathPoint a, PathPoint b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>Vector subtraction.</summary>
    public static PathPoint operator -(PathPoint a, PathPoint b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Scales both coordinates.</summary>
    public static PathPoint operator *(PathPoint a, float scale) => new(a.X * scale, a.Y * scale);
}

/// <summary>One command of a <see cref="PixelPath"/>. Control points are ignored by the verbs that do not
/// use them, so the struct stays fixed-size and a path is one flat array.</summary>
public readonly record struct PathCommand
{
    /// <summary>Creates a command.</summary>
    /// <param name="verb">What to draw.</param>
    /// <param name="end">The destination point (or the subpath start for <see cref="PathVerb.MoveTo"/>).</param>
    /// <param name="control1">First control point, for the Bézier verbs.</param>
    /// <param name="control2">Second control point, for <see cref="PathVerb.CubicTo"/>.</param>
    public PathCommand(PathVerb verb, PathPoint end, PathPoint control1 = default, PathPoint control2 = default)
    {
        Verb = verb;
        End = end;
        Control1 = control1;
        Control2 = control2;
    }

    /// <summary>What to draw.</summary>
    public PathVerb Verb { get; }

    /// <summary>The destination point.</summary>
    public PathPoint End { get; }

    /// <summary>First control point (cubic and quadratic).</summary>
    public PathPoint Control1 { get; }

    /// <summary>Second control point (cubic only).</summary>
    public PathPoint Control2 { get; }
}

/// <summary>Which points are inside a filled path.</summary>
public enum PathFillRule : byte
{
    /// <summary>Inside when the signed winding number is non-zero: overlapping contours of the same direction
    /// stay filled, opposite directions cancel.</summary>
    NonZero,

    /// <summary>Inside when the crossing count is odd: any two contours punch a hole in each other.</summary>
    EvenOdd,
}

/// <summary>
/// An immutable sequence of move / line / Bézier / close commands — geometry, not pixels. Built through
/// <see cref="PixelPathBuilder"/>; filling and stroking live in <see cref="PathOps"/>.
/// </summary>
public sealed class PixelPath
{
    private static readonly PathCommand[] None = [];
    private readonly PathCommand[] _commands;

    internal PixelPath(PathCommand[] commands) => _commands = commands;

    /// <summary>An empty path: fills and strokes nothing.</summary>
    public static PixelPath Empty { get; } = new(None);

    /// <summary>The commands, in order.</summary>
    public ReadOnlySpan<PathCommand> Commands => _commands;

    /// <summary>True when the path holds no command.</summary>
    public bool IsEmpty => Commands.Length == 0;
}
