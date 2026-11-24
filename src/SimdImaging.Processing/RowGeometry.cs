using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>Mirrors each row left to right as it passes: the row-local half of <c>FlipHorizontal</c>, streamable
/// because no row depends on any other.</summary>
internal sealed class FlipRowSource(IRowSource source) : IRowSource, IDisposable
{
    private readonly byte[] _row = new byte[source.Width * source.Format.BytesPerPixel()];
    private readonly int _rowBytes = source.Width * source.Format.BytesPerPixel();
    private readonly int _bytesPerPixel = source.Format.BytesPerPixel();
    private bool _disposed;

    public int Width => source.Width;

    public int Height => source.Height;

    public WorkingFormat Format => source.Format;

    public int ReadRows(Span<byte> destination, int stride, int maxRows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stride == 0)
            stride = _rowBytes;

        var copied = 0;
        while (copied < maxRows)
        {
            var read = source.ReadRows(_row, 0, 1);
            if (read <= 0)
                break;

            var target = destination.Slice(copied * stride, _rowBytes);
            for (var x = 0; x < Width; x++)
            {
                _row.AsSpan(x * _bytesPerPixel, _bytesPerPixel)
                    .CopyTo(target.Slice((Width - 1 - x) * _bytesPerPixel, _bytesPerPixel));
            }

            copied++;
        }

        return copied;
    }

    public void Dispose() => _disposed = true;
}

/// <summary>
/// Adds margins around each row as it passes, and whole background rows above and below: the row-local half of
/// <c>Pad</c>. Forward-streamable because the added rows do not depend on the source.
/// </summary>
internal sealed class PadRowSource : IRowSource, IDisposable
{
    private readonly IRowSource _source;
    private readonly int _left;
    private readonly int _top;
    private readonly int _right;
    private readonly RgbaColor _color;
    private readonly int _bytesPerPixel;
    private readonly int _sourceRowBytes;
    private readonly int _rowBytes;
    private readonly byte[] _row;
    private int _outRow;
    private bool _disposed;

    public PadRowSource(IRowSource source, int left, int top, int right, int bottom, RgbaColor color)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(left);
        ArgumentOutOfRangeException.ThrowIfNegative(top);
        ArgumentOutOfRangeException.ThrowIfNegative(right);
        ArgumentOutOfRangeException.ThrowIfNegative(bottom);

        _source = source;
        _left = left;
        _top = top;
        _right = right;
        _color = color;
        _bytesPerPixel = source.Format.BytesPerPixel();
        _sourceRowBytes = source.Width * _bytesPerPixel;
        _rowBytes = (source.Width + left + right) * _bytesPerPixel;
        _row = new byte[_sourceRowBytes];
        Height = source.Height + top + bottom;
    }

    public int Width => _source.Width + _left + _right;

    public int Height { get; }

    public WorkingFormat Format => _source.Format;

    public int ReadRows(Span<byte> destination, int stride, int maxRows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stride == 0)
            stride = _rowBytes;

        var copied = 0;
        while (copied < maxRows && _outRow < Height)
        {
            var target = destination.Slice(copied * stride, _rowBytes);
            if (_outRow < _top || _outRow >= _top + _source.Height)
            {
                DrawOps.FillRow(target, Width, _color, Format);
            }
            else
            {
                var read = _source.ReadRows(_row, 0, 1);
                if (read != 1)
                    throw new InvalidDataException(
                        $"The row source ended at output row {_outRow}; the pad needs {_source.Height} source rows.");

                if (_left > 0)
                    DrawOps.FillRow(target[..(_left * _bytesPerPixel)], _left, _color, Format);
                _row.AsSpan(0, _sourceRowBytes)
                    .CopyTo(target.Slice(_left * _bytesPerPixel, _sourceRowBytes));
                if (_right > 0)
                    DrawOps.FillRow(target[((_left * _bytesPerPixel) + _sourceRowBytes)..], _right, _color, Format);
            }

            _outRow++;
            copied++;
        }

        return copied;
    }

    public void Dispose() => _disposed = true;
}
