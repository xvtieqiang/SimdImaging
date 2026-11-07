using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Contrast-limited adaptive histogram equalisation. The image is split into tiles, each tile's histogram is
/// clipped at a limit and the excess spread back uniformly, and every pixel then reads a bilinear blend of the
/// four surrounding tile LUTs — so contrast rises locally without the tile seams a per-tile LUT would leave.
/// <para>
/// All table construction is integer; only the final blend is float, in a fixed order.
/// </para>
/// </summary>
public static class ClaheOps
{
    private const int Bins = 256;

    /// <summary>Equalises contrast locally.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="tilesX">Tiles across; at least 1 and at most the image width.</param>
    /// <param name="tilesY">Tiles down; at least 1 and at most the image height.</param>
    /// <param name="clipLimit">Histogram clip as a multiple of the tile's average bin count; 1 is very strong,
    /// large values approach plain equalisation.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A tile count is not positive or exceeds the image, or the
    /// clip limit is not positive.</exception>
    public static PixelBuffer Clahe(PixelView source, int tilesX = 8, int tilesY = 8, float clipLimit = 3f,
        IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tilesX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tilesY);
        if (tilesX > source.Width)
            throw new ArgumentOutOfRangeException(nameof(tilesX), tilesX, "Cannot ask for more tiles than columns.");
        if (tilesY > source.Height)
            throw new ArgumentOutOfRangeException(nameof(tilesY), tilesY, "Cannot ask for more tiles than rows.");
        if (!(clipLimit > 0f) || float.IsInfinity(clipLimit))
            throw new ArgumentOutOfRangeException(nameof(clipLimit), clipLimit, "Clip limit must be finite and positive.");

        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var tiles = tilesX * tilesY;
        var histograms = new int[tiles * 3 * Bins];

        // One parallel region per tile row: a tile row owns a disjoint slice of `histograms`, so no locking is
        // needed and every bin count is bit-identical to the sequential version. This pass and the apply pass
        // below were both fully serial before — 80 ms on a real 1080p photo, all of it on one core.
        RowParallel.ForBands(tilesY, (start, end) =>
        {
            var row = new float[stride];
            for (var tileRow = start; tileRow < end; tileRow++)
            {
                var firstRow = tileRow * height / tilesY;
                var lastRow = (tileRow + 1) * height / tilesY;
                for (var y = firstRow; y < lastRow; y++)
                {
                    Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

                    for (var x = 0; x < width; x++)
                    {
                        var tile = tileRow * tilesX + x * tilesX / width;
                        var offset = x * 4;

                        for (var c = 0; c < 3; c++)
                        {
                            var bin = (int)(row[offset + c] * 255f + 0.5f);
                            if (bin < 0) bin = 0;
                            if (bin > 255) bin = 255;
                            histograms[(tile * 3 + c) * Bins + bin]++;
                        }
                    }
                }
            }
        });

        var luts = BuildLuts(histograms, tilesX, tilesY, width, height, clipLimit);

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;
        var tileWidth = width / (float)tilesX;
        var tileHeight = height / (float)tilesY;

        // The apply pass only reads `luts`, and each row writes only its own destination row, so the band split
        // cannot change a byte.
        RowParallel.For(width, height, (start, end) =>
        {
            var row = new float[stride];
            for (var y = start; y < end; y++)
            {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

            var ty = (y + 0.5f) / tileHeight - 0.5f;
            ty = Math.Clamp(ty, 0f, tilesY - 1f);
            var ty0 = (int)MathF.Floor(ty);
            var ty1 = Math.Min(ty0 + 1, tilesY - 1);
            var fy = ty - ty0;

            for (var x = 0; x < width; x++)
            {
                var tx = (x + 0.5f) / tileWidth - 0.5f;
                tx = Math.Clamp(tx, 0f, tilesX - 1f);
                var tx0 = (int)MathF.Floor(tx);
                var tx1 = Math.Min(tx0 + 1, tilesX - 1);
                var fx = tx - tx0;

                var offset = x * 4;
                for (var c = 0; c < 3; c++)
                {
                    var value = row[offset + c] * 255f;
                    var bin = (int)MathF.Floor(value);
                    if (bin < 0) bin = 0;
                    if (bin > 255) bin = 255;
                    var next = Math.Min(bin + 1, 255);
                    var frac = Math.Clamp(value - bin, 0f, 1f);

                    var topLeft = Sample(luts, tilesX, ty0, tx0, c, bin, next, frac);
                    var topRight = Sample(luts, tilesX, ty0, tx1, c, bin, next, frac);
                    var bottomLeft = Sample(luts, tilesX, ty1, tx0, c, bin, next, frac);
                    var bottomRight = Sample(luts, tilesX, ty1, tx1, c, bin, next, frac);

                    var top = topLeft + (topRight - topLeft) * fx;
                    var bottom = bottomLeft + (bottomRight - bottomLeft) * fx;
                    row[offset + c] = top + (bottom - top) * fy;
                }
            }

            Pixels.WriteRowRaw(row, width, source.Format, view.Row(y));
            }
        });

        return destination;
    }

    private static float[] BuildLuts(int[] histograms, int tilesX, int tilesY, int width, int height, float clipLimit)
    {
        var luts = new float[tilesX * tilesY * 3 * Bins];

        for (var ty = 0; ty < tilesY; ty++)
        {
            var y0 = ty * height / tilesY;
            var y1 = (ty + 1) * height / tilesY;

            for (var tx = 0; tx < tilesX; tx++)
            {
                var x0 = tx * width / tilesX;
                var x1 = (tx + 1) * width / tilesX;
                var area = (x1 - x0) * (y1 - y0);
                var tile = ty * tilesX + tx;

                for (var c = 0; c < 3; c++)
                {
                    var histogram = histograms.AsSpan((tile * 3 + c) * Bins, Bins);
                    var lut = luts.AsSpan((tile * 3 + c) * Bins, Bins);

                    // A tile that holds a single value has nothing to equalise. Clipping it would spread its
                    // mass into the low bins and push the whole tile to white, so it keeps the identity.
                    var populated = 0;
                    for (var i = 0; i < Bins && populated < 2; i++)
                    {
                        if (histogram[i] != 0)
                            populated++;
                    }

                    if (populated < 2)
                    {
                        for (var i = 0; i < Bins; i++)
                            lut[i] = i / 255f;
                        continue;
                    }

                    var limit = Math.Max(1, (int)(clipLimit * area / Bins));
                    var excess = 0;
                    for (var i = 0; i < Bins; i++)
                    {
                        if (histogram[i] <= limit)
                            continue;

                        excess += histogram[i] - limit;
                        histogram[i] = limit;
                    }

                    if (excess > 0)
                    {
                        var share = excess / Bins;
                        var remainder = excess % Bins;
                        for (var i = 0; i < Bins; i++)
                            histogram[i] += share + (i < remainder ? 1 : 0);
                    }

                    // The lowest populated bin maps to zero, so the LUT spans the full range without clipping
                    // a flat tile into a white rectangle.
                    var cdfMin = 0;
                    for (var i = 0; i < Bins; i++)
                    {
                        if (histogram[i] == 0)
                            continue;

                        cdfMin = histogram[i];
                        break;
                    }

                    var denominator = area - cdfMin;
                    var cumulative = 0;
                    for (var i = 0; i < Bins; i++)
                    {
                        cumulative += histogram[i];
                        lut[i] = denominator > 0
                            ? Math.Clamp((cumulative - cdfMin) / (float)denominator, 0f, 1f)
                            : i / 255f;
                    }
                }
            }
        }

        return luts;
    }

    private static float Sample(float[] luts, int tilesX, int tileY, int tileX, int channel, int bin, int next,
        float frac)
    {
        var offset = ((tileY * tilesX + tileX) * 3 + channel) * Bins;
        var a = luts[offset + bin];
        var b = luts[offset + next];
        return a + (b - a) * frac;
    }
}
