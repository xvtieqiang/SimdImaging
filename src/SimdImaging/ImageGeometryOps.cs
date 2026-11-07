using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>Arbitrary-angle geometry. Unlike the lossless 90° rotations these resample, so they take a
/// background colour for the corners the source does not cover.</summary>
public sealed partial class Image
{
    /// <summary>Rotates by any angle, expanding the canvas to fit the rotated bounds.</summary>
    /// <param name="degrees">Clockwise positive, in a y-down coordinate system.</param>
    /// <param name="background">Colour for the corners outside the rotated source.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public Image Rotate(float degrees, RgbaColor background = default, IPixelPool? pool = null)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Abs(MathF.Cos(radians));
        var sin = MathF.Abs(MathF.Sin(radians));

        // Ceiling with an epsilon: MathF.Cos(90°) is -4.4e-8, so the exact-fit case computes 5.0000004 and
        // would grow the canvas by one pixel for a rotation that needs none.
        const float Epsilon = 1e-4f;
        var width = Math.Max(1, (int)MathF.Ceiling(Width * cos + Height * sin - Epsilon));
        var height = Math.Max(1, (int)MathF.Ceiling(Width * sin + Height * cos - Epsilon));

        // Translate so the source centre lands on the new canvas centre, then rotate about that centre.
        var forward = AffineMatrix.Translation(width / 2f, height / 2f)
            .Multiply(AffineMatrix.Rotation(degrees))
            .Multiply(AffineMatrix.Translation(-Width / 2f, -Height / 2f));

        return ReplaceBuffer(Mapping.Transform(this[0].Pixels.View, forward, width, height, background, pool));
    }

    /// <summary>Applies an 8-parameter perspective homography into a new canvas.</summary>
    /// <exception cref="ArgumentException">The homography is not eight values or is singular.</exception>
    public Image Perspective(ReadOnlySpan<float> homography, int width, int height,
        RgbaColor background = default, IPixelPool? pool = null) =>
        ReplaceBuffer(Mapping.Perspective(this[0].Pixels.View, homography, width, height, background, pool));

    /// <summary>Applies an arbitrary affine transform into a new canvas.</summary>
    /// <param name="forward">Source → destination transform.</param>
    /// <param name="width">Destination width.</param>
    /// <param name="height">Destination height.</param>
    /// <param name="background">Colour outside the source.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentException">The transform is singular.</exception>
    public Image Affine(AffineMatrix forward, int width, int height, RgbaColor background = default,
        IPixelPool? pool = null) =>
        ReplaceBuffer(Mapping.Transform(this[0].Pixels.View, forward, width, height, background, pool));
}
