using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Analysis operators: images whose pixels carry a measurement instead of colour.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Squared Euclidean distance from every pixel to the nearest foreground pixel. The mask is the alpha
    /// channel, as everywhere else in the library, and the result is a
    /// <see cref="WorkingFormat.Rgba32F"/> field with the squared distance in R, G and B and 1 in A.
    /// </summary>
    /// <param name="threshold">Alpha at or above this counts as foreground; 0.5 is the 8-bit 128 boundary.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside (0, 1].</exception>
    public Image DistanceTransform(float threshold = 0.5f, IPixelPool? pool = null) =>
        ReplaceBuffer(DistanceTransformOps.SquaredEuclidean(this[0].Pixels.View, threshold, pool));
}
