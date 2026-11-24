using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Abstractions;

/// <summary>
/// A source of pixels that can be read as arbitrary rectangles, not only downwards: the two-dimensional
/// counterpart of <see cref="IRowSource"/>, and the seam that lets one rectangle of a huge file be processed
/// without the image ever being held whole.
/// <para>
/// <see cref="Width"/> / <see cref="Height"/> describe the <b>whole</b> image and come from the header, not from
/// what happens to have been read. That is what makes a region run identical to a whole-image run: the kernels
/// clamp at the real image edge, never at the edge of the window they were handed.
/// </para>
/// <para>Not required to be thread-safe: an executor reads one window at a time from one thread.</para>
/// </summary>
public interface IRegionSource
{
    /// <summary>Pixels per row of the whole image.</summary>
    int Width { get; }

    /// <summary>Rows in the whole image.</summary>
    int Height { get; }

    /// <summary>The layout the rows this source writes are in.</summary>
    WorkingFormat Format { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with the pixels of <paramref name="region"/>, row 0 of the region at
    /// row 0 of the destination. Coordinates are absolute, in the whole image's pixels, and
    /// <paramref name="region"/> already lies inside the image — the caller clamps, this method does not.
    /// <para>
    /// <paramref name="destination"/> dimensions equal the region's, its format is <see cref="Format"/>, and its
    /// stride may be larger than <c>Width × Format.BytesPerPixel()</c>; rows are written with
    /// <see cref="PixelView.Row"/>. Every pixel is overwritten, and the bytes are the same bytes a whole-image
    /// decode of the same input would produce.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="region"/> is not inside the image, or
    /// <paramref name="destination"/> is not the region's size or format.</exception>
    /// <exception cref="InvalidDataException">The underlying data is malformed or ends early.</exception>
    void ReadRegion(PixelRect region, PixelView destination);
}
