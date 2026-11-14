using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Exact squared Euclidean distance transform of a mask: for every pixel, the squared distance to the
/// nearest foreground pixel. Felzenszwalb–Huttenlocher's two-pass parabola method, O(N) in the pixel count
/// regardless of the distances involved.
/// <para>
/// The mask is the alpha channel: a pixel is foreground when its alpha reaches the threshold
/// (0.5 by default, i.e. alpha ≥ 128 in 8-bit). Alpha is the library's coverage channel everywhere else,
/// so this needs no second convention.
/// </para>
/// <para>
/// The result is a distance field in <see cref="WorkingFormat.Rgba32F"/> with the squared distance in R, G
/// and B and 1 in A. Squared, not square-rooted: the values stay exact integers below 2²⁴, which makes the
/// whole operator D0 and lets a test compare against hand-computed numbers instead of a tolerance. Take
/// <c>MathF.Sqrt</c> at the call site when the distance itself is what you need.
/// </para>
/// </summary>
public static class DistanceTransformOps
{
    /// <summary>Squared Euclidean distance to the nearest foreground pixel.</summary>
    /// <param name="source">The mask; alpha is the coverage channel.</param>
    /// <param name="threshold">Alpha at or above this counts as foreground.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The threshold is outside (0, 1].</exception>
    public static WorkingBuffer SquaredEuclidean(PixelView source, float threshold = 0.5f,
        IPixelPool? pool = null)
    {
        if (!(threshold > 0f) || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be in (0, 1].");

        var width = source.Width;
        var height = source.Height;
        var count = width * height;
        var destination = WorkingBuffer.Rent(width, height, WorkingFormat.Rgba32F, pool);

        // A value large enough that it never wins a minimum but small enough that adding q² stays finite.
        const float Infinity = 1e20f;

        var field = FloatScratch.Rent(count);
        var row = FloatScratch.Rent(Math.Max(width, height));
        var result = FloatScratch.Rent(Math.Max(width, height));
        var vertices = new int[Math.Max(width, height) + 1];
        var boundaries = new float[Math.Max(width, height) + 2];

        // Column pass tile: 64 columns, column-major. See the gather loop for why.
        const int TileColumns = 64;
        var tileWidth = Math.Min(TileColumns, width);
        var tile = FloatScratch.Rent(tileWidth * height);

        try
        {
            // Seed: 0 on foreground, "infinitely far" on background.
            var alpha = new float[width * 4];
            for (var y = 0; y < height; y++)
            {
                Pixels.ReadRowRaw(source.Row(y), width, source.Format, alpha);
                for (var x = 0; x < width; x++)
                    field[y * width + x] = alpha[x * 4 + 3] >= threshold ? 0f : Infinity;
            }

            // Columns, then rows: the transform is separable, so two 1-D passes give the 2-D field.
            //
            // The column pass works on 64-column tiles held column-major. Gathering one column at a time
            // touches a new cache line per element (the row stride is 7.5 KiB at 1080p); reading 64
            // columns together turns that into 256 contiguous bytes per row, and the 64 lines a row
            // writes into stay in L1 across rows because each row advances them by four bytes. The sweep
            // then runs on contiguous columns out of L2 (276 KB at 1080p).
            for (var x0 = 0; x0 < width; x0 += TileColumns)
            {
                var columns = Math.Min(TileColumns, width - x0);

                for (var y = 0; y < height; y++)
                {
                    var sourceRow = field.AsSpan(y * width + x0, columns);
                    for (var c = 0; c < columns; c++)
                        tile[c * height + y] = sourceRow[c];
                }

                for (var c = 0; c < columns; c++)
                {
                    var column = tile.AsSpan(c * height, height);
                    Transform(column, result, vertices, boundaries, height);
                    result.AsSpan(0, height).CopyTo(column);
                }

                for (var y = 0; y < height; y++)
                {
                    var targetRow = field.AsSpan(y * width + x0, columns);
                    for (var c = 0; c < columns; c++)
                        targetRow[c] = tile[c * height + y];
                }
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    row[x] = field[y * width + x];

                Transform(row, result, vertices, boundaries, width);

                for (var x = 0; x < width; x++)
                    field[y * width + x] = result[x];
            }

            var pixels = FloatScratch.Rent(width * 4);
            try
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var value = field[y * width + x];
                        var offset = x * 4;
                        pixels[offset] = value;
                        pixels[offset + 1] = value;
                        pixels[offset + 2] = value;
                        pixels[offset + 3] = 1f;
                    }

                    Pixels.WriteRowRaw(pixels, width, WorkingFormat.Rgba32F, destination.View.Row(y));
                }
            }
            finally
            {
                FloatScratch.Return(pixels);
            }
        }
        catch
        {
            destination.Dispose();
            throw;
        }
        finally
        {
            FloatScratch.Return(field);
            FloatScratch.Return(row);
            FloatScratch.Return(result);
            FloatScratch.Return(tile);
        }

        return destination;
    }

    /// <summary>Where the parabolas rooted at <paramref name="left"/> and <paramref name="right"/> cross.</summary>
    private static float Intersection(ReadOnlySpan<float> f, int left, int right) =>
        ((f[right] + (float)right * right) - (f[left] + (float)left * left)) / (2 * right - 2 * left);

    /// <summary>
    /// The 1-D transform: lower envelope of the parabolas rooted at each sample. <paramref name="z"/> holds
    /// the boundary between consecutive parabolas, so the sweep never looks back.
    /// </summary>
    private static void Transform(ReadOnlySpan<float> f, Span<float> d, Span<int> v, Span<float> z, int n)
    {
        const float Infinity = 1e20f;

        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;

        for (var q = 1; q < n; q++)
        {
            // z[0] is -infinity, so this loop always terminates at k = 0: the first parabola is never
            // dominated by anything to its left.
            var s = Intersection(f, v[k], q);
            while (s <= z[k])
            {
                k--;
                s = Intersection(f, v[k], q);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = Infinity;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
                k++;

            var root = v[k];
            d[q] = (float)(q - root) * (q - root) + f[root];
        }
    }
}
