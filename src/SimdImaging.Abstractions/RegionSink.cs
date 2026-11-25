using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Abstractions;

/// <summary>
/// A destination that can accept an arbitrary rectangle of pixels: the two-dimensional counterpart of
/// <see cref="IRowSink"/>, and the seam a tile executor writes through.
/// <para>
/// <see cref="Width"/> / <see cref="Height"/> describe the whole destination, so a tile can be placed at its
/// absolute coordinates without the executor tracking the layout. Not required to be thread-safe: a tile executor
/// writes one tile at a time.
/// </para>
/// </summary>
public interface IRegionSink
{
    /// <summary>Pixels per row of the whole destination.</summary>
    int Width { get; }

    /// <summary>Rows in the whole destination.</summary>
    int Height { get; }

    /// <summary>The layout the source view is in, and the layout the destination must hold.</summary>
    WorkingFormat Format { get; }

    /// <summary>
    /// Writes the pixels of <paramref name="region"/> from <paramref name="source"/>, whose row 0 is the region's
    /// row 0 and whose dimensions are exactly the region's. The region already lies inside the image.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="region"/> is not inside the destination, or
    /// <paramref name="source"/> is not the region's size or format.</exception>
    void WriteRegion(PixelRect region, PixelView source);
}
