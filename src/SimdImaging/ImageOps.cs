using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>
/// Geometry and alpha operators on <see cref="Image"/>. Each returns a new image the caller owns; the source is
/// never modified, so a failed operation cannot leave a half-edited frame behind.
/// </summary>
public sealed partial class Image
{
    

    

    

    

    

    

    

    

    /// <summary>Reads one colour at a continuous coordinate, clamping at the edges.</summary>
    /// <param name="x">Column coordinate.</param>
    /// <param name="y">Row coordinate.</param>
    /// <param name="resampler">Nearest or bilinear.</param>
    /// <exception cref="NotSupportedException">Any other resampler.</exception>
    public RgbaColor SamplePixel(float x, float y, Resampler resampler = Resampler.Bilinear) =>
        Sampling.Sample(this[0].Pixels.View, x, y, resampler);

    

    

    

    

    

    

    

    
}
