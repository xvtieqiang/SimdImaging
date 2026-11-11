using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Frames, animation composition and thumbnails.</summary>
public sealed partial class Image
{
    /// <summary>The frames in file order. A still image has one; an animation or a multi-page document has as many
    /// as the codec decoded. Each frame's <see cref="ImageFrame.Duration"/> is what a viewer shows it for.</summary>
    public ReadOnlySpan<ImageFrame> Frames => _frames;

    /// <summary>
    /// Copies the selected frames, in the order given, into a new image. Frames are duplicated: two images never
    /// share a buffer.
    /// </summary>
    /// <param name="indices">Frame indices; repeats are allowed.</param>
    /// <exception cref="ArgumentException">The list is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index is outside the image.</exception>
    public Image KeepFrames(params int[] indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Length == 0)
            throw new ArgumentException("Select at least one frame.", nameof(indices));

        var frames = new ImageFrame[indices.Length];
        try
        {
            for (var i = 0; i < indices.Length; i++)
            {
                if ((uint)indices[i] >= (uint)_frames.Length)
                    throw new ArgumentOutOfRangeException(nameof(indices), indices[i],
                        $"Frame must be 0..{_frames.Length - 1}.");

                frames[i] = new ImageFrame(_frames[indices[i]].Pixels.Clone(), _frames[indices[i]].Duration);
            }
        }
        catch
        {
            foreach (var frame in frames)
                frame?.Dispose();
            throw;
        }

        return new Image(frames, BuildInfo(Width, Height, Format, frames.Length), Format, ColorEncoding, Metadata);
    }

    /// <summary>
    /// Builds an animation from separate frames: every frame is drawn over the running canvas, so each stored
    /// frame is the cumulative picture a viewer shows. Frames may be smaller than the canvas; they land at the
    /// top-left.
    /// </summary>
    /// <param name="width">Canvas width.</param>
    /// <param name="height">Canvas height.</param>
    /// <param name="frames">The frames, in display order.</param>
    /// <param name="frameDuration">Duration given to every frame, or null for a document.</param>
    /// <exception cref="ArgumentException">The list is empty or holds a different working format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public static Image ComposeAnimation(int width, int height, IReadOnlyList<Image> frames,
        TimeSpan? frameDuration = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (frames.Count == 0)
            throw new ArgumentException("An animation needs at least one frame.", nameof(frames));

        var format = frames[0].Format;
        foreach (var frame in frames)
        {
            if (frame.Format != format)
                throw new ArgumentException($"Frames differ in working format: {format} and {frame.Format}.", nameof(frames));
        }

        var composed = new ImageFrame[frames.Count];
        var canvas = Create(width, height, format);
        try
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (frame.Width > width || frame.Height > height)
                    throw new ArgumentException(
                        $"Frame {i} is {frame.Width}x{frame.Height}, larger than the {width}x{height} canvas.", nameof(frames));

                var next = canvas.DrawImage(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                canvas.Dispose();
                canvas = next;
                composed[i] = new ImageFrame(canvas[0].Pixels.Clone(), frameDuration);
            }
        }
        catch
        {
            foreach (var frame in composed)
                frame?.Dispose();
            throw;
        }
        finally
        {
            canvas.Dispose();
        }

        return new Image(composed, frames[0].BuildInfo(width, height, format, composed.Length), format,
            frames[0].ColorEncoding, frames[0].Metadata);
    }

    /// <summary>
    /// Shrinks into a box, keeping the aspect ratio, then sharpens lightly — the residue a downscale leaves.
    /// An image that already fits is not upscaled.
    /// </summary>
    /// <param name="maxWidth">Box width.</param>
    /// <param name="maxHeight">Box height.</param>
    /// <param name="resampler">Downscale filter.</param>
    /// <param name="sharpenAmount">Unsharp mask amount; 0 leaves the resize alone.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive, or the amount is negative.</exception>
    public Image Thumbnail(int maxWidth, int maxHeight, Resampler resampler = Resampler.Lanczos3,
        float sharpenAmount = 0.4f, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(sharpenAmount);

        var scale = MathF.Min(maxWidth / (float)Width, maxHeight / (float)Height);
        if (scale >= 1f)
            return Clone(pool);

        var width = Math.Max(1, (int)MathF.Round(Width * scale));
        var height = Math.Max(1, (int)MathF.Round(Height * scale));

        using var resized = Resize(width, height, resampler);
        return sharpenAmount > 0f ? resized.Sharpen(sharpenAmount, 0.8f, pool) : resized.Clone(pool);
    }

    /// <summary>
    /// Reads the reduced-size preview the file embeds (JPEG EXIF, HEIF, WebP), without decoding the full image.
    /// Returns false when the file has none.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="thumbnail">The preview, when there is one.</param>
    /// <exception cref="InvalidDataException">The file is not a recognised image.</exception>
    public static bool TryLoadEmbeddedThumbnail(string path, out Image? thumbnail)
    {
        ArgumentNullException.ThrowIfNull(path);
        thumbnail = null;

        using var session = ImageDecoder.Open(path);
        if (!session.GetFrame(0).TryGetThumbnail(out var decoded))
            return false;

        using (decoded)
        {
            var format = WorkingFormatExtensions.FromSampleFormat(decoded.SampleFormat);
            var buffer = WorkingBuffer.Rent(decoded.Width, decoded.Height, format, null);
            try
            {
                CopyDecoded(decoded, buffer.View);
            }
            catch
            {
                buffer.Dispose();
                throw;
            }

            var info = new ImageInfo(decoded.Width, decoded.Height, 4, format.ToSampleFormat())
            {
                ColorEncoding = decoded.ColorEncoding,
            };
            thumbnail = new Image([new ImageFrame(buffer, null)], info, format, decoded.ColorEncoding, decoded.Metadata);
            return true;
        }
    }

    private ImageInfo BuildInfo(int width, int height, WorkingFormat format, int frameCount) =>
        new(width, height, 4, format.ToSampleFormat())
        {
            Format = Info.Format,
            BitDepth = Info.BitDepth,
            Orientation = Info.Orientation,
            FrameCount = frameCount,
            ColorEncoding = ColorEncoding,
            Resolution = Info.Resolution,
            HasThumbnail = Info.HasThumbnail,
        };
}
