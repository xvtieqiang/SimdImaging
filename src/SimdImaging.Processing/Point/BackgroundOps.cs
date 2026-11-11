using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Replaces a background colour, with a tolerance for "close enough" and a feather band where the replacement
/// fades in. This is the chroma-key shape: nothing outside the tolerance moves, so a subject of a different
/// hue is untouched.
/// </summary>
public static class BackgroundOps
{
    /// <summary>Swaps a background colour for another.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="background">The colour to remove.</param>
    /// <param name="replacement">The colour to put in its place.</param>
    /// <param name="tolerance">Distance below which a pixel is fully replaced, in normalised RGB.</param>
    /// <param name="feather">Width of the band above the tolerance over which the replacement fades in.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A distance is negative.</exception>
    public static WorkingBuffer ReplaceBackground(PixelView source, RgbaColor background, RgbaColor replacement,
        float tolerance = 0.1f, float feather = 0.05f, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        ArgumentOutOfRangeException.ThrowIfNegative(feather);

        var width = source.Width;
        var stride = width * 4;
        var destination = WorkingBuffer.Rent(width, source.Height, source.Format, pool);
        var row = new float[stride];

        var backgroundR = background.R / 255f;
        var backgroundG = background.G / 255f;
        var backgroundB = background.B / 255f;
        var replacementR = replacement.R / 255f;
        var replacementG = replacement.G / 255f;
        var replacementB = replacement.B / 255f;
        var edge = tolerance + feather;

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                var dr = row[offset] - backgroundR;
                var dg = row[offset + 1] - backgroundG;
                var db = row[offset + 2] - backgroundB;

                // RMS rather than the sum: the distance then reads as "average channel error", so a tolerance
                // means the same thing whatever the channel count.
                var distance = MathF.Sqrt((dr * dr + dg * dg + db * db) / 3f);
                if (distance >= edge)
                    continue;

                var weight = distance <= tolerance ? 1f : 1f - (distance - tolerance) / feather;
                row[offset] += (replacementR - row[offset]) * weight;
                row[offset + 1] += (replacementG - row[offset + 1]) * weight;
                row[offset + 2] += (replacementB - row[offset + 2]) * weight;
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }
}
