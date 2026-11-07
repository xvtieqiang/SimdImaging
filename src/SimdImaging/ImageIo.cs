using System.Runtime.InteropServices;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using SimdImaging.Abstractions;

namespace SimdImaging;

public sealed partial class Image
{
    /// <summary>Reads and decodes an image file.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a recognised, decodable image.</exception>
    /// <exception cref="NotSupportedException">The codec cannot honour <see cref="LoadOptions.ScaleDenominator"/>.</exception>
    /// <exception cref="InvalidOperationException">The output would exceed <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Image Load(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Load(File.ReadAllBytes(path), options, cancellationToken);
    }

    /// <summary>Decodes an image held in memory.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a recognised, decodable image.</exception>
    /// <exception cref="NotSupportedException">The codec cannot honour <see cref="LoadOptions.ScaleDenominator"/>, or the
    /// file has more than one frame (multi-frame lands with the frame reader).</exception>
    /// <exception cref="InvalidOperationException">The output would exceed <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Image Load(ReadOnlySpan<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
            throw new InvalidDataException("Empty input.");
        options ??= LoadOptions.Default;

        if (!SimdCodecs.TryReadInfo(data, out var info))
            throw new InvalidDataException("The stream is not a recognised image format.");

        var decodeOptions = new DecodeOptions
        {
            Region = options.Region,
            ScaleDenominator = options.ScaleDenominator ?? 1,
        };

        var (outputWidth, outputHeight) = decodeOptions.GetOutputSize(info.Width, info.Height);
        var outputPixels = (long)outputWidth * outputHeight;
        if (outputPixels > options.MaxPixels)
            throw new InvalidOperationException(
                $"The decoded image would be {outputWidth}x{outputHeight} = {outputPixels} pixels, above the {options.MaxPixels} pixel limit.");

        // An animation or a multi-page document needs the session: it owns the frame reader and the compositing
        // state. A still keeps the zero-copy span path.
        if (info.FrameCount > 1)
            return LoadFrames(data.ToArray(), options, decodeOptions, info, cancellationToken);

        if (!SimdCodecs.TryDecode(data, out var decoded, decodeOptions, cancellationToken))
            throw new InvalidDataException("The header was recognised but the pixel data could not be decoded.");

        using (decoded)
        {
            var format = options.Format ?? WorkingFormatExtensions.FromSampleFormat(decoded.SampleFormat);
            var buffer = PixelBuffer.Rent(decoded.Width, decoded.Height, format, options.Pool);
            try
            {
                CopyDecoded(decoded, buffer.View);
                var outputInfo = new ImageInfo(decoded.Width, decoded.Height, 4, format.ToSampleFormat())
                {
                    Format = info.Format,
                    BitDepth = info.BitDepth,
                    Orientation = info.Orientation,
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
    /// Decodes every frame of an animation or a multi-page document. The codec hands back composited canvases,
    /// so frame <c>i</c> is what a viewer shows at that point — this method only converts and stores them.
    /// </summary>
    private static Image LoadFrames(ReadOnlyMemory<byte> data, LoadOptions options, DecodeOptions decodeOptions,
        ImageInfo info, CancellationToken cancellationToken)
    {
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

                var buffer = PixelBuffer.Rent(decoded.Width, decoded.Height, format.Value, options.Pool);
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
            Orientation = info.Orientation,
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

    /// <summary>Encodes the image and writes it to <paramref name="path"/>. The format comes from the extension.</summary>
    /// <exception cref="NotSupportedException">The extension names no format this build can write, or the image has
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

        var frame = image[0];
        var view = frame.Pixels.View;
        return SimdCodecs.Encode(view.Span, image.Width, image.Height, pixelFormat, format,
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
