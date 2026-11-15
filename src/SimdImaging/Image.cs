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
    private ImageFrame[] _frames;
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
    public WorkingFormat Format { get; private set; }

    /// <summary>Number of frames. One in this milestone; multi-frame arrives with the frame reader.</summary>
    public int FrameCount => _frames.Length;

    /// <summary>Header information: format, real bit depth, orientation, resolution, colour encoding.</summary>
    /// <remarks>Private setter only so <see cref="Mutate"/> can adopt a result; callers see a getter.</remarks>
    public ImageInfo Info { get; private set; }

    /// <summary>How the samples encode colour. Informational: no operator rescales pixels because of it.</summary>
    public ColorEncoding ColorEncoding { get; private set; }

    /// <summary>The Exif / XMP / ICC blocks the file carried, for passing on to an encoder.</summary>
    public ImageMetadata Metadata { get; private set; }

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

    /// <summary>
    /// Copies frame 0 through a chain of operations and returns the result — the original is untouched. This is the
    /// one call for "several edits in a row": the steps are recorded first and executed once, so a chain of point
    /// operators costs a single destination buffer instead of one per step, and every intermediate buffer is
    /// released as soon as the next step has read it.
    /// <para>
    /// Frame 0 only: a multi-frame image would lose every frame after the first, so this throws rather than
    /// silently dropping them.
    /// </para>
    /// </summary>
    /// <param name="steps">Records the operations, e.g. <c>x => x.Resize(800, 600).Saturation(1.2f)</c>.</param>
    /// <param name="pool">Where the buffers are rented from; null uses <see cref="ArrayPixelPool.Shared"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is null.</exception>
    /// <exception cref="NotSupportedException">The image has more than one frame.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Image Clone(Action<ImageChain> steps, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_frames.Length > 1)
            throw new NotSupportedException(
                $"The image has {_frames.Length} frames and a chain edits one; select a frame with KeepFrames first.");

        var chain = new ImageChain();
        steps(chain);

        var buffer = chain.Run(_frames[0].Pixels.View, pool);
        try
        {
            return ReplaceBuffer(buffer, Metadata, Info.Orientation);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
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
    /// <summary>
    /// Runs a chain and applies the result to <em>this</em> image, the way a mutating call site expects.
    /// </summary>
    /// <remarks>
    /// Every other operator returns a new image and leaves the source untouched; this one deliberately does the
    /// opposite, because ported call sites read <c>image.Mutate(x =&gt; x.Resize(...))</c> and then keep using
    /// <c>image</c>. Returning a new image from that shape would be a silent trap: the call compiles, the
    /// original never changes, and the bug surfaces far away.
    /// <para>
    /// The old buffers are released to the pool once the new ones are in place, so the peak is the same two
    /// buffers <see cref="Clone(Action{ImageChain}, IPixelPool?)"/> needs. If the chain throws, the image is
    /// left exactly as it was.
    /// </para>
    /// </remarks>
    /// <param name="steps">Records the operations; the same methods <see cref="ImageChain"/> exposes.</param>
    /// <param name="pool">Optional pool for the intermediate and destination buffers.</param>
    /// <returns>This image, so the call can be chained or ignored.</returns>
    /// <exception cref="NotSupportedException">The image has more than one frame.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Image Mutate(Action<ImageChain> steps, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_frames.Length > 1)
            throw new NotSupportedException(
                $"The image has {_frames.Length} frames and a chain edits one; select a frame with KeepFrames first.");

        var chain = new ImageChain();
        steps(chain);

        var buffer = chain.Run(_frames[0].Pixels.View, pool);
        Image replacement;
        try
        {
            replacement = ReplaceBuffer(buffer, Metadata, Info.Orientation);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        Adopt(replacement);
        return this;
    }

    /// <summary>
    /// Takes over another image's frames and header, releasing the ones held here. The donor is left empty, so
    /// disposing it afterwards is harmless.
    /// </summary>
    private void Adopt(Image donor)
    {
        var previous = _frames;
        _frames = donor._frames;
        donor._frames = [];
        Info = donor.Info;
        Format = donor.Format;
        ColorEncoding = donor.ColorEncoding;
        Metadata = donor.Metadata;

        foreach (var frame in previous)
            frame.Dispose();
    }

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
