using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Where a watermark sits.</summary>
public enum WatermarkPosition : byte
{
    /// <summary>Top-left corner.</summary>
    TopLeft,
    /// <summary>Top-right corner.</summary>
    TopRight,
    /// <summary>Bottom-left corner.</summary>
    BottomLeft,
    /// <summary>Bottom-right corner.</summary>
    BottomRight,
    /// <summary>Centred.</summary>
    Center,
}

/// <summary>Image-onto-image compositing.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Draws <paramref name="source"/> into <paramref name="destination"/>, resampling it to that rectangle.
    /// Returns a new image: this is compositing, not a canvas write — see <see cref="Fill"/> for in-place drawing.
    /// </summary>
    /// <param name="source">The image to draw.</param>
    /// <param name="destination">Where it goes; must fit inside this image.</param>
    /// <param name="opacity">0..1 applied to the source's alpha.</param>
    /// <exception cref="ArgumentException">The rectangle does not fit, or the format differs.</exception>
    public Image DrawImage(Image source, PixelRect destination, float opacity = 1f)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Format != Format)
            throw new ArgumentException($"The source is {source.Format} but this image is {Format}.", nameof(source));

        using var scaled = source.Resize(destination.Width, destination.Height, Resampler.Bilinear);
        if (opacity >= 1f)
            return Composite(scaled, destination.X, destination.Y);

        using var faded = scaled.Opacity(opacity);
        return Composite(faded, destination.X, destination.Y);
    }

    /// <summary>Places <paramref name="mark"/> at a corner (or the centre) with a margin and an opacity.</summary>
    /// <exception cref="ArgumentException">The mark plus margin does not fit inside this image.</exception>
    public Image Watermark(Image mark, WatermarkPosition position = WatermarkPosition.BottomRight, int margin = 0,
        float opacity = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);

        var x = position switch
        {
            WatermarkPosition.TopLeft or WatermarkPosition.BottomLeft => margin,
            WatermarkPosition.TopRight or WatermarkPosition.BottomRight => Width - mark.Width - margin,
            _ => (Width - mark.Width) / 2,
        };
        var y = position switch
        {
            WatermarkPosition.TopLeft or WatermarkPosition.TopRight => margin,
            WatermarkPosition.BottomLeft or WatermarkPosition.BottomRight => Height - mark.Height - margin,
            _ => (Height - mark.Height) / 2,
        };

        if (x < 0 || y < 0 || x + mark.Width > Width || y + mark.Height > Height)
            throw new ArgumentException("The watermark plus margin does not fit inside this image.", nameof(mark));

        return DrawImage(mark, new PixelRect(x, y, mark.Width, mark.Height), opacity);
    }
}
