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
    /// <summary>Copies a region out. Lossless: no resampling, same working format.</summary>
    /// <exception cref="ArgumentException">The region does not lie inside the image.</exception>
    public Image Crop(PixelRect region, IPixelPool? pool = null) =>
        ReplaceBuffer(Transforms.Crop(this[0].Pixels.View, region, pool));

    /// <summary>Grows the canvas by the four margins and fills the new border with <paramref name="background"/>.</summary>
    public Image Pad(int left, int top, int right, int bottom, RgbaColor background, IPixelPool? pool = null) =>
        ReplaceBuffer(Transforms.Pad(this[0].Pixels.View, left, top, right, bottom, background, pool));

    /// <summary>Grows the canvas by the same margin on all four sides.</summary>
    public Image Pad(int margin, RgbaColor background, IPixelPool? pool = null) =>
        Pad(margin, margin, margin, margin, background, pool);

    /// <summary>Mirrors left-to-right.</summary>
    public Image FlipHorizontal(IPixelPool? pool = null) =>
        ReplaceBuffer(Transforms.FlipHorizontal(this[0].Pixels.View, pool));

    /// <summary>Mirrors top-to-bottom.</summary>
    public Image FlipVertical(IPixelPool? pool = null) =>
        ReplaceBuffer(Transforms.FlipVertical(this[0].Pixels.View, pool));

    /// <summary>Rotates by 90° without resampling.</summary>
    public Image Rotate90(bool clockwise = true, IPixelPool? pool = null) =>
        ReplaceBuffer(Transforms.Rotate90(this[0].Pixels.View, clockwise, pool));

    /// <summary>Rotates by 180° without resampling.</summary>
    public Image Rotate180(IPixelPool? pool = null) =>
        ReplaceBuffer(Transforms.Rotate180(this[0].Pixels.View, pool));

    /// <summary>
    /// Resamples to <paramref name="width"/> × <paramref name="height"/> with a separable filter. Alpha is
    /// premultiplied while filtering, so transparent pixels cannot bleed their colour into the result.
    /// </summary>
    /// <param name="width">Destination width (&gt; 0).</param>
    /// <param name="height">Destination height (&gt; 0).</param>
    /// <param name="resampler">The reconstruction filter; <see cref="Resampler.Bicubic"/> by default.</param>
    /// <param name="pool">Where the destination buffer is rented from.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public Image Resize(int width, int height, Resampler resampler = Resampler.Bicubic, IPixelPool? pool = null) =>
        ReplaceBuffer(Resample.Resize(this[0].Pixels.View, width, height, resampler, pool));

    /// <summary>Reads one colour at a continuous coordinate, clamping at the edges.</summary>
    /// <param name="x">Column coordinate.</param>
    /// <param name="y">Row coordinate.</param>
    /// <param name="resampler">Nearest or bilinear.</param>
    /// <exception cref="NotSupportedException">Any other resampler.</exception>
    public RgbaColor SamplePixel(float x, float y, Resampler resampler = Resampler.Bilinear) =>
        Sampling.Sample(this[0].Pixels.View, x, y, resampler);

    /// <summary>Warps through a caller-supplied destination → source mapping.</summary>
    /// <param name="width">Destination width.</param>
    /// <param name="height">Destination height.</param>
    /// <param name="map">Destination → source coordinate mapping.</param>
    /// <param name="resampler">Nearest or bilinear.</param>
    /// <param name="background">Colour outside the source.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public Image Warp(int width, int height, PixelWarp map, Resampler resampler = Resampler.Bilinear,
        RgbaColor background = default, IPixelPool? pool = null) =>
        ReplaceBuffer(Sampling.Warp(this[0].Pixels.View, width, height, map, resampler, background, pool));

    /// <summary>Multiplies colour by alpha. Filters do this internally; expose it for callers that hand pixels to
    /// a compositor expecting associated alpha.</summary>
    public Image Premultiply(IPixelPool? pool = null) =>
        ReplaceBuffer(AlphaOps.Premultiply(this[0].Pixels.View, pool));

    /// <summary>Divides colour by alpha.</summary>
    public Image Unpremultiply(IPixelPool? pool = null) =>
        ReplaceBuffer(AlphaOps.Unpremultiply(this[0].Pixels.View, pool));

    /// <summary>Source-over composition of <paramref name="overlay"/> at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentException">The overlay does not fit inside this image at that position.</exception>
    /// <exception cref="ArgumentException">The overlay's working format differs from this image's.</exception>
    public Image Composite(Image overlay, int x, int y, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (overlay.Format != Format)
            throw new ArgumentException($"The overlay is {overlay.Format} but this image is {Format}.", nameof(overlay));
        return ReplaceBuffer(AlphaOps.Composite(this[0].Pixels.View, overlay[0].Pixels.View, x, y, pool));
    }

    /// <summary>Blends <paramref name="overlay"/> onto this image with a W3C blend mode, composited source-over.</summary>
    /// <exception cref="ArgumentException">The overlay does not fit, or its format differs.</exception>
    public Image Blend(Image overlay, int x, int y, BlendMode mode, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (overlay.Format != Format)
            throw new ArgumentException($"The overlay is {overlay.Format} but this image is {Format}.", nameof(overlay));
        return ReplaceBuffer(Processing.Blend.Apply(this[0].Pixels.View, overlay[0].Pixels.View, x, y, mode, pool));
    }

    /// <summary>Composes <paramref name="overlay"/> with one of the twelve Porter-Duff operators.</summary>
    /// <exception cref="ArgumentException">The overlay does not fit, or its format differs.</exception>
    public Image Composite(Image overlay, int x, int y, PorterDuff mode, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (overlay.Format != Format)
            throw new ArgumentException($"The overlay is {overlay.Format} but this image is {Format}.", nameof(overlay));
        return ReplaceBuffer(AlphaOps.Composite(this[0].Pixels.View, overlay[0].Pixels.View, x, y, mode, pool));
    }
}
