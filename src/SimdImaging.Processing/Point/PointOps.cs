using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Point operators: every output pixel depends on the same input pixel only. They work on straight alpha —
/// a colour adjustment must not change coverage, so alpha is never touched. Rounding and clamping happen
/// once, in <see cref="Pixels.WriteRowRaw"/>.
/// </summary>
public static class PointOps
{
    /// <summary>Inverts the colour channels; alpha is preserved.</summary>
    public static WorkingBuffer Invert(PixelView source, IPixelPool? pool = null) =>
        ApplyLut(source, pool, static (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] = 1f - row[i];
                row[i + 1] = 1f - row[i + 1];
                row[i + 2] = 1f - row[i + 2];
            }
        });

    /// <summary>Adds <paramref name="amount"/> to the colour channels, in sample units (0.5 = half range).</summary>
    public static WorkingBuffer Brightness(PixelView source, float amount, IPixelPool? pool = null)
    {
        // NaN/+Inf used to pass straight through the float transform and silently produce a black image.
        // Untrusted/caller-mistake values must fail loudly, never degrade to a plausible-looking result.
        if (!float.IsFinite(amount))
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Brightness must be finite.");

        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] += amount;
                row[i + 1] += amount;
                row[i + 2] += amount;
            }
        });
    }

    /// <summary>Multiplies the colour channels: 1 is identity, above 1 brightens, below 1 darkens.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is negative.</exception>
    public static WorkingBuffer Lightness(PixelView source, float factor, IPixelPool? pool = null)
    {
        RequireFinite(factor, nameof(factor));
        ArgumentOutOfRangeException.ThrowIfNegative(factor);
        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] *= factor;
                row[i + 1] *= factor;
                row[i + 2] *= factor;
            }
        });
    }

    /// <summary>Scales the colour channels around mid-grey: <c>(v - 0.5)·amount + 0.5</c>.</summary>
    public static WorkingBuffer Contrast(PixelView source, float amount, IPixelPool? pool = null)
    {
        RequireFinite(amount, nameof(amount));
        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] = (row[i] - 0.5f) * amount + 0.5f;
                row[i + 1] = (row[i + 1] - 0.5f) * amount + 0.5f;
                row[i + 2] = (row[i + 2] - 0.5f) * amount + 0.5f;
            }
        });
    }

    /// <summary>Scales colour away from its luma: <c>luma + (v - luma)·amount</c>; 0 is greyscale, 1 is identity.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="amount">0..1 (and beyond, to oversaturate).</param>
    /// <param name="bt709">True uses BT.709 luma weights, false BT.601.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static WorkingBuffer Saturation(PixelView source, float amount, bool bt709 = false, IPixelPool? pool = null)
    {
        RequireFinite(amount, nameof(amount));
        var (lr, lg, lb) = bt709 ? (0.2126f, 0.7152f, 0.0722f) : (0.299f, 0.587f, 0.114f);
        return Apply(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                var luma = row[i] * lr + row[i + 1] * lg + row[i + 2] * lb;
                row[i] = luma + (row[i] - luma) * amount;
                row[i + 1] = luma + (row[i + 1] - luma) * amount;
                row[i + 2] = luma + (row[i + 2] - luma) * amount;
            }
        });
    }

    /// <summary>Replaces colour with its luma (BT.601 or BT.709); alpha is preserved.</summary>
    public static WorkingBuffer Grayscale(PixelView source, bool bt709 = false, IPixelPool? pool = null)
    {
        var (lr, lg, lb) = bt709 ? (0.2126f, 0.7152f, 0.0722f) : (0.299f, 0.587f, 0.114f);
        Span<float> matrix = stackalloc float[12]
        {
            lr, lg, lb, 0f,
            lr, lg, lb, 0f,
            lr, lg, lb, 0f,
        };
        return ColorMatrixCore(source, matrix, pool);
    }

    /// <summary>Applies the sRGB-style power curve <c>v^(1/gamma)</c> to the colour channels.</summary>
    /// <param name="gamma">Greater than 0. 1 is identity, 2.2 approximates a display gamma decode.</param>
    public static WorkingBuffer Gamma(PixelView source, float gamma, IPixelPool? pool = null)
    {
        if (!(gamma > 0f) || float.IsInfinity(gamma))
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "Gamma must be finite and greater than 0.");

        var exponent = 1f / gamma;
        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] = MathF.Pow(row[i], exponent);
                row[i + 1] = MathF.Pow(row[i + 1], exponent);
                row[i + 2] = MathF.Pow(row[i + 2], exponent);
            }
        });
    }

    internal delegate void RowTransform(Span<float> row, int width);

    /// <summary>Test hook: forces the original float row path so the LUT twin can be compared byte for byte.</summary>
    internal static bool ForceScalarPoint;

    /// <summary>Caller mistakes must fail loudly: NaN/Infinity used to flow through a float transform and
    /// silently turn into a black (or fully clamped) image.</summary>
    private static void RequireFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, value, "Value must be finite.");
    }

    /// <summary>
    /// For channel-independent operators on Rgba8, precompute the 256-entry byte LUT instead of doing
    /// eight float conversions per pixel. The scalar twin is the reference and must match exactly.
    /// </summary>
    private static WorkingBuffer ApplyLut(PixelView source, IPixelPool? pool, RowTransform transform) =>
        source.Format == WorkingFormat.Rgba8 && !ForceScalarPoint
            ? PointLut.ApplyRgba8(source, pool, transform)
            : Apply(source, pool, transform);

    private static WorkingBuffer Apply(PixelView source, IPixelPool? pool, RowTransform transform)
    {
        var destination = WorkingBuffer.Rent(source.Width, source.Height, source.Format, pool);
        RowParallel.For(source.Width, source.Height, (start, end) =>
        {
            // One scratch row per worker: FloatScratch is thread-local, so bands never share a buffer.
            var row = FloatScratch.Rent(source.Width * 4);
            try
            {
                for (var y = start; y < end; y++)
                {
                    Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
                    transform(row, source.Width);
                    Pixels.WriteRowRaw(row, source.Width, source.Format, destination.View.Row(y));
                }
            }
            finally
            {
                FloatScratch.Return(row);
            }
        });

        return destination;
    }

    /// <summary>Reduces each colour channel to <paramref name="levels"/> evenly spaced values.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="levels"/> is less than 2.</exception>
    public static WorkingBuffer Posterize(PixelView source, int levels, IPixelPool? pool = null)
    {
        if (levels < 2)
            throw new ArgumentOutOfRangeException(nameof(levels), levels, "Posterize needs at least 2 levels.");

        var steps = levels - 1;
        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] = MathF.Round(row[i] * steps) / steps;
                row[i + 1] = MathF.Round(row[i + 1] * steps) / steps;
                row[i + 2] = MathF.Round(row[i + 2] * steps) / steps;
            }
        });
    }

    /// <summary>Black or white by luma against <paramref name="threshold"/> (0..1); alpha is preserved.</summary>
    public static WorkingBuffer Threshold(PixelView source, float threshold, bool bt709 = false,
        IPixelPool? pool = null)
    {
        if (!(threshold >= 0f) || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be within 0..1.");

        var (lr, lg, lb) = bt709 ? (0.2126f, 0.7152f, 0.0722f) : (0.299f, 0.587f, 0.114f);
        return Apply(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                var luma = row[i] * lr + row[i + 1] * lg + row[i + 2] * lb;
                var value = luma >= threshold ? 1f : 0f;
                row[i] = value;
                row[i + 1] = value;
                row[i + 2] = value;
            }
        });
    }

    /// <summary>
    /// Applies a 3×4 colour matrix (row-major: <c>R' = m0·R + m1·G + m2·B + m3</c>). Alpha is untouched.
    /// Values are straight samples in [0,1] for integer formats.
    /// </summary>
    /// <exception cref="ArgumentException">The matrix is not 12 floats.</exception>
    public static WorkingBuffer ColorMatrix(PixelView source, ReadOnlySpan<float> matrix, IPixelPool? pool = null)
    {
        if (matrix.Length != 12)
            throw new ArgumentException("A colour matrix needs 12 floats (3 rows × 4 columns).", nameof(matrix));
        for (var i = 0; i < matrix.Length; i++)
            RequireFinite(matrix[i], nameof(matrix));

        return ColorMatrixCore(source, matrix, pool);
    }

    private static WorkingBuffer ColorMatrixCore(PixelView source, ReadOnlySpan<float> matrix, IPixelPool? pool)
    {
        if (source.Format == WorkingFormat.Rgba8 && !ForceScalarPoint)
            return PointLut.ApplyMatrixRgba8(source, pool, matrix);

        // Copy out of the span: the row lambda captures it.
        var coefficients = matrix.ToArray();
        return Apply(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                var r = row[i];
                var g = row[i + 1];
                var b = row[i + 2];
                row[i] = coefficients[0] * r + coefficients[1] * g + coefficients[2] * b + coefficients[3];
                row[i + 1] = coefficients[4] * r + coefficients[5] * g + coefficients[6] * b + coefficients[7];
                row[i + 2] = coefficients[8] * r + coefficients[9] * g + coefficients[10] * b + coefficients[11];
            }
        });
    }

    /// <summary>Rotates hue by <paramref name="degrees"/> using the standard RGB axis rotation.</summary>
    public static WorkingBuffer HueRotate(PixelView source, float degrees, IPixelPool? pool = null)
    {
        RequireFinite(degrees, nameof(degrees));
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);

        Span<float> matrix = stackalloc float[12]
        {
            0.213f + cos * 0.787f - sin * 0.213f, 0.715f - cos * 0.715f - sin * 0.715f, 0.072f - cos * 0.072f + sin * 0.928f, 0f,
            0.213f - cos * 0.213f + sin * 0.143f, 0.715f + cos * 0.285f + sin * 0.140f, 0.072f - cos * 0.072f - sin * 0.283f, 0f,
            0.213f - cos * 0.213f - sin * 0.787f, 0.715f - cos * 0.715f + sin * 0.715f, 0.072f + cos * 0.928f + sin * 0.072f, 0f,
        };

        return ColorMatrix(source, matrix, pool);
    }

    /// <summary>Blends towards a sepia tone; <paramref name="amount"/> 0 is identity, 1 is full sepia.</summary>
    public static WorkingBuffer Sepia(PixelView source, float amount = 1f, IPixelPool? pool = null)
    {
        RequireFinite(amount, nameof(amount));
        var a = Math.Clamp(amount, 0f, 1f);
        var inverse = 1f - a;

        Span<float> matrix = stackalloc float[12]
        {
            inverse + 0.393f * a, 0.769f * a, 0.189f * a, 0f,
            0.349f * a, inverse + 0.686f * a, 0.168f * a, 0f,
            0.272f * a, 0.534f * a, inverse + 0.131f * a, 0f,
        };

        return ColorMatrix(source, matrix, pool);
    }

    /// <summary>
    /// Levels: maps <paramref name="inputBlack"/>..<paramref name="inputWhite"/> onto
    /// <paramref name="outputBlack"/>..<paramref name="outputWhite"/> through a gamma curve. One 1024-entry
    /// lookup, so the power function runs per table slot rather than per pixel.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The input range is empty or gamma is not positive.</exception>
    public static WorkingBuffer Levels(PixelView source, float inputBlack, float inputWhite, float gamma,
        float outputBlack = 0f, float outputWhite = 1f, IPixelPool? pool = null)
    {
        RequireFinite(inputBlack, nameof(inputBlack));
        RequireFinite(inputWhite, nameof(inputWhite));
        RequireFinite(gamma, nameof(gamma));
        RequireFinite(outputBlack, nameof(outputBlack));
        RequireFinite(outputWhite, nameof(outputWhite));
        if (!(inputWhite > inputBlack))
            throw new ArgumentOutOfRangeException(nameof(inputWhite), inputWhite, "inputWhite must be greater than inputBlack.");
        if (!(gamma > 0f) || float.IsInfinity(gamma))
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "Gamma must be finite and positive.");

        const int Size = 1024;
        var lut = new float[Size];
        var span = inputWhite - inputBlack;
        var exponent = 1f / gamma;
        for (var i = 0; i < Size; i++)
        {
            var value = i / (Size - 1f);
            var normalised = Math.Clamp((value - inputBlack) / span, 0f, 1f);
            lut[i] = outputBlack + (outputWhite - outputBlack) * MathF.Pow(normalised, exponent);
        }

        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] = Lookup(row[i]);
                row[i + 1] = Lookup(row[i + 1]);
                row[i + 2] = Lookup(row[i + 2]);
            }

            float Lookup(float value)
            {
                var index = (int)(value * (Size - 1) + 0.5f);
                return lut[Math.Clamp(index, 0, Size - 1)];
            }
        });
    }

    /// <summary>Clamps the colour channels into [<paramref name="low"/>, <paramref name="high"/>]; alpha untouched.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is inverted or outside 0..1.</exception>
    public static WorkingBuffer Clamp(PixelView source, float low, float high, IPixelPool? pool = null)
    {
        RequireFinite(low, nameof(low));
        RequireFinite(high, nameof(high));
        if (low < 0f || high > 1f || low > high)
            throw new ArgumentOutOfRangeException(nameof(low), low, "The clamp range must satisfy 0 <= low <= high <= 1.");

        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] = Math.Clamp(row[i], low, high);
                row[i + 1] = Math.Clamp(row[i + 1], low, high);
                row[i + 2] = Math.Clamp(row[i + 2], low, high);
            }
        });
    }

    /// <summary>Makes every pixel fully transparent or fully opaque by its alpha, without changing colour.</summary>
    public static WorkingBuffer AlphaThreshold(PixelView source, float threshold = 0.5f, IPixelPool? pool = null)
    {
        if (!(threshold >= 0f) || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be within 0..1.");

        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
                row[i + 3] = row[i + 3] >= threshold ? 1f : 0f;
        });
    }

    /// <summary>Stretches each colour channel's min..max to the full range; a flat channel is left alone.</summary>
    public static WorkingBuffer Normalize(PixelView source, IPixelPool? pool = null)
    {
        var row = new float[source.Width * 4];
        Span<float> minimum = [1f, 1f, 1f];
        Span<float> maximum = [0f, 0f, 0f];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, row);
            for (var i = 0; i < source.Width * 4; i += 4)
            {
                for (var c = 0; c < 3; c++)
                {
                    minimum[c] = Math.Min(minimum[c], row[i + c]);
                    maximum[c] = Math.Max(maximum[c], row[i + c]);
                }
            }
        }

        // Arrays, not stackalloc: these are captured by the row lambda below.
        var offset = new float[3];
        var scale = new float[3];
        for (var c = 0; c < 3; c++)
        {
            var span = maximum[c] - minimum[c];
            offset[c] = minimum[c];
            scale[c] = span > 0f ? 1f / span : 0f;
        }

        return Apply(source, pool, (values, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                for (var c = 0; c < 3; c++)
                {
                    values[i + c] = scale[c] > 0f ? (values[i + c] - offset[c]) * scale[c] : values[i + c];
                }
            }
        });
    }

    /// <summary>
    /// Vibrance: saturation boost weighted by how unsaturated the pixel already is, so already-saturated
    /// colours move less than muted ones.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="amount">-1..1; 0 is identity, positive boosts, negative mutes.</param>
    /// <param name="bt709">True uses BT.709 luma weights.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static WorkingBuffer Vibrance(PixelView source, float amount, bool bt709 = false, IPixelPool? pool = null)
    {
        RequireFinite(amount, nameof(amount));
        var (lr, lg, lb) = bt709 ? (0.2126f, 0.7152f, 0.0722f) : (0.299f, 0.587f, 0.114f);
        return Apply(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                var r = row[i];
                var g = row[i + 1];
                var b = row[i + 2];
                var luma = r * lr + g * lg + b * lb;

                var max = MathF.Max(r, MathF.Max(g, b));
                var min = MathF.Min(r, MathF.Min(g, b));
                var saturation = max - min;

                var factor = 1f + amount * (1f - saturation);
                row[i] = luma + (r - luma) * factor;
                row[i + 1] = luma + (g - luma) * factor;
                row[i + 2] = luma + (b - luma) * factor;
            }
        });
    }

    /// <summary>White-balance shift: positive warms (more red, less blue), negative cools.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="amount">-1..1; 0 is identity.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static WorkingBuffer Temperature(PixelView source, float amount, IPixelPool? pool = null)
    {
        RequireFinite(amount, nameof(amount));
        var clamped = Math.Clamp(amount, -1f, 1f);
        var redGain = 1f + clamped * 0.3f;
        var blueGain = 1f - clamped * 0.3f;

        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] *= redGain;
                row[i + 2] *= blueGain;
            }
        });
    }

    /// <summary>Blends the colour towards <paramref name="tint"/>; alpha is untouched.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="tint">The target colour.</param>
    /// <param name="amount">0..1 blend weight.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static WorkingBuffer Tint(PixelView source, RgbaColor tint, float amount = 0.5f, IPixelPool? pool = null)
    {
        RequireFinite(amount, nameof(amount));
        var weight = Math.Clamp(amount, 0f, 1f);
        var (tr, tg, tb) = (tint.R / 255f, tint.G / 255f, tint.B / 255f);

        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] += (tr - row[i]) * weight;
                row[i + 1] += (tg - row[i + 1]) * weight;
                row[i + 2] += (tb - row[i + 2]) * weight;
            }
        });
    }

    /// <summary>
    /// Colour balance across shadows, midtones and highlights. Each tonal range gets a smooth weight from the
    /// pixel's luma — <c>(1-l)²</c>, <c>1 - s - h</c>, <c>l²</c> — so the three ranges sum to 1 and no
    /// brightness is created or lost.
    /// </summary>
    public static WorkingBuffer ColorBalance(PixelView source, ColorBalanceShifts shadows, ColorBalanceShifts midtones,
        ColorBalanceShifts highlights, bool bt709 = false, IPixelPool? pool = null)
    {
        var (lr, lg, lb) = bt709 ? (0.2126f, 0.7152f, 0.0722f) : (0.299f, 0.587f, 0.114f);
        return Apply(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                var luma = row[i] * lr + row[i + 1] * lg + row[i + 2] * lb;
                var shadowWeight = (1f - luma) * (1f - luma);
                var highlightWeight = luma * luma;
                var midWeight = Math.Max(0f, 1f - shadowWeight - highlightWeight);

                row[i] += shadows.R * shadowWeight + midtones.R * midWeight + highlights.R * highlightWeight;
                row[i + 1] += shadows.G * shadowWeight + midtones.G * midWeight + highlights.G * highlightWeight;
                row[i + 2] += shadows.B * shadowWeight + midtones.B * midWeight + highlights.B * highlightWeight;
            }
        });
    }

    /// <summary>Reorders the colour channels; 0 = R, 1 = G, 2 = B. Alpha is untouched.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A source index is outside 0..2.</exception>
    public static WorkingBuffer ChannelSwap(PixelView source, int redSource, int greenSource, int blueSource,
        IPixelPool? pool = null)
    {
        if ((uint)redSource > 2 || (uint)greenSource > 2 || (uint)blueSource > 2)
            throw new ArgumentOutOfRangeException(nameof(redSource), "Channel indices must be 0..2.");

        return Apply(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                var r = row[i];
                var g = row[i + 1];
                var b = row[i + 2];
                Span<float> channels = [r, g, b];
                row[i] = channels[redSource];
                row[i + 1] = channels[greenSource];
                row[i + 2] = channels[blueSource];
            }
        });
    }

    /// <summary>Pure black or white by luma; a hard threshold with no in-between values.</summary>
    public static WorkingBuffer BlackWhite(PixelView source, float threshold = 0.5f, bool bt709 = false,
        IPixelPool? pool = null)
    {
        RequireFinite(threshold, nameof(threshold));
        if (!(threshold >= 0f) || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be within 0..1.");

        var (lr, lg, lb) = bt709 ? (0.2126f, 0.7152f, 0.0722f) : (0.299f, 0.587f, 0.114f);
        return Apply(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                var luma = row[i] * lr + row[i + 1] * lg + row[i + 2] * lb;
                var value = luma >= threshold ? 1f : 0f;
                row[i] = value;
                row[i + 1] = value;
                row[i + 2] = value;
            }
        });
    }

    /// <summary>Multiplies alpha by <paramref name="opacity"/>; colour is untouched.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is outside 0..1.</exception>
    public static WorkingBuffer Opacity(PixelView source, float opacity, IPixelPool? pool = null)
    {
        if (!(opacity >= 0f) || opacity > 1f)
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be within 0..1.");

        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 3; i < width * 4; i += 4)
                row[i] *= opacity;
        });
    }

    /// <summary>
    /// Curves: a tonal transfer function through control points, evaluated with monotone cubic (Fritsch–Carlson)
    /// interpolation so the curve never overshoots between points — an S-curve must not invert locally.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="points">At least two points, sorted by X; the ends are extended flat.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentException">Fewer than two points, or X is not strictly increasing.</exception>
    public static WorkingBuffer Curves(PixelView source, ReadOnlySpan<CurvePoint> points, IPixelPool? pool = null)
    {
        if (points.Length < 2)
            throw new ArgumentException("Curves needs at least two control points.", nameof(points));

        var xs = new float[points.Length];
        var ys = new float[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            xs[i] = points[i].X;
            ys[i] = points[i].Y;
            if (i > 0 && xs[i] <= xs[i - 1])
                throw new ArgumentException("Control points must be sorted by strictly increasing X.", nameof(points));
        }

        var tangents = ComputeMonotoneTangents(xs, ys);

        const int Size = 1024;
        var lut = new float[Size];
        for (var i = 0; i < Size; i++)
            lut[i] = Evaluate(xs, ys, tangents, i / (Size - 1f));

        // ApplyLut, not Apply. On Rgba8 the byte path runs this exact closure once per byte value and
        // thereafter costs four table loads per pixel instead of eight float conversions — measured 3.6 ms
        // down to 0.4 ms at 800×600. PointLut builds the table through the same ReadRowRaw/WriteRowRaw pair
        // the float path uses, so the bytes match by construction; PointLutTests holds Curves to that
        // like every other operator on the fast path. Non-Rgba8 falls through to the float path unchanged.
        return ApplyLut(source, pool, (row, width) =>
        {
            for (var i = 0; i < width * 4; i += 4)
            {
                row[i] = Lookup(row[i]);
                row[i + 1] = Lookup(row[i + 1]);
                row[i + 2] = Lookup(row[i + 2]);
            }

            float Lookup(float value)
            {
                var index = (int)(value * (Size - 1) + 0.5f);
                return lut[Math.Clamp(index, 0, Size - 1)];
            }
        });
    }

    private static float[] ComputeMonotoneTangents(float[] xs, float[] ys)
    {
        var count = xs.Length;
        var slopes = new float[count - 1];
        for (var i = 0; i < count - 1; i++)
            slopes[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]);

        var tangents = new float[count];
        tangents[0] = slopes[0];
        tangents[count - 1] = slopes[count - 2];
        for (var i = 1; i < count - 1; i++)
        {
            if (slopes[i - 1] * slopes[i] <= 0f)
                tangents[i] = 0f;                       // local extremum: flat tangent, no overshoot
            else
                tangents[i] = (slopes[i - 1] + slopes[i]) / 2f;
        }

        // Fritsch-Carlson limiter: keep each tangent inside the circle of radius 3 around the slope.
        for (var i = 0; i < count - 1; i++)
        {
            if (slopes[i] == 0f)
            {
                tangents[i] = 0f;
                tangents[i + 1] = 0f;
                continue;
            }

            var a = tangents[i] / slopes[i];
            var b = tangents[i + 1] / slopes[i];
            var lengthSquared = a * a + b * b;
            if (lengthSquared > 9f)
            {
                var scale = 3f / MathF.Sqrt(lengthSquared);
                tangents[i] = scale * a * slopes[i];
                tangents[i + 1] = scale * b * slopes[i];
            }
        }

        return tangents;
    }

    private static float Evaluate(float[] xs, float[] ys, float[] tangents, float x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];

        var i = 0;
        while (i < xs.Length - 2 && x > xs[i + 1])
            i++;

        var h = xs[i + 1] - xs[i];
        var t = (x - xs[i]) / h;
        var t2 = t * t;
        var t3 = t2 * t;

        var h00 = 2f * t3 - 3f * t2 + 1f;
        var h10 = t3 - 2f * t2 + t;
        var h01 = -2f * t3 + 3f * t2;
        var h11 = t3 - t2;

        return h00 * ys[i] + h10 * h * tangents[i] + h01 * ys[i + 1] + h11 * h * tangents[i + 1];
    }

}
