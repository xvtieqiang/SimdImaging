using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>
/// Presents a rectangle of another row source as its own rows: skips the rows above it, takes only the columns inside
/// it, and stops after its height. The pull-side counterpart of the crop sink the region plan used to need, and the
/// piece that lets "plan over a padded window → resize" stay a chain of sources.
/// </summary>
internal sealed class CropRowSource : IRowSource, IDisposable
{
    private readonly IRowSource _source;
    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bytesPerPixel;
    private readonly int _rowBytes;
    private readonly WorkingBuffer _band;
    private IDisposable? _workspaceLease;

    private int _loaded;    // rows consumed from the inner source
    private int _bandRows;  // valid rows in the band
    private int _bandPos;   // next row of the band to hand out
    private int _served;    // rows handed out
    private bool _disposed;

    public CropRowSource(IRowSource source, int x, int y, int width, int height, IPixelPool? pool = null,
        ExecuteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
            (long)x + width > source.Width || (long)y + height > source.Height)
            throw new ArgumentException(
                $"The crop ({x},{y},{width},{height}) does not lie inside {source.Width}x{source.Height}.");

        _source = source;
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _bytesPerPixel = source.Format.BytesPerPixel();
        _rowBytes = checked(width * _bytesPerPixel);
        options ??= new ExecuteOptions();
        var bandRows = (int)Math.Min(256L, (long)height + 1);
        var bytes = checked((long)source.Width * bandRows * _bytesPerPixel);
        if (options.MaxWorkingBytes > 0 && bytes > options.MaxWorkingBytes)
            throw new InvalidOperationException(
                $"The crop row window needs {bytes} bytes, above ExecuteOptions.MaxWorkingBytes = " +
                $"{options.MaxWorkingBytes}.");

        _workspaceLease = options.Workspace?.Reserve(bytes, "crop row window");
        try
        {
            _band = WorkingBuffer.Rent(source.Width, bandRows, source.Format, pool);
        }
        catch
        {
            _workspaceLease?.Dispose();
            _workspaceLease = null;
            throw;
        }
    }

    public int Width => _width;

    public int Height => _height;

    public WorkingFormat Format => _source.Format;

    public int ReadRows(Span<byte> destination, int stride, int maxRows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stride == 0)
            stride = _rowBytes;

        var copied = 0;
        while (copied < maxRows && _served < _height)
        {
            if (_bandPos >= _bandRows)
            {
                if (!FillBand())
                    break;
            }

            var take = Math.Min(Math.Min(maxRows - copied, _bandRows - _bandPos), _height - _served);
            for (var y = 0; y < take; y++)
            {
                _band.View.Row(_bandPos + y)
                    .Slice(_x * _bytesPerPixel, _rowBytes)
                    .CopyTo(destination.Slice((copied + y) * stride, _rowBytes));
            }

            _bandPos += take;
            _served += take;
            copied += take;
        }

        return copied;
    }

    private bool FillBand()
    {
        var bandStart = _loaded;
        var cropEnd = _y + _height;
        if (bandStart >= cropEnd)
            return false;

        var want = Math.Min(_band.Height, cropEnd - bandStart);
        var view = _band.View.Slice(new PixelRect(0, 0, _source.Width, want));
        var read = _source.ReadRows(view.Span, view.Stride, want);
        if (read <= 0)
        {
            if (bandStart >= _y)
                throw new InvalidDataException(
                    $"The row source ended at row {bandStart}; the crop needs {cropEnd}.");
            return false;
        }

        _loaded += read;

        // Rows above the crop are consumed and skipped; a band that straddles the top yields only its lower part.
        var skip = Math.Max(0, _y - bandStart);
        _bandPos = Math.Min(skip, read);
        _bandRows = Math.Min(read, cropEnd - bandStart);
        return _bandPos < _bandRows;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _band.Dispose();
        _workspaceLease?.Dispose();
        _workspaceLease = null;
    }
}
