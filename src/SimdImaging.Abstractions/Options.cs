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

    /// <summary>
    /// Turn the pixels as the picture is shown, applying the EXIF / TIFF Orientation tag during the decode
    /// (SimdCodecs <c>DecodeOptions.ApplyOrientation</c>). Off by default: a default load reports the tag in
    /// <c>Image.Info.Orientation</c> and leaves the stored pixels alone.
    /// <para>
    /// When on, orientations 5..8 exchange width and height, <see cref="Region"/> is in <em>displayed</em>
    /// coordinates, <c>Image.Info.Orientation</c> reads <see cref="ImageOrientation.Normal"/>, and the metadata
    /// carries a copy of the Exif block with its Orientation reset — so saving the image does not turn it twice.
    /// The turning itself is lossless: bytes move, nothing is resampled.
    /// </para>
    /// </summary>
    public bool ApplyOrientation { get; init; }

    /// <summary>A region to decode, in stored (pre-orientation) coordinates, or in displayed coordinates when
    /// <see cref="ApplyOrientation"/> is set; null decodes the whole image.
    /// Codecs without native region support decode the full image and crop — the contract SimdCodecs defines.</summary>
    public PixelRect? Region { get; init; }

    /// <summary>
    /// Decode at 1/<c>ScaleDenominator</c> in each direction: a power of two from 1 to 2^30. The output is
    /// <c>⌈width / d⌉ × ⌈height / d⌉</c> whatever the codec does natively — one that scales natively (JPEG up to 8,
    /// JPEG 2000 to its decomposition levels) decodes at its largest native step and box-filters the difference,
    /// which costs a second pass. <see cref="PreferNativeScale"/> trades that exactness for the faster path.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to a value that is not a power of two from 1 to 2^30.</exception>
    public int? ScaleDenominator
    {
        get => _scaleDenominator;
        init
        {
            if (value is { } d && (d <= 0 || d > 1 << 30 || (d & (d - 1)) != 0))
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "The scale denominator is a power of two from 1 to 2^30.");
            _scaleDenominator = value;
        }
    }

    private readonly int? _scaleDenominator;

    /// <summary>
    /// Take the reduction the codec produces natively instead of the exact <see cref="ScaleDenominator"/>, skipping
    /// the box-filter pass the exact size costs. Off by default; only meaningful when <see cref="ScaleDenominator"/>
    /// is above 1.
    /// <para>
    /// The output is normally <em>larger</em> than <c>⌈width / ScaleDenominator⌉</c>: asking for 1/16 of a JPEG
    /// that scales natively to 1/8 gives 1/8. A codec whose only native scale is 1 is left alone — taking it would
    /// decode full size, which is worse on every axis. Read <c>Image.Width</c> for what actually came back: this
    /// trades an exact size for a cheaper decode.
    /// </para>
    /// <para>
    /// Not combinable with <see cref="Region"/>: a region is expressed in the pixels of the scale it is given at,
    /// and negotiating a different scale would silently reinterpret its coordinates.
    /// </para>
    /// </summary>
    public bool PreferNativeScale { get; init; }

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

    /// <summary>
    /// Write WebP losslessly instead of the encoder's default lossy mode. Null leaves the choice to the codec,
    /// which means lossy — <see cref="ImageFormat.Webp"/> is the only format this affects, and setting it for
    /// any other is an error rather than a silently ignored field.
    /// <para>
    /// Lossless here means byte-exact: the codec's own <c>Exact</c> flag is turned on as well, so the RGB under a
    /// fully transparent pixel comes back unchanged instead of being flattened. Measured on this library's round
    /// trip: the default lossy encode changes 72.8% of the samples of a synthetic 64×64 gradient; lossless changes
    /// none.
    /// </para>
    /// </summary>
    public bool? WebpLossless { get; init; }

    /// <summary>The metadata blocks to write (Exif / XMP / ICC). Null passes the image's own blocks through,
    /// which is what a transcode wants.
    /// <para>
    /// JPEG is the exception: its encoder takes XMP from <c>JpegEncodeOptions.Xmp</c> (a structured packet), so an
    /// XMP block handed over here is dropped rather than written. Exif and ICC travel normally. Verified against
    /// Sdcb.SimdCodecs 2.0.0 (JPEG round trip keeps Exif, loses Xmp; PNG and WebP keep both).
    /// </para>
    /// </summary>
    public ImageMetadata? Metadata { get; init; }
}
