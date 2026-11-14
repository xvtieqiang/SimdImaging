using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// CIEDE2000 colour difference (Sharma, Wu and Dalal, 2005) — the metric a colour-management claim is
/// judged by. Two forms: the formula on one pair of CIE L*a*b* colours, and the mean/worst over two
/// images.
/// <para>
/// The image form reads both images as sRGB and converts each pixel to Lab under D65, which is the
/// white sRGB is defined against. Alpha is not part of a colour, so it is not compared — the same rule
/// <see cref="AnalysisOps.Ssim"/> follows.
/// </para>
/// <para>
/// Degrees-and-radians details are where implementations drift: the hue average has a discontinuity at
/// 180°, the Δh′ term is zero when either chroma is zero, and the rotation term is a sine of twice
/// Δθ. The reference pairs in the test suite are the paper's own table, including the four cases that
/// sit exactly on that discontinuity.
/// </para>
/// </summary>
public static class ColorDifference
{
    /// <summary>The CIEDE2000 difference between two CIE L*a*b* colours, with kL = kC = kH = 1.</summary>
    public static double DeltaE2000(double l1, double a1, double b1, double l2, double a2, double b2)
    {
        const double Pow25To7 = 6103515625.0;   // 25^7

        var c1 = Math.Sqrt(a1 * a1 + b1 * b1);
        var c2 = Math.Sqrt(a2 * a2 + b2 * b2);
        var cMean = (c1 + c2) / 2.0;
        var cMean7 = Math.Pow(cMean, 7.0);
        var g = 0.5 * (1.0 - Math.Sqrt(cMean7 / (cMean7 + Pow25To7)));

        var a1Prime = (1.0 + g) * a1;
        var a2Prime = (1.0 + g) * a2;
        var c1Prime = Math.Sqrt(a1Prime * a1Prime + b1 * b1);
        var c2Prime = Math.Sqrt(a2Prime * a2Prime + b2 * b2);

        var h1Prime = HueAngle(b1, a1Prime);
        var h2Prime = HueAngle(b2, a2Prime);

        var deltaL = l2 - l1;
        var deltaC = c2Prime - c1Prime;
        var deltaHue = h2Prime - h1Prime;

        if (c1Prime * c2Prime == 0.0)
            deltaHue = 0.0;
        else if (deltaHue > 180.0)
            deltaHue -= 360.0;
        else if (deltaHue < -180.0)
            deltaHue += 360.0;

        var deltaH = 2.0 * Math.Sqrt(c1Prime * c2Prime) * Math.Sin(ToRadians(deltaHue) / 2.0);

        var lMean = (l1 + l2) / 2.0;
        var cPrimeMean = (c1Prime + c2Prime) / 2.0;

        double hMean;
        if (c1Prime * c2Prime == 0.0)
            hMean = h1Prime + h2Prime;
        else if (Math.Abs(h1Prime - h2Prime) <= 180.0)
            hMean = (h1Prime + h2Prime) / 2.0;
        else if (h1Prime + h2Prime < 360.0)
            hMean = (h1Prime + h2Prime + 360.0) / 2.0;
        else
            hMean = (h1Prime + h2Prime - 360.0) / 2.0;

        var t = 1.0
                - 0.17 * Math.Cos(ToRadians(hMean - 30.0))
                + 0.24 * Math.Cos(ToRadians(2.0 * hMean))
                + 0.32 * Math.Cos(ToRadians(3.0 * hMean + 6.0))
                - 0.20 * Math.Cos(ToRadians(4.0 * hMean - 63.0));

        var deltaTheta = 30.0 * Math.Exp(-Math.Pow((hMean - 275.0) / 25.0, 2.0));
        var cPrimeMean7 = Math.Pow(cPrimeMean, 7.0);
        var rotationC = 2.0 * Math.Sqrt(cPrimeMean7 / (cPrimeMean7 + Pow25To7));

        var lightnessTerm = 1.0 + (0.015 * Math.Pow(lMean - 50.0, 2.0)) / Math.Sqrt(20.0 + Math.Pow(lMean - 50.0, 2.0));
        var chromaTerm = 1.0 + 0.045 * cPrimeMean;
        var hueTerm = 1.0 + 0.015 * cPrimeMean * t;
        var rotation = -Math.Sin(ToRadians(2.0 * deltaTheta)) * rotationC;

        var l = deltaL / lightnessTerm;
        var c = deltaC / chromaTerm;
        var h = deltaH / hueTerm;

        return Math.Sqrt(l * l + c * c + h * h + rotation * c * h);
    }

    /// <summary>
    /// Mean and worst CIEDE2000 between two images of the same size. Both are read as sRGB and
    /// compared in Lab under D65.
    /// </summary>
    /// <exception cref="ArgumentException">The images differ in size.</exception>
    public static (double Mean, double Max) DeltaE2000(PixelView a, PixelView b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new ArgumentException($"The images differ in size: {a.Width}x{a.Height} against {b.Width}x{b.Height}.");

        var width = a.Width;
        var left = FloatScratch.Rent(width * 4);
        var right = FloatScratch.Rent(width * 4);

        try
        {
            double sum = 0.0;
            double worst = 0.0;

            for (var y = 0; y < a.Height; y++)
            {
                Pixels.ReadRowRaw(a.Row(y), width, a.Format, left);
                Pixels.ReadRowRaw(b.Row(y), width, b.Format, right);

                for (var x = 0; x < width; x++)
                {
                    var offset = x * 4;
                    var (l1, a1, b1) = ColorMath.ToLab(left[offset], left[offset + 1], left[offset + 2], WhitePoint.D65);
                    var (l2, a2, b2) = ColorMath.ToLab(right[offset], right[offset + 1], right[offset + 2], WhitePoint.D65);

                    var difference = DeltaE2000(l1, a1, b1, l2, a2, b2);
                    sum += difference;
                    if (difference > worst)
                        worst = difference;
                }
            }

            return (sum / ((double)width * a.Height), worst);
        }
        finally
        {
            FloatScratch.Return(left);
            FloatScratch.Return(right);
        }
    }

    /// <summary>atan2 in degrees, folded into [0, 360).</summary>
    private static double HueAngle(double b, double aPrime)
    {
        if (b == 0.0 && aPrime == 0.0)
            return 0.0;

        var degrees = ToDegrees(Math.Atan2(b, aPrime));
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
