using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Abstractions;

/// <summary>
/// An owned pixel buffer: width, height, row stride, working format and the memory behind them. Buffers come
/// from a pool (or wrap memory the caller owns) and are always released through <see cref="Dispose"/>.
/// <para>
/// It also implements <see cref="IMemoryOwner{T}"/> so a tightly packed buffer can be handed to
/// <c>RasterImage.Wrap</c> or an encoder session without a copy. <see cref="IMemoryOwner{T}.Memory"/> throws
/// when the buffer is not tight: making a non-tight buffer pretend to be contiguous is exactly the kind of
/// silent lie this library refuses.
/// </para>
/// </summary>
public sealed class PixelBuffer : IDisposable, IMemoryOwner<byte>
{
    private readonly Memory<byte> _memory;
    private readonly int _offset;
    private readonly int _length;      // tightly packed byte count
    private IMemoryOwner<byte>? _owner;
    private int _disposed;

    private PixelBuffer(IMemoryOwner<byte>? owner, Memory<byte> memory, int offset,
        int width, int height, int stride, WorkingFormat format)
    {
        _owner = owner;
        _memory = memory;
        _offset = offset;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        _length = checked(stride * height);
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

    /// <summary>True when rows are back to back (<see cref="Stride"/> equals <see cref="RowBytes"/>), so the
    /// whole buffer is contiguous and can be exposed as one <see cref="Memory{T}"/>.</summary>
    public bool IsTight => Stride == RowBytes;

    /// <summary>A view over the whole buffer.</summary>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public PixelView View
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new PixelView(_memory, _offset, Width, Height, Stride, Format);
        }
    }

    /// <summary>
    /// Rents a tightly packed buffer from <paramref name="pool"/> (or <see cref="ArrayPixelPool.Shared"/>).
    /// Contents are undefined; <c>Image.Create</c> clears them.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive, or the size overflows.</exception>
    public static PixelBuffer Rent(int width, int height, WorkingFormat format,
        IPixelPool? pool = null, bool clear = false)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        var stride = checked(width * format.BytesPerPixel());
        var length = checked(stride * height);
        var owner = (pool ?? ArrayPixelPool.Shared).Rent(length);
        if (clear)
            owner.Memory.Span.Slice(0, length).Clear();
        return new PixelBuffer(owner, owner.Memory, 0, width, height, stride, format);
    }

    /// <summary>
    /// Takes over <paramref name="owner"/>: the buffer reads and writes the first
    /// <c>stride × height</c> bytes and disposes the owner when it is disposed.
    /// </summary>
    /// <exception cref="ArgumentException">The memory is too small for the requested layout.</exception>
    public static PixelBuffer Wrap(IMemoryOwner<byte> owner, int width, int height, int stride, WorkingFormat format)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var rowBytes = checked(width * format.BytesPerPixel());
        if (stride < rowBytes)
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"Stride must be at least {rowBytes} bytes.");
        if ((long)stride * height > owner.Memory.Length)
            throw new ArgumentException($"Memory of {owner.Memory.Length} bytes is too small for {width}x{height} at stride {stride}.", nameof(owner));
        return new PixelBuffer(owner, owner.Memory, 0, width, height, stride, format);
    }

    /// <summary>
    /// Copies this buffer into <paramref name="destination"/> in <paramref name="format"/> using
    /// <see cref="PixelConverter"/> — the one conversion path in the library, so a caller never meets a second
    /// set of layout or rounding rules.
    /// </summary>
    /// <param name="destination">The destination; rows are <paramref name="stride"/> bytes apart (0: tightly packed).</param>
    /// <param name="stride">Destination row stride in bytes; 0 means <c>width × destination bytes per pixel</c>.</param>
    /// <param name="format">The destination layout.</param>
    /// <returns>False when the destination does not fit.</returns>
    /// <exception cref="NotSupportedException">This buffer or the destination is <see cref="WorkingFormat.Rgba32F"/>.</exception>
    public bool TryCopyTo(Span<byte> destination, int stride, PixelFormat format)
    {
        var view = View;   // throws when disposed
        var rowBytes = checked(Width * format.BytesPerPixel);
        if (stride == 0)
            stride = rowBytes;
        if (stride < rowBytes || destination.Length < (long)stride * (Height - 1) + rowBytes)
            return false;

        var sourceSample = Format.ToSampleFormat();
        for (var y = 0; y < Height; y++)
            PixelConverter.ConvertRow(view.Row(y), 4, sourceSample, destination.Slice(y * stride, rowBytes), format, Width);
        return true;
    }

    /// <summary>Copies the buffer into a new, tightly packed buffer the caller owns.</summary>
    public PixelBuffer Clone(IPixelPool? pool = null)
    {
        var copy = Rent(Width, Height, Format, pool);
        for (var y = 0; y < Height; y++)
            View.Row(y).CopyTo(copy.View.Row(y));
        return copy;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The buffer is not tightly packed.</exception>
    Memory<byte> IMemoryOwner<byte>.Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!IsTight)
                throw new InvalidOperationException(
                    $"A {Width}x{Height} buffer with stride {Stride} is not tightly packed ({RowBytes} bytes per row); its rows cannot be exposed as one contiguous block.");
            return _memory.Slice(_offset, _length);
        }
    }

    /// <summary>Returns the memory to its pool (or disposes the wrapped owner). Safe to call more than once;
    /// afterwards <see cref="View"/> throws.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }
}
