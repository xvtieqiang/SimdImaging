using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging;

/// <summary>
/// An image: one or more frames of RGBA pixels in a single <see cref="WorkingFormat"/>, plus the colour
/// signalling and metadata blocks the decoder reported.
/// <para>
/// Frames are owned; disposing the image releases their buffers to the pool. The static
/// <see cref="Load(string, LoadOptions?, CancellationToken)"/> family lives on this type on purpose: the
/// namespace is already <c>SimdImaging</c>, and <c>Image.Load(...)</c> is the shape .NET callers expect.
/// </para>
/// </summary>
public sealed partial class Image : IDisposable
{
    private readonly ImageFrame[] _frames;
    private int _disposed;

    private Image(ImageFrame[] frames, ImageInfo info, WorkingFormat format,
        ColorEncoding colorEncoding, ImageMetadata metadata)
    {
        _frames = frames;
        Info = info;
        Format = format;
        ColorEncoding = colorEncoding;
        Metadata = metadata;
    }

    /// <summary>Pixel width.</summary>
    public int Width => Info.Width;

    /// <summary>Pixel height.</summary>
    public int Height => Info.Height;

    /// <summary>The layout every frame is stored in.</summary>
    public WorkingFormat Format { get; }

    /// <summary>Number of frames. One in this milestone; multi-frame arrives with the frame reader.</summary>
    public int FrameCount => _frames.Length;

    /// <summary>Header information: format, real bit depth, orientation, resolution, colour encoding.</summary>
    public ImageInfo Info { get; }

    /// <summary>How the samples encode colour. Informational: no operator rescales pixels because of it.</summary>
    public ColorEncoding ColorEncoding { get; }

    /// <summary>The Exif / XMP / ICC blocks the file carried, for passing on to an encoder.</summary>
    public ImageMetadata Metadata { get; }

    /// <summary>One frame.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the image.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public ImageFrame this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if ((uint)index >= (uint)_frames.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Frame must be 0..{_frames.Length - 1}.");
            return _frames[index];
        }
    }

    /// <summary>The bytes of frame 0, for filling right after <see cref="Create"/>. Writing to an image someone
    /// else holds is the caller's problem; operators return new buffers instead.</summary>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Span<byte> WritablePixels => this[0].Pixels.View.Span;

    /// <summary>
    /// Creates a zero-filled image in <paramref name="format"/>, to fill through <see cref="WritablePixels"/>.
    /// This is the constructor the tests and tools use; decoded images come from <c>Load</c>.
    /// </summary>
    public static Image Create(int width, int height, WorkingFormat format, IPixelPool? pool = null)
    {
        var buffer = WorkingBuffer.Rent(width, height, format, pool, clear: true);
        var info = new ImageInfo(width, height, 4, format.ToSampleFormat());
        return new Image([new ImageFrame(buffer, null)], info, format, ColorEncoding.AssumedSrgb, ImageMetadata.Empty);
    }

    /// <summary>Copies every frame into new buffers the caller owns, keeping format, colour signalling and metadata.</summary>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Image Clone(IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var frames = new ImageFrame[_frames.Length];
        try
        {
            for (var i = 0; i < _frames.Length; i++)
                frames[i] = new ImageFrame(_frames[i].Pixels.Clone(pool), _frames[i].Duration);
        }
        catch
        {
            foreach (var frame in frames)
                frame?.Dispose();
            throw;
        }

        return new Image(frames, Info, Format, ColorEncoding, Metadata);
    }

    /// <summary>Releases every frame's buffer. Afterwards the indexer and <see cref="WritablePixels"/> throw.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var frame in _frames)
            frame.Dispose();
    }

    /// <summary>Turns an operator's output buffer into a new image with this image's metadata and colour signalling.</summary>
    internal Image ReplaceBuffer(WorkingBuffer buffer) => ReplaceBuffer(buffer, Metadata, Info.Orientation);

    /// <summary>Same, with the metadata and orientation replaced — what a transcode or an auto-orient needs.</summary>
    internal Image ReplaceBuffer(WorkingBuffer buffer, ImageMetadata metadata, ImageOrientation orientation)
    {
        // The buffer's own format, not the source's: an operator that returns a different working format
        // (colour-space conversion returns floats) must not be relabelled as the input's.
        var info = new ImageInfo(buffer.Width, buffer.Height, 4, buffer.Format.ToSampleFormat())
        {
            Format = Info.Format,
            // Same format keeps the decoder's reported depth (a 10-bit file stored as 16 stays 10); a
            // format-changing operator reports its own.
            BitDepth = buffer.Format == Format ? Info.BitDepth : buffer.Format.BytesPerSample() * 8,
            Orientation = orientation,
            FrameCount = 1,
            ColorEncoding = ColorEncoding,
            Resolution = Info.Resolution,
            HasThumbnail = Info.HasThumbnail,
        };
        return new Image([new ImageFrame(buffer, null)], info, buffer.Format, ColorEncoding, metadata);
    }
}
