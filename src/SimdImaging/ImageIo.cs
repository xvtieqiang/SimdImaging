using System.Runtime.InteropServices;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Webp;
using SimdImaging.Abstractions;

namespace SimdImaging;

public sealed partial class Image
{
    /// <summary>Reads and decodes an image file.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a recognised, decodable image.</exception>
    /// <exception cref="ArgumentException"><see cref="LoadOptions.Region"/> does not lie inside the image, or it is
    /// combined with <see cref="LoadOptions.PreferNativeScale"/>.</exception>
    /// <exception cref="InvalidOperationException">The output would exceed <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Image Load(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Load(File.ReadAllBytes(path), options, cancellationToken);
    }

    /// <summary>Decodes an image held in memory.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a recognised, decodable image.</exception>
    /// <exception cref="ArgumentException"><see cref="LoadOptions.Region"/> does not lie inside the image, or it is
    /// combined with <see cref="LoadOptions.PreferNativeScale"/>.</exception>
    /// <exception cref="InvalidOperationException">The output would exceed <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Image Load(ReadOnlySpan<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
            throw new InvalidDataException("Empty input.");
        options ??= LoadOptions.Default;

        if (!ImageCodecs.TryReadInfo(data, out var info))
            throw new InvalidDataException("The stream is not a recognised image format.");

        var scaleDenominator = options.ScaleDenominator ?? 1;
        if (options.PreferNativeScale && scaleDenominator > 1)
        {
            // A region is expressed in the pixels of the scale it is given at. Negotiating a different scale would
            // silently reinterpret those coordinates, so the two do not combine.
            if (options.Region is not null)
                throw new ArgumentException(
                    "PreferNativeScale changes the reduction, which would reinterpret Region's coordinates. " +
                    "Use one or the other.", nameof(options));

            // Which reduction the codec can do without a second pass. A native 1 means "this codec does not scale
            // natively at all": taking it would decode the full image, worse on every axis than the box filter.
            var targetWidth = (info.Width + scaleDenominator - 1) / scaleDenominator;
            var targetHeight = (info.Height + scaleDenominator - 1) / scaleDenominator;
            if (ImageCodecs.TryGetClosestSize(data, targetWidth, targetHeight, out _, out _, out var native)
                && native > 1 && native != scaleDenominator)
                scaleDenominator = native;
        }

        var decodeOptions = new DecodeOptions
        {
            Region = options.Region,
            ScaleDenominator = scaleDenominator,
            ApplyOrientation = options.ApplyOrientation,
        };

        // GetOutputSize(in ImageInfo) rather than the width/height overload: with ApplyOrientation it is the one
        // that knows orientations 5..8 exchange the axes.
        var (outputWidth, outputHeight) = decodeOptions.GetOutputSize(info);
        var outputPixels = (long)outputWidth * outputHeight;
        if (outputPixels > options.MaxPixels)
            throw new InvalidOperationException(
                $"The decoded image would be {outputWidth}x{outputHeight} = {outputPixels} pixels, above the {options.MaxPixels} pixel limit.");

        // An animation or a multi-page document needs the session: it owns the frame reader and the compositing
        // state. A still keeps the zero-copy span path.
        if (info.FrameCount > 1)
            return LoadFrames(data.ToArray(), options, decodeOptions, info, cancellationToken);

        if (!ImageCodecs.TryDecode(data, out var decoded, decodeOptions, cancellationToken))
            throw new InvalidDataException("The header was recognised but the pixel data could not be decoded.");

        using (decoded)
        {
            var format = options.Format ?? WorkingFormatExtensions.FromSampleFormat(decoded.SampleFormat);
            var buffer = WorkingBuffer.Rent(decoded.Width, decoded.Height, format, options.Pool);
            try
            {
                CopyDecoded(decoded, buffer.View);
                var outputInfo = new ImageInfo(decoded.Width, decoded.Height, 4, format.ToSampleFormat())
                {
                    Format = info.Format,
                    BitDepth = info.BitDepth,
                    // The tag describes the stored pixels; once the codec has applied it the pixels are upright,
                    // and reporting the file's value would make the next save turn them again.
                    Orientation = options.ApplyOrientation ? ImageOrientation.Normal : info.Orientation,
                    FrameCount = 1,
                    ColorEncoding = decoded.ColorEncoding,
                    Resolution = info.Resolution,
                    HasThumbnail = info.HasThumbnail,
                };
                return new Image([new ImageFrame(buffer, null)], outputInfo, format, decoded.ColorEncoding, decoded.Metadata);
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Decodes an image read from a stream, starting at its current position. The stream is left open — the caller
    /// owns it. Everything downstream works on bytes, so the rest of the stream is buffered first.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a recognised, decodable image.</exception>
    /// <exception cref="ArgumentException"><see cref="LoadOptions.Region"/> does not lie inside the image, or it is
    /// combined with <see cref="LoadOptions.PreferNativeScale"/>.</exception>
    /// <exception cref="InvalidOperationException">The output would exceed <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Image Load(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Load(ReadToEnd(stream, cancellationToken), options, cancellationToken);
    }

    /// <summary>Buffers what is left of <paramref name="stream"/>. Reads in chunks instead of calling
    /// <see cref="Stream.CopyTo(Stream)"/>, whose synchronous overload cannot observe the token: a 100 MB read that
    /// ignores cancellation is the kind of promise this library does not make.</summary>
    private static byte[] ReadToEnd(Stream stream, CancellationToken cancellationToken)
    {
        var capacity = stream.CanSeek && stream.Length > stream.Position
            ? (int)Math.Min(stream.Length - stream.Position, int.MaxValue)
            : 0;

        using var copy = new MemoryStream(capacity);
        var chunk = new byte[64 * 1024];
        for (var read = stream.Read(chunk); read > 0; read = stream.Read(chunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            copy.Write(chunk, 0, read);
        }

        return copy.ToArray();
    }

    /// <summary>
    /// Decodes every frame of an animation or a multi-page document. The codec hands back composited canvases,
    /// so frame <c>i</c> is what a viewer shows at that point — this method only converts and stores them.
    /// </summary>
    private static Image LoadFrames(ReadOnlyMemory<byte> data, LoadOptions options, DecodeOptions decodeOptions,
        ImageInfo info, CancellationToken cancellationToken)    {
        using var session = ImageDecoder.Open(data);
        var count = session.FrameCount;
        var frames = new ImageFrame[count];
        var format = options.Format;
        var metadata = ImageMetadata.Empty;
        ColorEncoding encoding = ColorEncoding.AssumedSrgb;
        var width = 0;
        var height = 0;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var frame = session.GetFrame(i);
                using var decoded = frame.Decode(decodeOptions, cancellationToken);

                format ??= WorkingFormatExtensions.FromSampleFormat(decoded.SampleFormat);
                if (decoded.Width * decoded.Height > options.MaxPixels)
                    throw new InvalidOperationException(
                        $"Frame {i} decodes to {decoded.Width}x{decoded.Height}, above the {options.MaxPixels} pixel limit.");

                var buffer = WorkingBuffer.Rent(decoded.Width, decoded.Height, format.Value, options.Pool);
                try
                {
                    CopyDecoded(decoded, buffer.View);
                }
                catch
                {
                    buffer.Dispose();
                    throw;
                }

                frames[i] = new ImageFrame(buffer, frame.Duration);
                metadata = decoded.Metadata;
                encoding = decoded.ColorEncoding;
                width = decoded.Width;
                height = decoded.Height;
            }
        }
        catch
        {
            foreach (var frame in frames)
                frame?.Dispose();
            throw;
        }

        var workingFormat = format ?? WorkingFormat.Rgba8;
        var outputInfo = new ImageInfo(width, height, 4, workingFormat.ToSampleFormat())
        {
            Format = info.Format,
            BitDepth = info.BitDepth,
            // Same rule as the still path: applied by the frame decoder, so the pixels are upright already.
            Orientation = options.ApplyOrientation ? ImageOrientation.Normal : info.Orientation,
            FrameCount = count,
            ColorEncoding = encoding,
            Resolution = info.Resolution,
            HasThumbnail = info.HasThumbnail,
        };

        return new Image(frames, outputInfo, workingFormat, encoding, metadata);
    }

    /// <summary>Like <see cref="Load(ReadOnlySpan{byte}, LoadOptions?, CancellationToken)"/>, but returns false instead
    /// of throwing when the bytes are not a decodable image.</summary>
    public static bool TryLoad(ReadOnlySpan<byte> data, out Image? image, LoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            image = Load(data, options, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>Like <see cref="Load(Stream, LoadOptions?, CancellationToken)"/>, but returns false instead of
    /// throwing when the bytes are not a decodable image. The stream is left open either way.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static bool TryLoad(Stream stream, out Image? image, LoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            image = Load(stream, options, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Decodes an image read from a stream, awaiting the read. Only the read is asynchronous: the decode itself is
    /// CPU-bound and runs synchronously on the continuation. The stream is left open.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a recognised, decodable image.</exception>
    /// <exception cref="ArgumentException"><see cref="LoadOptions.Region"/> does not lie inside the image, or it is
    /// combined with <see cref="LoadOptions.PreferNativeScale"/>.</exception>
    /// <exception cref="InvalidOperationException">The output would exceed <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<Image> LoadAsync(Stream stream, LoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var data = await ReadToEndAsync(stream, cancellationToken).ConfigureAwait(false);
        return Load(data, options, cancellationToken);
    }

    /// <summary>Reads and decodes an image file, awaiting the read (see
    /// <see cref="LoadAsync(Stream, LoadOptions?, CancellationToken)"/> for what is and is not asynchronous).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a recognised, decodable image.</exception>
    /// <exception cref="ArgumentException"><see cref="LoadOptions.Region"/> does not lie inside the image, or it is
    /// combined with <see cref="LoadOptions.PreferNativeScale"/>.</exception>
    /// <exception cref="InvalidOperationException">The output would exceed <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<Image> LoadAsync(string path, LoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Load(data, options, cancellationToken);
    }

    /// <summary>Like <see cref="LoadAsync(Stream, LoadOptions?, CancellationToken)"/>, but returns null instead of
    /// throwing when the bytes are not a decodable image. The stream is left open either way.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static async Task<Image?> TryLoadAsync(Stream stream, LoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await LoadAsync(stream, options, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Like <see cref="LoadAsync(string, LoadOptions?, CancellationToken)"/>, but returns null instead of
    /// throwing when the file is not a decodable image.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static async Task<Image?> TryLoadAsync(string path, LoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await LoadAsync(path, options, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The asynchronous twin of the chunked read in <see cref="ReadToEnd(Stream, CancellationToken)"/>:
    /// same capacity guess, same 64 KB chunks, but the loop yields to the caller between reads.</summary>
    private static async Task<byte[]> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        var capacity = stream.CanSeek && stream.Length > stream.Position
            ? (int)Math.Min(stream.Length - stream.Position, int.MaxValue)
            : 0;

        using var copy = new MemoryStream(capacity);
        var chunk = new byte[64 * 1024];
        for (var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
             read > 0;
             read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false))
            copy.Write(chunk, 0, read);

        return copy.ToArray();
    }

    /// <summary>Encodes the image and writes it to <paramref name="path"/>. The format comes from the extension.</summary>    /// <exception cref="NotSupportedException">The extension names no format this build can write, or the image has
    /// more than one frame — writing frame 0 of an animation would silently drop the rest.</exception>
    public static void Save(Image image, string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(path);
        var format = FormatFromExtension(Path.GetExtension(path));
        File.WriteAllBytes(path, Encode(image, format, options, cancellationToken));
    }

    /// <summary>Encodes the image to a byte array.</summary>
    /// <param name="image">The image; only frame 0 is written in this milestone.</param>
    /// <param name="format">The container to write.</param>
    /// <param name="options">Quality and metadata; null uses the image's own metadata and encoder defaults.</param>
    /// <param name="cancellationToken">Checked between coarse units of work.</param>
    /// <exception cref="ArgumentException"><paramref name="format"/> is <see cref="ImageFormat.Unknown"/>, or
    /// <see cref="SaveOptions.JpegQuality"/> is set for a non-JPEG format.</exception>
    /// <exception cref="NotSupportedException">Float32 pixels: there is no integer projection without a tone map.</exception>
    public static byte[] Encode(Image image, ImageFormat format, SaveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (format == ImageFormat.Unknown)
            throw new ArgumentException("Unknown is not an encodable format.", nameof(format));
        if (image.FrameCount > 1)
            throw new NotSupportedException(
                $"The image has {image.FrameCount} frames and this build writes one; encoding it would drop every frame after the first. Select a frame with KeepFrames first.");
        options ??= SaveOptions.Default;

        var pixelFormat = image.Format.ToPixelFormat();
        EncodeOptions? encodeOptions = null;
        if (format == ImageFormat.Jpeg)
            encodeOptions = new JpegEncodeOptions { Quality = options.JpegQuality ?? 90 };
        else if (options.JpegQuality is not null)
            throw new ArgumentException("JpegQuality only applies to ImageFormat.Jpeg.", nameof(options));

        // Sdcb.SimdCodecs 2.0 encodes WebP lossily unless told otherwise; this is the knob that says otherwise.
        // Exact keeps the RGB under alpha 0, which the codec otherwise flattens: "lossless" should mean the bytes
        // come back unchanged, hidden pixels included.
        if (format == ImageFormat.Webp)
        {
            if (options.WebpLossless == true)
                encodeOptions = new WebpLosslessEncodeOptions { Exact = true };
        }
        else if (options.WebpLossless is not null)
        {
            throw new ArgumentException("WebpLossless only applies to ImageFormat.Webp.", nameof(options));
        }

        var frame = image[0];
        var view = frame.Pixels.View;
        return ImageCodecs.Encode(view.Span, image.Width, image.Height, pixelFormat, format,
            stride: view.Stride,
            options: encodeOptions,
            metadata: options.Metadata ?? image.Metadata,
            cancellationToken: cancellationToken);
    }

    private static void CopyDecoded(IDecodedImage decoded, PixelView destination)
    {
        var pixelCount = checked(decoded.Width * decoded.Height);
        switch (destination.Format)
        {
            case WorkingFormat.Rgba8:
                RequireIntegerSamples(decoded);
                PixelConverter.ConvertRow(decoded.Pixels, decoded.Channels, decoded.SampleFormat,
                    destination.Span, PixelFormat.Rgba32, pixelCount);
                break;

            case WorkingFormat.Rgba16:
                RequireIntegerSamples(decoded);
                PixelConverter.ConvertRow(decoded.Pixels, decoded.Channels, decoded.SampleFormat,
                    destination.Span, PixelFormat.Rgba64, pixelCount);
                break;

            default:
                ExpandToFloat(decoded, destination);
                break;
        }
    }

    private static void RequireIntegerSamples(IDecodedImage decoded)
    {
        if (decoded.SampleFormat == SampleFormat.Float32)
            throw new NotSupportedException(
                "Float32 samples have no integer projection without an explicit tone/stretch policy; load as Rgba32F and convert explicitly.");
    }

    // Container widening, exactly like IDecodedImage.ToFloats(): integer samples normalize to [0,1]
    // (0 -> 0, max -> exactly 1), float samples copy verbatim, gray broadcasts, missing alpha becomes 1.
    private static void ExpandToFloat(IDecodedImage decoded, PixelView destination)
    {
        var pixelCount = checked(decoded.Width * decoded.Height);
        var channels = decoded.Channels;
        var destinationFloats = MemoryMarshal.Cast<byte, float>(destination.Span)[..(pixelCount * 4)];
        var source = decoded.Pixels;

        switch (decoded.SampleFormat)
        {
            case SampleFormat.UInt8:
                for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
                {
                    float r = source[s];
                    float g, b, a;
                    switch (channels)
                    {
                        case 1: g = b = r; a = 1f; break;
                        case 2: g = b = r; a = source[s + 1]; break;
                        case 3: g = source[s + 1]; b = source[s + 2]; a = 1f; break;
                        default: g = source[s + 1]; b = source[s + 2]; a = source[s + 3]; break;
                    }

                    destinationFloats[d] = r / 255f;
                    destinationFloats[d + 1] = g / 255f;
                    destinationFloats[d + 2] = b / 255f;
                    destinationFloats[d + 3] = a / 255f;
                }
                break;

            case SampleFormat.UInt16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(source);
                for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
                {
                    float r = samples[s];
                    float g, b, a;
                    switch (channels)
                    {
                        case 1: g = b = r; a = 1f; break;
                        case 2: g = b = r; a = samples[s + 1]; break;
                        case 3: g = samples[s + 1]; b = samples[s + 2]; a = 1f; break;
                        default: g = samples[s + 1]; b = samples[s + 2]; a = samples[s + 3]; break;
                    }

                    destinationFloats[d] = r / 65535f;
                    destinationFloats[d + 1] = g / 65535f;
                    destinationFloats[d + 2] = b / 65535f;
                    destinationFloats[d + 3] = a / 65535f;
                }
                break;
            }

            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(source);
                for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
                {
                    var r = samples[s];
                    switch (channels)
                    {
                        case 1:
                            destinationFloats[d] = destinationFloats[d + 1] = destinationFloats[d + 2] = r;
                            destinationFloats[d + 3] = 1f;
                            break;
                        case 2:
                            destinationFloats[d] = destinationFloats[d + 1] = destinationFloats[d + 2] = r;
                            destinationFloats[d + 3] = samples[s + 1];
                            break;
                        case 3:
                            destinationFloats[d] = r;
                            destinationFloats[d + 1] = samples[s + 1];
                            destinationFloats[d + 2] = samples[s + 2];
                            destinationFloats[d + 3] = 1f;
                            break;
                        default:
                            destinationFloats[d] = r;
                            destinationFloats[d + 1] = samples[s + 1];
                            destinationFloats[d + 2] = samples[s + 2];
                            destinationFloats[d + 3] = samples[s + 3];
                            break;
                    }
                }
                break;
            }
        }
    }

    private static ImageFormat FormatFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => ImageFormat.Png,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFormat.Jpeg,
        ".bmp" or ".dib" => ImageFormat.Bmp,
        ".gif" => ImageFormat.Gif,
        ".webp" => ImageFormat.Webp,
        ".tif" or ".tiff" => ImageFormat.Tiff,
        ".qoi" => ImageFormat.Qoi,
        ".pbm" or ".pgm" or ".ppm" or ".pnm" or ".pam" => ImageFormat.Pnm,
        ".ico" => ImageFormat.Ico,
        ".cur" => ImageFormat.Cur,
        ".jp2" or ".j2k" or ".jpc" or ".jpf" => ImageFormat.Jpeg2000,
        ".jb2" or ".jbig2" => ImageFormat.Jbig2,
        _ => throw new NotSupportedException($"No encoder is registered for '{extension}'."),
    };
}
