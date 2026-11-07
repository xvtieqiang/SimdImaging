using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Abstractions;

/// <summary>What <c>Image.Load</c> asks of the decoder and of
/// the buffer it fills.</summary>
public sealed record LoadOptions
{
    /// <summary>Defaults: native depth, whole image, full size, shared pool, 268 MPix cap.</summary>
    public static LoadOptions Default { get; } = new();

    /// <summary>
    /// The working format to produce. Null (the default) follows the decoded sample type: 8-bit →
    /// <see cref="WorkingFormat.Rgba8"/>, 16-bit → <see cref="WorkingFormat.Rgba16"/>, float →
    /// <see cref="WorkingFormat.Rgba32F"/>. Asking for a narrower format is allowed and happens once, through
    /// <c>PixelConverter</c>; asking for <see cref="WorkingFormat.Rgba32F"/> from an integer source is allowed
    /// too, but is a widening, not a linearization.
    /// </summary>
    public WorkingFormat? Format { get; init; }

    /// <summary>A region to decode, in stored (pre-orientation) coordinates; null decodes the whole image.
    /// Codecs without native region support decode the full image and crop — the contract SimdCodecs defines.</summary>
    public PixelRect? Region { get; init; }

    /// <summary>Decode at 1/<c>ScaleDenominator</c>: 1, 2, 4 or 8. Codecs that cannot do it natively throw
    /// <see cref="NotSupportedException"/> — the library never pretends by decoding full size and shrinking.</summary>
    public int? ScaleDenominator { get; init; }

    /// <summary>Where the pixel buffer is rented from. Null uses <see cref="ArrayPixelPool.Shared"/>.
    /// (The codec's own scratch memory is separate and stays with the codec.)</summary>
    public IPixelPool? Pool { get; init; }

    /// <summary>The largest image this call may produce, in pixels. Guards against decompression bombs before
    /// a buffer is allocated.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to a non-positive value.</exception>
    public long MaxPixels
    {
        get => _maxPixels;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxPixels = value;
        }
    }

    private readonly long _maxPixels = 1L << 28;   // 268 MPix ≈ 1 GiB at Rgba8
}

/// <summary>What <c>Image.Save</c> writes, beyond the pixels.</summary>
public sealed record SaveOptions
{
    /// <summary>Defaults: encoder default quality, the image's own metadata.</summary>
    public static SaveOptions Default { get; } = new();

    /// <summary>JPEG quality, 1..100. Null uses the encoder default (90). Setting it for a non-JPEG format is
    /// an error, not a silently ignored field.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Set outside 1..100.</exception>
    public int? JpegQuality
    {
        get => _jpegQuality;
        init
        {
            if (value is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(value), value, "JPEG quality must be 1..100.");
            _jpegQuality = value;
        }
    }

    private readonly int? _jpegQuality;

    /// <summary>The metadata blocks to write (Exif / XMP / ICC). Null passes the image's own blocks through,
    /// which is what a transcode wants.</summary>
    public ImageMetadata? Metadata { get; init; }
}
