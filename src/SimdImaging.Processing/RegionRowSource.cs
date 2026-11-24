using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>
/// A rectangle of a <see cref="IRegionSource"/> presented as rows, one band at a time: the bridge that lets a
/// same-size plan run over a region without the region ever existing whole.
/// <para>The rectangle becomes the "image" this source describes, so a plan run over it clamps at the rectangle's
/// edges — exactly what <c>ProcessRegion</c> does, and exactly what <c>Load → Apply → Crop</c> produces for interior
/// pixels.</para>
/// </summary>
internal sealed class RegionRowSource : IRowSource, IDisposable
{
    private readonly IRegionSource _source;
    private readonly PixelRect _region;
    private readonly WorkingBuffer _band;
    private readonly int _rowBytes;

    private int _loaded;   // rows of the region read into the band so far
    private int _bandRows; // valid rows in the band
    private int _bandPos;  // next row to hand out
    private bool _disposed;

    public RegionRowSource(IRegionSource source, PixelRect region, int bandRows, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!region.FitsIn(source.Width, source.Height) || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"The region {region} does not lie inside {source.Width}x{source.Height}.",
                nameof(region));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bandRows);

        _source = source;
        _region = region;
        _band = WorkingBuffer.Rent(region.Width, Math.Min(bandRows, region.Height), source.Format, pool);
        _rowBytes = region.Width * source.Format.BytesPerPixel();
    }

    public int Width => _region.Width;

    public int Height => _region.Height;

    public WorkingFormat Format => _source.Format;

    public int ReadRows(Span<byte> destination, int stride, int maxRows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stride == 0)
            stride = _rowBytes;

        var copied = 0;
        while (copied < maxRows)
        {
            if (_bandPos == _bandRows)
            {
                if (_loaded >= _region.Height)
                    break;

                var rows = Math.Min(_band.Height, _region.Height - _loaded);
                var view = _band.View.Slice(new PixelRect(0, 0, _region.Width, rows));
                _source.ReadRegion(new PixelRect(_region.X, _region.Y + _loaded, _region.Width, rows), view);
                _loaded += rows;
                _bandRows = rows;
                _bandPos = 0;
            }

            var take = Math.Min(maxRows - copied, _bandRows - _bandPos);
            for (var y = 0; y < take; y++)
            {
                _band.View.Row(_bandPos + y)
                    .CopyTo(destination.Slice((copied + y) * stride, _rowBytes));
            }

            _bandPos += take;
            copied += take;
        }

        return copied;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _band.Dispose();
    }
}
