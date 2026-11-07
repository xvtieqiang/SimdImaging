using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Abstractions;

/// <summary>
/// The three layouts an <c>Image</c> can hold. One working format for every operator is what keeps
/// the kernel matrix at <c>3 × algorithms</c> instead of <c>layouts (6) × depths (2) × alpha (2) × algorithms</c>:
/// a decoder's native channels and sample width are folded into RGBA exactly once, at the codec boundary.
/// <para>
/// The three are the same four channels at 8 bits, 16 bits and 32-bit float. Values carry the image's
/// <see cref="ImageInfo.ColorEncoding"/>: a float buffer is a container, never an implicit linearization.
/// </para>
/// </summary>
public enum WorkingFormat : byte
{
    /// <summary>4 bytes/pixel: R, G, B, A, 8 bits each.</summary>
    Rgba8 = 0,

    /// <summary>8 bytes/pixel: R, G, B, A, 16 bits each.</summary>
    Rgba16 = 1,

    /// <summary>16 bytes/pixel: R, G, B, A, 32-bit IEEE floats. Not an implicit colour-space change.</summary>
    Rgba32F = 2,
}

/// <summary>Conversions, sizes and the codec-boundary mapping for <see cref="WorkingFormat"/>.</summary>
public static class WorkingFormatExtensions
{
    /// <summary>Bytes of one channel sample: 1, 2 or 4.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined member.</exception>
    public static int BytesPerSample(this WorkingFormat format) => format switch
    {
        WorkingFormat.Rgba8 => 1,
        WorkingFormat.Rgba16 => 2,
        WorkingFormat.Rgba32F => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a defined working format."),
    };

    /// <summary>Bytes of one pixel: 4, 8 or 16 (always four channels).</summary>
    public static int BytesPerPixel(this WorkingFormat format) => 4 * format.BytesPerSample();

    /// <summary>The <see cref="SampleFormat"/> of one channel.</summary>
    public static SampleFormat ToSampleFormat(this WorkingFormat format) => format switch
    {
        WorkingFormat.Rgba8 => SampleFormat.UInt8,
        WorkingFormat.Rgba16 => SampleFormat.UInt16,
        WorkingFormat.Rgba32F => SampleFormat.Float32,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a defined working format."),
    };

    /// <summary>The working format a decoder's sample type maps to: 8-bit → <see cref="WorkingFormat.Rgba8"/>,
    /// 16-bit → <see cref="WorkingFormat.Rgba16"/>, float → <see cref="WorkingFormat.Rgba32F"/>.</summary>
    public static WorkingFormat FromSampleFormat(SampleFormat sample) => sample switch
    {
        SampleFormat.UInt8 => WorkingFormat.Rgba8,
        SampleFormat.UInt16 => WorkingFormat.Rgba16,
        SampleFormat.Float32 => WorkingFormat.Rgba32F,
        _ => throw new ArgumentOutOfRangeException(nameof(sample), sample, "Not a defined sample format."),
    };

    /// <summary>The <see cref="PixelFormat"/> with the same channels and depth, for <c>PixelConverter</c>
    /// and the encoders.</summary>
    /// <exception cref="NotSupportedException">Float samples: there is no float <see cref="PixelFormat"/>.</exception>
    public static PixelFormat ToPixelFormat(this WorkingFormat format) => format switch
    {
        WorkingFormat.Rgba8 => PixelFormat.Rgba32,
        WorkingFormat.Rgba16 => PixelFormat.Rgba64,
        _ => throw new NotSupportedException(
            "Float32 samples have no integer PixelFormat. Apply a tone map or Linearize() to 8/16 bits before encoding."),
    };
}
