using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>Metadata transfer and re-encoding.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Copies the pixels and replaces the metadata blocks. The pixels are duplicated: an image owns its
    /// buffers, and handing the same buffer to two images would double-dispose it.
    /// </summary>
    /// <param name="metadata">The blocks to attach; <see cref="ImageMetadata.Empty"/> strips them.</param>
    /// <param name="pool">Where the copy is rented.</param>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Image WithMetadata(ImageMetadata metadata, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return ReplaceBuffer(this[0].Pixels.Clone(pool), metadata, Info.Orientation);
    }

    /// <summary>Takes <paramref name="source"/>'s metadata blocks onto a copy of this image's pixels.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public Image CopyMetadataFrom(Image source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return WithMetadata(source.Metadata);
    }

    /// <summary>
    /// Applies the EXIF orientation to the pixels and resets the tag to <see cref="ImageOrientation.Normal"/>.
    /// A file already upright is copied unchanged.
    /// </summary>
    public Image AutoOrient(IPixelPool? pool = null) =>
        ReplaceBuffer(Orientation.AutoOrient(this[0].Pixels.View, Info.Orientation, pool), Metadata,
            ImageOrientation.Normal);

    /// <summary>
    /// Re-encodes a file, optionally replacing its metadata blocks on the way through. This is a decode and an
    /// encode: the compressed stream is not passed through, so quality settings apply.
    /// <para>
    /// The decode is full fidelity and as stored: no <see cref="LoadOptions"/> apply, so nothing is scaled or
    /// turned. The Exif Orientation tag travels with the metadata, which is what a re-encode has to do — the
    /// output displays the same way as the input. To bake the orientation in, load with
    /// <see cref="LoadOptions.ApplyOrientation"/> and save that instead.
    /// </para>
    /// </summary>
    /// <param name="sourcePath">The file to read.</param>
    /// <param name="destinationPath">The file to write; its extension picks the format.</param>
    /// <param name="metadata">Blocks to write instead of the file's own; null keeps them.</param>
    /// <param name="options">Encoder options.</param>
    /// <param name="cancellationToken">Checked between coarse units of work.</param>
    /// <exception cref="InvalidDataException">The input is not a decodable image.</exception>
    /// <exception cref="NotSupportedException">The input has more than one frame.</exception>
    public static void Transcode(string sourcePath, string destinationPath, ImageMetadata? metadata = null,
        SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);

        using var image = Load(sourcePath, null, cancellationToken);
        if (metadata is null)
        {
            Save(image, destinationPath, options, cancellationToken);
            return;
        }

        using var stamped = image.WithMetadata(metadata);
        Save(stamped, destinationPath, options, cancellationToken);
    }
}
