using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tiff;
using SimdImaging.Abstractions;

namespace SimdImaging;

/// <summary>
/// A TIFF read one strip or tile at a time: the only format this build can read as arbitrary rectangles without
/// touching the rest of the file. Every segment that intersects the requested rectangle is decoded (the codec does
/// that much) and converted into the working format; nothing else is read.
/// <para>
/// The accepted layouts are the ones whose pixel meaning is fully described by the public layout: 8- or 16-bit
/// unsigned samples, 1 or 3 samples per pixel, MinIsBlack / MinIsWhite / RGB. Anything else is refused by name —
/// in particular 4 samples per pixel, because <c>TiffPageLayout</c> does not expose <c>ExtraSamples</c> and
/// associated (premultiplied) alpha cannot be told from straight alpha.
/// </para>
/// </summary>
internal sealed class TiffRegionSource : IRegionSource, IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly TiffPageLayout _layout;
    private readonly WorkingFormat _format;
    private readonly SampleFormat _sampleFormat;
    private readonly bool _invert;
    private byte[] _segment = [];
    private bool _disposed;

    private TiffRegionSource(Stream stream, bool ownsStream, TiffPageLayout layout, WorkingFormat format)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _layout = layout;
        _format = format;
        _sampleFormat = layout.BitsPerSample == 16 ? SampleFormat.UInt16 : SampleFormat.UInt8;
        _invert = layout.Photometric == TiffPhotometric.MinIsWhite;
    }

    public int Width => _layout.Width;

    public int Height => _layout.Height;

    public WorkingFormat Format => _format;

    /// <summary>The page geometry the source was opened with.</summary>
    internal TiffPageLayout Layout => _layout;

    /// <summary>Bytes the largest segment of the page needs, so a caller can refuse before allocating it.</summary>
    internal long MaxSegmentBytes
    {
        get
        {
            long largest = 0;
            for (var i = 0; i < _layout.SegmentCount; i++)
                largest = Math.Max(largest, _layout.SegmentBytes(i));
            return largest;
        }
    }

    /// <summary>Opens a page and validates the layout. Nothing is decoded.</summary>
    /// <exception cref="NotSupportedException">The page layout is not one this slice reads; the message names the
    /// offending field.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a TIFF this codec can read.</exception>
    public static TiffRegionSource Open(string path, int pageIndex, WorkingFormat? format, long maxSegmentBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        try
        {
            return Open(stream, ownsStream: true, pageIndex, format, maxSegmentBytes);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens a page from a seekable stream positioned at the TIFF header. The TIFF offsets are absolute,
    /// so the header has to be at position 0.</summary>
    public static TiffRegionSource Open(Stream stream, int pageIndex, WorkingFormat? format, long maxSegmentBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Open(stream, ownsStream: false, pageIndex, format, maxSegmentBytes);
    }

    private static TiffRegionSource Open(Stream stream, bool ownsStream, int pageIndex, WorkingFormat? format,
        long maxSegmentBytes)
    {
        if (!stream.CanSeek || !stream.CanRead)
            throw new NotSupportedException("A TIFF region read needs a readable, seekable stream.");
        if (stream.Position != 0)
            throw new NotSupportedException(
                "A TIFF region read needs the image at position 0: TIFF offsets are absolute, so an image embedded at "
                + "an offset cannot be read without rebasing every offset.");

        stream.Position = 0;
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != 4 || !LooksLikeTiff(signature))
            throw new NotSupportedException(
                "Only a TIFF has a region source in this build: the bytes do not start with a TIFF header "
                + "(II*\0, MM\0*, II+\0 or MM\0+). Use Load for every other format.");

        var layout = TiffReader.ReadLayout(stream, pageIndex);
        var working = format ?? (layout.BitsPerSample == 16 ? WorkingFormat.Rgba16 : WorkingFormat.Rgba8);
        Validate(layout, working, maxSegmentBytes);

        stream.Position = 0;
        return new TiffRegionSource(stream, ownsStream, layout, working);
    }

    /// <summary>True for a classic or BigTIFF header in either byte order. Shared with the streaming path, which has
    /// to know whether a prefix is a TIFF before it decides how to read its depth.</summary>
    internal static bool LooksLikeTiff(ReadOnlySpan<byte> signature)
    {
        // A prefix longer than four bytes is the normal input here; comparing the whole span would answer false for
        // every real call and push the streaming path into a one-megabyte sniff.
        if (signature.Length < 4)
            return false;

        var head = signature[..4];
        return head.SequenceEqual(new byte[] { 0x49, 0x49, 0x2A, 0x00 })
            || head.SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })
            || head.SequenceEqual(new byte[] { 0x49, 0x49, 0x2B, 0x00 })
            || head.SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2B });
    }

    private static void Validate(TiffPageLayout layout, WorkingFormat format, long maxSegmentBytes)
    {
        if (format == WorkingFormat.Rgba32F)
            throw new NotSupportedException(
                "Rgba32F is not a region target: an integer TIFF sample has no float projection without a tone "
                + "policy. Ask for Rgba8 or Rgba16.");

        if (layout.Photometric is not (TiffPhotometric.MinIsBlack or TiffPhotometric.MinIsWhite or TiffPhotometric.Rgb))
            throw new NotSupportedException(
                $"A TIFF page with Photometric={layout.Photometric} is not read as a region: only MinIsBlack, "
                + "MinIsWhite and Rgb are. Palette, CMYK, YCbCr, CIELab and transparency masks need the codec's own "
                + "colour conversion, which is not part of the public layout.");

        if (layout.SamplesPerPixel is not (1 or 3))
            throw new NotSupportedException(
                $"A TIFF page with {layout.SamplesPerPixel} samples per pixel is not read as a region: the layout does "
                + "not expose ExtraSamples, so a 2- or 4-sample page cannot be told apart from one with associated "
                + "(premultiplied) alpha. Re-encode without the extra sample first.");

        if (layout.SampleFormat != TiffSampleFormat.Uint)
            throw new NotSupportedException(
                $"A TIFF page with SampleFormat={layout.SampleFormat} is not read as a region: only unsigned integer "
                + "samples are.");

        if (layout.BitsPerSample is not (8 or 16))
            throw new NotSupportedException(
                $"A TIFF page with {layout.BitsPerSample}-bit samples is not read as a region: only 8 and 16 are "
                + "(1/2/4-bit pages are reported as 8 by the codec and do go through this path).");

        var largest = 0L;
        for (var i = 0; i < layout.SegmentCount; i++)
            largest = Math.Max(largest, layout.SegmentBytes(i));
        if (largest > maxSegmentBytes)
            throw new NotSupportedException(
                $"The page's largest strip/tile is {largest} bytes, above the {maxSegmentBytes}-byte ceiling in "
                + "RegionOptions.MaxSegmentBytes. The codec decodes a segment as a unit; re-encode the page with a "
                + "smaller RowsPerStrip (or tile) to read it as a region.");
    }

    public void ReadRegion(PixelRect region, PixelView destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!region.FitsIn(Width, Height) || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"The region {region} does not lie inside {Width}x{Height}.", nameof(region));
        if (destination.Width != region.Width || destination.Height != region.Height || destination.Format != _format)
            throw new ArgumentException("The destination does not match the region's size or format.", nameof(destination));

        var bytesPerPixel = _format.BytesPerPixel();
        var first = _layout.SegmentAt(region.X, region.Y);
        var last = _layout.SegmentAt(region.X + region.Width - 1, region.Y + region.Height - 1);

        // Row-major indices: the segments between the two corners include ones to the left and right of a middle
        // row, and the intersection test is what skips them.
        for (var index = first; index <= last; index++)
        {
            var segment = _layout.SegmentRectangle(index);
            var hit = Intersect(segment, region);
            if (hit.Width <= 0 || hit.Height <= 0)
                continue;

            var size = _layout.SegmentBytes(index);
            if (_segment.Length < size)
                _segment = new byte[size];

            var slice = _segment.AsSpan(0, size);
            var read = TiffReader.ReadSegment(_stream, _layout, index, slice);
            if (read != segment)
                throw new InvalidDataException(
                    $"The codec reported segment {index} as {segment} and then decoded {read}; the layout and the "
                    + "decoder disagree about the same page.");

            var segmentRowBytes = segment.Width * _layout.BytesPerPixel;
            for (var y = hit.Y; y < hit.Y + hit.Height; y++)
            {
                var sourceOffset = (((y - segment.Y) * segment.Width) + (hit.X - segment.X)) * _layout.BytesPerPixel;
                var source = slice.Slice(sourceOffset, hit.Width * _layout.BytesPerPixel);
                var row = destination.Row(y - region.Y);
                var target = row.Slice((hit.X - region.X) * bytesPerPixel, hit.Width * bytesPerPixel);
                PixelConverter.ConvertRow(source, _layout.SamplesPerPixel, _sampleFormat, target,
                    _format.ToPixelFormat(), hit.Width);
                if (_invert)
                    Invert(target, _format);
            }
        }
    }

    /// <summary>MinIsWhite is the negative of the stored sample. Inverting after the conversion is exactly what the
    /// whole-image path produces: <c>~v</c> for 8→8, <c>65535−v</c> for 16→16 and, across widths,
    /// <c>(65535−v)&gt;&gt;8 = 255−(v&gt;&gt;8)</c>.</summary>
    private static void Invert(Span<byte> row, WorkingFormat format)
    {
        if (format == WorkingFormat.Rgba8)
        {
            for (var i = 0; i < row.Length; i += 4)
            {
                row[i] = (byte)(255 - row[i]);
                row[i + 1] = (byte)(255 - row[i + 1]);
                row[i + 2] = (byte)(255 - row[i + 2]);
            }

            return;
        }

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(row);
        for (var i = 0; i < samples.Length; i += 4)
        {
            samples[i] = (ushort)(65535 - samples[i]);
            samples[i + 1] = (ushort)(65535 - samples[i + 1]);
            samples[i + 2] = (ushort)(65535 - samples[i + 2]);
        }
    }

    private static PixelRect Intersect(TiffSegmentRect a, PixelRect b) =>
        Intersect(new PixelRect(a.X, a.Y, a.Width, a.Height), b);

    private static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        var x0 = Math.Max(a.X, b.X);
        var y0 = Math.Max(a.Y, b.Y);
        var x1 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return x1 <= x0 || y1 <= y0 ? default : new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsStream)
            _stream.Dispose();
    }
}
