using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>Arbitrary-angle geometry. Unlike the lossless 90° rotations these resample, so they take a
/// background colour for the corners the source does not cover.</summary>
public sealed partial class Image
{
    

    

    

    

    

    

    // Canvas expansion for an arbitrary-angle rotation, extracted verbatim from Rotate so the
    // frozen legacy path and the WithOptions path share one definition. Pure code motion.
    // Internal rather than private because a gain-map pair derives its gain canvas from this one, not
    // by re-running the ceiling on the gain map's own dimensions.
    internal static (int Width, int Height, AffineMatrix Forward) RotationCanvas(int sourceWidth, int sourceHeight,
        float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Abs(MathF.Cos(radians));
        var sin = MathF.Abs(MathF.Sin(radians));

        // Ceiling with an epsilon: MathF.Cos(90°) is -4.4e-8, so the exact-fit case computes 5.0000004 and
        // would grow the canvas by one pixel for a rotation that needs none.
        const float Epsilon = 1e-4f;
        var width = Math.Max(1, (int)MathF.Ceiling(sourceWidth * cos + sourceHeight * sin - Epsilon));
        var height = Math.Max(1, (int)MathF.Ceiling(sourceWidth * sin + sourceHeight * cos - Epsilon));

        // Translate so the source centre lands on the new canvas centre, then rotate about that centre.
        var forward = AffineMatrix.Translation(width / 2f, height / 2f)
            .Multiply(AffineMatrix.Rotation(degrees))
            .Multiply(AffineMatrix.Translation(-sourceWidth / 2f, -sourceHeight / 2f));

        return (width, height, forward);
    }
}
