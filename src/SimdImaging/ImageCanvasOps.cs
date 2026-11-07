using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>Canvas fitting: aspect-preserving resize, padding, cropping, tiling and rounded corners.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Resizes into a canvas of the requested size, honouring the aspect ratio according to
    /// <paramref name="mode"/>; <see cref="ResizeMode.Max"/> and <see cref="ResizeMode.Min"/> use the request as
    /// a bound and return the scaled size.
    /// </summary>
    /// <param name="width">Requested width.</param>
    /// <param name="height">Requested height.</param>
    /// <param name="mode">Stretch, fit-and-pad, cover-and-crop, or bound.</param>
    /// <param name="anchor">Where the content sits in the canvas.</param>
    /// <param name="resampler">Sampling filter.</param>
    /// <param name="background">Colour for the padding.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public Image Resize(int width, int height, ResizeMode mode, Anchor anchor = Anchor.Centre,
        Resampler resampler = Resampler.Bicubic, RgbaColor background = default, IPixelPool? pool = null) =>
        ReplaceBuffer(Canvas.Resize(this[0].Pixels.View, width, height, resampler, mode, anchor, background, pool));

    /// <summary>Resizes to a width, keeping the aspect ratio (height rounded, at least one pixel).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not positive.</exception>
    public Image ResizeToWidth(int width, Resampler resampler = Resampler.Bicubic, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        var height = Math.Max(1, (int)MathF.Round((float)Height * width / Width));
        return Resize(width, height, resampler, pool);
    }

    /// <summary>Resizes to a height, keeping the aspect ratio (width rounded, at least one pixel).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="height"/> is not positive.</exception>
    public Image ResizeToHeight(int height, Resampler resampler = Resampler.Bicubic, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var width = Math.Max(1, (int)MathF.Round((float)Width * height / Height));
        return Resize(width, height, resampler, pool);
    }

    /// <summary>Changes the canvas without scaling: pads when it grows, crops when it shrinks.</summary>
    /// <param name="width">New canvas width.</param>
    /// <param name="height">New canvas height.</param>
    /// <param name="anchor">Which part survives a crop, and where the content sits in a pad.</param>
    /// <param name="background">Colour for the padding.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public Image ResizeCanvas(int width, int height, Anchor anchor = Anchor.Centre,
        RgbaColor background = default, IPixelPool? pool = null) =>
        ReplaceBuffer(Canvas.ResizeCanvas(this[0].Pixels.View, width, height, anchor, background, pool));

    /// <summary>Repeats the image to fill a canvas; <paramref name="mirror"/> flips every other tile.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public Image Tile(int width, int height, bool mirror = false, IPixelPool? pool = null) =>
        ReplaceBuffer(Canvas.Tile(this[0].Pixels.View, width, height, mirror, pool));

    /// <summary>Removes a uniform border matching the top-left pixel; a uniform image trims to one pixel.</summary>
    /// <param name="tolerance">Largest per-channel difference still considered the same, 0..1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is negative.</exception>
    public Image Trim(float tolerance = 0f, IPixelPool? pool = null) =>
        ReplaceBuffer(Canvas.Trim(this[0].Pixels.View, tolerance, pool));

    /// <summary>Rounds the corners with an antialiased alpha mask; at half the short side the result is a circle.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is not positive.</exception>
    public Image RoundCorners(float radius, IPixelPool? pool = null) =>
        ReplaceBuffer(Canvas.RoundCorners(this[0].Pixels.View, radius, pool));

    /// <summary>Swaps the axes; <paramref name="anti"/> mirrors across the anti-diagonal instead.</summary>
    public Image Transpose(bool anti = false, IPixelPool? pool = null) =>
        ReplaceBuffer(Orientation.Transpose(this[0].Pixels.View, anti, pool));

    /// <summary>Rotates by whole quarter turns and optionally mirrors, in one pass.</summary>
    /// <param name="quarterTurns">Clockwise quarter turns; taken modulo 4, negatives allowed.</param>
    /// <param name="flipHorizontal">Mirror the result left-to-right.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public Image RotateFlip(int quarterTurns, bool flipHorizontal = false, IPixelPool? pool = null) =>
        ReplaceBuffer(Orientation.RotateFlip(this[0].Pixels.View, quarterTurns, flipHorizontal, pool));

    /// <summary>Crops the borders whose luma entropy is a small fraction of the busiest line.</summary>
    /// <param name="threshold">Fraction of the highest row/column entropy below which a line is margin, 0..1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0..1.</exception>
    public Image EntropyCrop(float threshold = 0.25f, IPixelPool? pool = null) =>
        ReplaceBuffer(Processing.Geometry.EntropyCrop.Crop(this[0].Pixels.View, threshold, pool));
}
