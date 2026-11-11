using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// Crops the borders whose content carries no information: a row or column whose luma entropy is below a
/// fraction of the busiest one is treated as empty margin. Unlike <see cref="Canvas.Trim"/> this does not
/// require the border to match the corner pixel — a noisy scan border is cropped where an exact-match trim
/// would keep it.
/// </summary>
public static class EntropyCrop
{
    private const int Bins = 256;

    /// <summary>Crops low-entropy rows and columns from the outside in.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="threshold">Fraction of the highest row/column entropy below which a line counts as margin,
    /// 0..1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0..1.</exception>
    public static WorkingBuffer Crop(PixelView source, float threshold = 0.25f, IPixelPool? pool = null)
    {
        if (!(threshold >= 0f) || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be in [0, 1].");

        var width = source.Width;
        var height = source.Height;
        var row = new float[width * 4];
        var histogram = new int[Bins];
        var rowEntropy = new double[height];

        // One pass for the rows: a per-row histogram needs no image-sized scratch.
        for (var y = 0; y < height; y++)
        {
            Array.Clear(histogram);
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

            for (var x = 0; x < width; x++)
                histogram[ToBin(Luma(row, x))]++;

            rowEntropy[y] = Entropy(histogram, width);
        }

        var busiestRow = Max(rowEntropy);
        if (busiestRow <= 0.0)
            return Transforms.Crop(source, new PixelRect(0, 0, 1, 1), pool);

        var rowCut = busiestRow * threshold;
        var top = 0;
        while (top < height && rowEntropy[top] < rowCut)
            top++;

        var bottom = height - 1;
        while (bottom > top && rowEntropy[bottom] < rowCut)
            bottom--;

        var band = bottom - top + 1;
        var columnEntropy = new double[width];

        // One histogram per column, filled in a single pass over the surviving rows. At 4K that is 3.9 MB
        // of counters instead of a 33 MB luma plane.
        var columnHistograms = new int[width * Bins];
        for (var y = top; y <= bottom; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            for (var x = 0; x < width; x++)
                columnHistograms[x * Bins + ToBin(Luma(row, x))]++;
        }

        for (var x = 0; x < width; x++)
            columnEntropy[x] = Entropy(columnHistograms.AsSpan(x * Bins, Bins), band);

        var busiestColumn = Max(columnEntropy);
        var columnCut = busiestColumn * threshold;

        var left = 0;
        while (left < width && columnEntropy[left] < columnCut)
            left++;

        var right = width - 1;
        while (right > left && columnEntropy[right] < columnCut)
            right--;

        return Transforms.Crop(source, new PixelRect(left, top, right - left + 1, bottom - top + 1), pool);
    }

    private static int ToBin(float luma)
    {
        var bin = (int)(luma * 255f + 0.5f);
        return bin < 0 ? 0 : bin > 255 ? 255 : bin;
    }

    private static float Luma(float[] row, int x)
    {
        var offset = x * 4;
        return 0.299f * row[offset] + 0.587f * row[offset + 1] + 0.114f * row[offset + 2];
    }

    private static double Entropy(ReadOnlySpan<int> histogram, int total)
    {
        if (total <= 0)
            return 0.0;

        var entropy = 0.0;
        foreach (var count in histogram)
        {
            if (count == 0)
                continue;

            var probability = (double)count / total;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    private static double Max(double[] values)
    {
        var max = 0.0;
        foreach (var value in values)
            max = Math.Max(max, value);
        return max;
    }
}
