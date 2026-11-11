using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>Which colour vision deficiency to simulate.</summary>
public enum ColorVisionDeficiency : byte
{
    /// <summary>No red cone response: reds darken and confuse with greens.</summary>
    Protanopia,

    /// <summary>No green cone response, the most common form.</summary>
    Deuteranopia,

    /// <summary>No blue cone response: blues and greens collapse.</summary>
    Tritanopia,

    /// <summary>No colour at all; only luminance survives.</summary>
    Achromatopsia,
}

/// <summary>
/// Colour vision deficiency simulation in LMS cone space, with an optional daltonisation pass that pushes the
/// lost difference into channels the viewer can still see.
/// <para>
/// Correction is not the inverse of the simulation matrix: at full severity that matrix is singular (one cone
/// response is replaced, not scaled), so there is nothing to invert. Daltonisation shifts the error instead,
/// which is what the term has always meant.
/// </para>
/// </summary>
public static class ColorBlindnessOps
{
    // Hunt-Pointer-Estevez, normalised for D65.
    private static readonly float[] RgbToLms =
    [
        0.31399022f, 0.63951294f, 0.04649755f,
        0.15537241f, 0.75789446f, 0.08670142f,
        0.01775239f, 0.10944209f, 0.87256922f,
    ];

    private static readonly float[] LmsToRgb =
    [
        5.47221206f, -4.6419601f, 0.16963708f,
        -1.1252419f, 2.29317094f, -0.1678952f,
        0.02980165f, -0.19318073f, 1.16364789f,
    ];

    private static readonly float[] ProtanLms =
    [
        0f, 2.02344f, -2.52581f,
        0f, 1f, 0f,
        0f, 0f, 1f,
    ];

    private static readonly float[] DeutanLms =
    [
        1f, 0f, 0f,
        0.494207f, 0f, 1.24827f,
        0f, 0f, 1f,
    ];

    private static readonly float[] TritanLms =
    [
        1f, 0f, 0f,
        0f, 1f, 0f,
        -0.395913f, 0.801109f, 0f,
    ];

    /// <summary>Simulates (or compensates for) a colour vision deficiency.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="kind">Which deficiency.</param>
    /// <param name="severity">0 is normal vision, 1 is full dichromacy.</param>
    /// <param name="correct">Daltonise: keep the simulation's error but move it where the viewer can see it.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is outside 0..1.</exception>
    public static WorkingBuffer ColorBlindness(PixelView source,
        ColorVisionDeficiency kind = ColorVisionDeficiency.Protanopia,
        float severity = 1f, bool correct = false, IPixelPool? pool = null)
    {
        if (!(severity >= 0f) || severity > 1f)
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Severity must be in [0, 1].");

        var width = source.Width;
        var stride = width * 4;
        var destination = WorkingBuffer.Rent(width, source.Height, source.Format, pool);
        var row = new float[stride];

        // Both the simulation and the daltonisation are 3×3 on RGB, so the LMS round trip is folded once into
        // a single matrix per call.
        var matrix = kind == ColorVisionDeficiency.Achromatopsia ? null : Compose(kind, severity);

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                var r = row[offset];
                var g = row[offset + 1];
                var b = row[offset + 2];

                if (kind == ColorVisionDeficiency.Achromatopsia)
                {
                    var luma = 0.299f * r + 0.587f * g + 0.114f * b;
                    r += (luma - r) * severity;
                    g += (luma - g) * severity;
                    b += (luma - b) * severity;
                }
                else
                {
                    var simulatedR = matrix![0] * r + matrix[1] * g + matrix[2] * b;
                    var simulatedG = matrix[3] * r + matrix[4] * g + matrix[5] * b;
                    var simulatedB = matrix[6] * r + matrix[7] * g + matrix[8] * b;

                    if (correct)
                    {
                        // Daltonise: red is left as it is and the difference the deficiency loses is pushed
                        // into green and blue, the two channels a protan or deutan still separates.
                        var errorR = r - simulatedR;
                        var errorG = g - simulatedG;
                        var errorB = b - simulatedB;
                        g += 0.7f * errorR + errorG;
                        b += 0.7f * errorR + errorB;
                    }
                    else
                    {
                        r = simulatedR;
                        g = simulatedG;
                        b = simulatedB;
                    }
                }

                row[offset] = Math.Clamp(r, 0f, 1f);
                row[offset + 1] = Math.Clamp(g, 0f, 1f);
                row[offset + 2] = Math.Clamp(b, 0f, 1f);
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>Folds <c>LMS→RGB · deficiency(severity) · RGB→LMS</c> into one row-major 3×3.</summary>
    private static float[] Compose(ColorVisionDeficiency kind, float severity)
    {
        var deficiency = kind switch
        {
            ColorVisionDeficiency.Protanopia => ProtanLms,
            ColorVisionDeficiency.Deuteranopia => DeutanLms,
            _ => TritanLms,
        };

        // Identity at severity 0, the published matrix at 1.
        var lms = new float[9];
        for (var i = 0; i < 9; i++)
            lms[i] = (i % 4 == 0 ? 1f : 0f) + (deficiency[i] - (i % 4 == 0 ? 1f : 0f)) * severity;

        var rgb = new float[9];
        Multiply(LmsToRgb, lms, rgb, 3, 3, 3);
        var result = new float[9];
        Multiply(rgb, RgbToLms, result, 3, 3, 3);
        return result;
    }

    private static void Multiply(float[] a, float[] b, float[] destination, int rows, int inner, int columns)
    {
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var sum = 0f;
                for (var k = 0; k < inner; k++)
                    sum += a[r * inner + k] * b[k * columns + c];
                destination[r * columns + c] = sum;
            }
        }
    }
}
