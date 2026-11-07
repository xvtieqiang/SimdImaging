namespace SimdImaging.Abstractions;

/// <summary>A point in pixel coordinates, for polygon paths. Separate from <c>PixelRect</c> so a path is a
/// span of points rather than a rectangle with a degenerate meaning.</summary>
public readonly record struct PixelPoint(int X, int Y);
