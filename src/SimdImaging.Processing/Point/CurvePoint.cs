namespace SimdImaging.Processing;

/// <summary>A control point for <c>Curves</c>: input level and output level, both 0..1.</summary>
public readonly record struct CurvePoint(float X, float Y);
