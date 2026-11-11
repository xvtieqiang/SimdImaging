using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// W3C blend modes composited source-over. The blend function decides the colour, then the standard
/// <c>outA = sa + da(1-sa)</c> resolves coverage — blending never changes where the paint lands.
/// </summary>
public static class Blend
{
    /// <summary>Blends <paramref name="overlay"/> onto <paramref name="background"/> at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentException">The overlay does not fit at that position.</exception>
    public static WorkingBuffer Apply(PixelView background, PixelView overlay, int x, int y, BlendMode mode,
        IPixelPool? pool = null)
    {
        if (x < 0 || y < 0 || x + overlay.Width > background.Width || y + overlay.Height > background.Height)
            throw new ArgumentException(
                $"A {overlay.Width}x{overlay.Height} overlay at ({x},{y}) does not fit in {background.Width}x{background.Height}.", nameof(overlay));

        var destination = Transforms.Copy(background, pool);
        var bytesPerPixel = background.Format.BytesPerPixel();
        var overlayRow = new float[overlay.Width * 4];
        var backgroundRow = new float[overlay.Width * 4];
        Span<float> blended = stackalloc float[3];

        for (var row = 0; row < overlay.Height; row++)
        {
            Pixels.ReadRowRaw(overlay.Row(row), overlay.Width, overlay.Format, overlayRow);
            var destinationBytes = destination.View.Row(y + row).Slice(x * bytesPerPixel, overlay.Width * bytesPerPixel);
            Pixels.ReadRowRaw(destinationBytes, overlay.Width, background.Format, backgroundRow);

            for (var i = 0; i < overlay.Width * 4; i += 4)
            {
                var sourceAlpha = overlayRow[i + 3];
                var destinationAlpha = backgroundRow[i + 3];
                var outputAlpha = sourceAlpha + destinationAlpha * (1f - sourceAlpha);

                if (outputAlpha <= 0f)
                {
                    backgroundRow[i] = backgroundRow[i + 1] = backgroundRow[i + 2] = backgroundRow[i + 3] = 0f;
                    continue;
                }

                BlendPixel(mode, backgroundRow.AsSpan(i, 3), overlayRow.AsSpan(i, 3), blended);

                for (var c = 0; c < 3; c++)
                {
                    var backdrop = backgroundRow[i + c];
                    var source = overlayRow[i + c];
                    backgroundRow[i + c] = ((1f - destinationAlpha) * source * sourceAlpha
                                            + destinationAlpha * sourceAlpha * blended[c]
                                            + (1f - sourceAlpha) * backdrop * destinationAlpha) / outputAlpha;
                }

                backgroundRow[i + 3] = outputAlpha;
            }

            Pixels.WriteRowRaw(backgroundRow, overlay.Width, background.Format, destinationBytes);
        }

        return destination;
    }

    private static void BlendPixel(BlendMode mode, ReadOnlySpan<float> backdrop, ReadOnlySpan<float> source, Span<float> result)
    {
        switch (mode)
        {
            case BlendMode.Hue:
            {
                SetSaturation(source, Saturation(backdrop), result);
                SetLuminosity(result, Luminosity(backdrop));
                break;
            }

            case BlendMode.Saturation:
            {
                SetSaturation(backdrop, Saturation(source), result);
                SetLuminosity(result, Luminosity(backdrop));
                break;
            }

            case BlendMode.Color:
            {
                result[0] = source[0];
                result[1] = source[1];
                result[2] = source[2];
                SetLuminosity(result, Luminosity(backdrop));
                break;
            }

            case BlendMode.Luminosity:
            {
                result[0] = backdrop[0];
                result[1] = backdrop[1];
                result[2] = backdrop[2];
                SetLuminosity(result, Luminosity(source));
                break;
            }

            default:
                for (var c = 0; c < 3; c++)
                    result[c] = Channel(mode, backdrop[c], source[c]);
                break;
        }
    }

    private static float Channel(BlendMode mode, float b, float s) => mode switch
    {
        BlendMode.Normal => s,
        BlendMode.Multiply => b * s,
        BlendMode.Screen => 1f - (1f - b) * (1f - s),
        BlendMode.Overlay => HardLight(s, b),
        BlendMode.Darken => MathF.Min(b, s),
        BlendMode.Lighten => MathF.Max(b, s),
        BlendMode.ColorDodge => s >= 1f ? 1f : MathF.Min(1f, b / (1f - s)),
        BlendMode.ColorBurn => s <= 0f ? 0f : 1f - MathF.Min(1f, (1f - b) / s),
        BlendMode.HardLight => HardLight(b, s),
        BlendMode.SoftLight => SoftLight(b, s),
        BlendMode.Difference => MathF.Abs(b - s),
        BlendMode.Exclusion => b + s - 2f * b * s,
        BlendMode.Add => MathF.Min(1f, b + s),
        BlendMode.Subtract => MathF.Max(0f, b - s),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a separable blend mode."),
    };

    private static float HardLight(float b, float s) =>
        s <= 0.5f ? 2f * b * s : 1f - 2f * (1f - b) * (1f - s);

    private static float SoftLight(float b, float s)
    {
        if (s <= 0.5f)
            return b - (1f - 2f * s) * b * (1f - b);

        var d = b <= 0.25f ? ((16f * b - 12f) * b + 4f) * b : MathF.Sqrt(b);
        return b + (2f * s - 1f) * (d - b);
    }

    private static float Luminosity(ReadOnlySpan<float> colour) =>
        0.3f * colour[0] + 0.59f * colour[1] + 0.11f * colour[2];

    private static float Saturation(ReadOnlySpan<float> colour)
    {
        var max = MathF.Max(colour[0], MathF.Max(colour[1], colour[2]));
        var min = MathF.Min(colour[0], MathF.Min(colour[1], colour[2]));
        return max - min;
    }

    private static void SetLuminosity(Span<float> colour, float target)
    {
        var delta = target - Luminosity(colour);
        colour[0] += delta;
        colour[1] += delta;
        colour[2] += delta;
        ClipColor(colour);
    }

    private static void SetSaturation(ReadOnlySpan<float> colour, float target, Span<float> result)
    {
        var minIndex = 0;
        var maxIndex = 0;
        for (var i = 1; i < 3; i++)
        {
            if (colour[i] < colour[minIndex]) minIndex = i;
            if (colour[i] > colour[maxIndex]) maxIndex = i;
        }

        var min = colour[minIndex];
        var max = colour[maxIndex];

        if (max <= min)
        {
            // Neutral colour: no hue to scale, so the target saturation cannot be applied.
            result[0] = result[1] = result[2] = 0f;
            return;
        }

        var midIndex = 3 - minIndex - maxIndex;
        result[minIndex] = 0f;
        result[midIndex] = (colour[midIndex] - min) * target / (max - min);
        result[maxIndex] = target;
    }

    private static void ClipColor(Span<float> colour)
    {
        var luminosity = Luminosity(colour);
        var min = MathF.Min(colour[0], MathF.Min(colour[1], colour[2]));
        var max = MathF.Max(colour[0], MathF.Max(colour[1], colour[2]));

        if (min < 0f && luminosity > 0f)
        {
            var scale = luminosity / (luminosity - min);
            for (var c = 0; c < 3; c++)
                colour[c] = luminosity + (colour[c] - luminosity) * scale;
        }

        if (max > 1f && luminosity < 1f)
        {
            var scale = (1f - luminosity) / (max - luminosity);
            for (var c = 0; c < 3; c++)
                colour[c] = luminosity + (colour[c] - luminosity) * scale;
        }

        for (var c = 0; c < 3; c++)
            colour[c] = Math.Clamp(colour[c], 0f, 1f);
    }

}
