using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Abstractions;

/// <summary>
/// A window onto pixel storage: a base memory, a byte offset, a row stride and a working format. This is the
/// only shape operators accept, which is what makes tiles, slices and sub-regions zero-copy — a view is a
/// pointer-sized value, not a buffer.
/// <para>
/// Rows are <see cref="Stride"/> bytes apart and a row is <c>Width × Format.BytesPerPixel()</c> bytes; bytes
/// between the end of a row and the next are padding and are never read or written.
/// </para>
/// </summary>
public readonly struct PixelView
{
    private readonly Memory<byte> _memory;
    private readonly int _offset;

    /// <summary>Creates a view over <paramref name="memory"/>.</summary>
    /// <param name="memory">The backing storage.</param>
    /// <param name="offset">Byte offset of the first pixel.</param>
    /// <param name="width">Pixels per row (&gt; 0).</param>
    /// <param name="height">Rows (&gt; 0).</param>
    /// <param name="stride">Bytes from one row to the next, at least <c>width × bytesPerPixel</c>.</param>
    /// <param name="format">The pixel layout.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive, or <paramref name="stride"/> is too small.</exception>
    /// <exception cref="ArgumentException">The view does not fit in <paramref name="memory"/>.</exception>
    public PixelView(Memory<byte> memory, int offset, int width, int height, int stride, WorkingFormat format)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must not be negative.");

        var itemSize = format.BytesPerPixel();
        var rowBytes = checked(width * itemSize);
        if (stride < rowBytes)
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"Stride must be at least {rowBytes} bytes for {width} pixels of {format}.");
        if ((long)offset + (long)stride * height > memory.Length)
            throw new ArgumentException($"A {width}x{height} {format} view at offset {offset} with stride {stride} does not fit in {memory.Length} bytes.", nameof(memory));

        _memory = memory;
        _offset = offset;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
    }

    /// <summary>Pixels per row.</summary>
    public int Width { get; }

    /// <summary>Rows.</summary>
    public int Height { get; }

    /// <summary>Bytes from one row to the next.</summary>
    public int Stride { get; }

    /// <summary>The pixel layout.</summary>
    public WorkingFormat Format { get; }

    /// <summary>Bytes of one row's pixels, excluding padding.</summary>
    public int RowBytes => Width * Format.BytesPerPixel();

    /// <summary>The view's bytes: <c>Height × Stride</c>, including row padding.</summary>
    public Span<byte> Span => _memory.Span.Slice(_offset, checked(Stride * Height));

    /// <summary>The view's bytes as a <see cref="Memory{T}"/>.</summary>
    public Memory<byte> Memory => _memory.Slice(_offset, checked(Stride * Height));

    /// <summary>One row's pixels, excluding padding.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the view.</exception>
    public Span<byte> Row(int y)
    {
        if ((uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(y), y, $"Row must be 0..{Height - 1}.");
        return _memory.Span.Slice(_offset + y * Stride, RowBytes);
    }

    /// <summary>A zero-copy sub-view. The stride is preserved, so the result is usually not tightly packed.</summary>
    /// <exception cref="ArgumentException"><paramref name="rect"/> does not lie inside this view.</exception>
    public PixelView Slice(PixelRect rect)
    {
        if (!rect.FitsIn(Width, Height))
            throw new ArgumentException($"The region {rect} does not fit in {Width}x{Height}.", nameof(rect));
        return new PixelView(_memory, _offset + rect.Y * Stride + rect.X * Format.BytesPerPixel(),
            rect.Width, rect.Height, Stride, Format);
    }
}
