using System.Numerics;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// A 2-D affine transform in row form: <c>x' = A·x + B·y + C</c>, <c>y' = D·x + E·y + F</c>. Matrices compose
/// right-to-left, like every other graphics API: <c>Translate.Multiply(Rotate)</c> rotates first.
/// </summary>
public readonly record struct AffineMatrix(float A, float B, float C, float D, float E, float F)
{
    /// <summary>The identity transform.</summary>
    public static AffineMatrix Identity => new(1f, 0f, 0f, 0f, 1f, 0f);

    /// <summary>Translation by (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static AffineMatrix Translation(float x, float y) => new(1f, 0f, x, 0f, 1f, y);

    /// <summary>Rotation about the origin, clockwise for positive angles in a y-down coordinate system.</summary>
    public static AffineMatrix Rotation(float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new AffineMatrix(cos, -sin, 0f, sin, cos, 0f);
    }

    /// <summary>Scale about the origin.</summary>
    public static AffineMatrix Scale(float x, float y) => new(x, 0f, 0f, 0f, y, 0f);

    /// <summary><c>this × other</c>: <paramref name="other"/> is applied first.</summary>
    public AffineMatrix Multiply(AffineMatrix other) => new(
        A * other.A + B * other.D,
        A * other.B + B * other.E,
        A * other.C + B * other.F + C,
        D * other.A + E * other.D,
        D * other.B + E * other.E,
        D * other.C + E * other.F + F);

    /// <summary>Applies the transform to a point.</summary>
    public (float X, float Y) Apply(float x, float y) => (A * x + B * y + C, D * x + E * y + F);

    /// <summary>Computes the inverse, or returns false when the matrix is singular.</summary>
    public bool TryInvert(out AffineMatrix inverse)
    {
        var determinant = A * E - B * D;
        if (MathF.Abs(determinant) < 1e-12f)
        {
            inverse = default;
            return false;
        }

        var scale = 1f / determinant;
        var ia = E * scale;
        var ib = -B * scale;
        var id = -D * scale;
        var ie = A * scale;
        inverse = new AffineMatrix(ia, ib, -(ia * C + ib * F), id, ie, -(id * C + ie * F));
        return true;
    }
}
