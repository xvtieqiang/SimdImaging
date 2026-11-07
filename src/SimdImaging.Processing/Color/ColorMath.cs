namespace SimdImaging.Processing;

/// <summary>A colour space a triple can be expressed in.</summary>
public enum ColorSpace : byte
{
    /// <summary>CIE XYZ, Y normalised so the reference white has Y = 1.</summary>
    Xyz,

    /// <summary>CIE xyY: chromaticity plus luminance.</summary>
    Xyy,

    /// <summary>CIE L*a*b*: L 0..100, a/b roughly ±128.</summary>
    Lab,

    /// <summary>CIE LCh: Lab in polar form, hue in degrees.</summary>
    Lch,

    /// <summary>CIE L*u*v*: L 0..100, u/v roughly ±200.</summary>
    Luv,

    /// <summary>CIE LCh(uv): Luv in polar form, hue in degrees.</summary>
    Lchuv,

    /// <summary>Hunter Lab, the 1948 square-root variant.</summary>
    HunterLab,

    /// <summary>Long/medium/short cone response (Hunt-Pointer-Estevez, D65).</summary>
    Lms,

    /// <summary>Hue, saturation, lightness — all in 0..1.</summary>
    Hsl,

    /// <summary>Hue, saturation, value — all in 0..1.</summary>
    Hsv,

    /// <summary>Luma plus two colour differences; chroma is centred on 0.5.</summary>
    YCbCr,
}

/// <summary>The white a colour space is referred to.</summary>
public enum WhitePoint : byte
{
    /// <summary>D65, the sRGB and Rec. 709 white (6504 K).</summary>
    D65,

    /// <summary>D50, the ICC profile connection space white (5003 K).</summary>
    D50,
}

/// <summary>How a colour adapts from one white point to another.</summary>
public enum ChromaticAdaptation : byte
{
    /// <summary>Bradford: the cone-response matrix that fits published corresponding-colour data best.</summary>
    Bradford,

    /// <summary>Von Kries: plain ratio scaling in Hunt-Pointer-Estevez cone space.</summary>
    VonKries,
}

/// <summary>Which luma coefficients <see cref="ColorSpace.YCbCr"/> uses.</summary>
public enum YCbCrStandard : byte
{
    /// <summary>BT.601: Kr 0.299, Kb 0.114 — SD video and JPEG's usual choice.</summary>
    Bt601,

    /// <summary>BT.709: Kr 0.2126, Kb 0.0722 — HD video and sRGB's luma.</summary>
    Bt709,

    /// <summary>BT.2020: Kr 0.2627, Kb 0.0593 — UHD.</summary>
    Bt2020,
}

/// <summary>
/// Scalar colour-space conversions. Input and output RGB triples are <b>sRGB-encoded</b> in 0..1 unless the
/// method name says otherwise: the linearisation happens inside, because that is what a caller means by "this
/// pixel's Lab value".
/// <para>
/// Unbounded spaces (XYZ, Lab, Luv, LMS) return their natural units — L in 0..100, a/b around zero — which is
/// why the image operator writes them into a float buffer instead of a byte one.
/// </para>
/// </summary>
public static class ColorMath
{
    // sRGB linear RGB → XYZ, D65.
    private static readonly float[] RgbToXyzMatrix =
    [
        0.4124564f, 0.3575761f, 0.1804375f,
        0.2126729f, 0.7151522f, 0.0721750f,
        0.0193339f, 0.1191920f, 0.9503041f,
    ];

    private static readonly float[] XyzToRgbMatrix =
    [
        3.2404542f, -1.5371385f, -0.4985314f,
        -0.9692660f, 1.8760108f, 0.0415560f,
        0.0556434f, -0.2040259f, 1.0572252f,
    ];

    private static readonly float[] BradfordMatrix =
    [
        0.8951f, 0.2664f, -0.1614f,
        -0.7502f, 1.7135f, 0.0367f,
        0.0389f, -0.0685f, 1.0296f,
    ];

    private static readonly float[] BradfordInverse =
    [
        0.9869929f, -0.1470543f, 0.1599627f,
        0.4323053f, 0.5183603f, 0.0492912f,
        -0.0085287f, 0.0400428f, 0.9684867f,
    ];

    private static readonly float[] VonKriesMatrix =
    [
        0.40024f, 0.70760f, -0.08081f,
        -0.22630f, 1.16532f, 0.04570f,
        0.00000f, 0.00000f, 0.91822f,
    ];

    private static readonly float[] VonKriesInverse =
    [
        1.8599364f, -1.1293816f, 0.2198974f,
        0.3611914f, 0.6388125f, -0.0000064f,
        0.0000000f, 0.0000000f, 1.0890636f,
    ];

    /// <summary>The reference white's XYZ, with Y = 1.</summary>
    public static (float X, float Y, float Z) WhiteXyz(WhitePoint white) => white switch
    {
        WhitePoint.D50 => (0.96422f, 1f, 0.82521f),
        _ => (0.95047f, 1f, 1.08883f),
    };

    /// <summary>Decodes one sRGB channel to linear light.</summary>
    public static float SrgbToLinear(float value) => value <= 0.04045f
        ? value / 12.92f
        : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    /// <summary>Decodes a PQ (ST 2084) code value into linear light, where 1.0 is 10 000 cd/m².</summary>
    public static float PqDecode(float encoded)
    {
        const float M1 = 0.1593017578125f;    // 2610 / 16384
        const float M2 = 78.84375f;           // 2523 / 4096 * 128
        const float C1 = 0.8359375f;          // 3424 / 4096
        const float C2 = 18.8515625f;         // 2413 / 4096 * 32
        const float C3 = 18.6875f;            // 2392 / 4096 * 32

        var power = MathF.Pow(MathF.Max(encoded, 0f), 1f / M2);
        var numerator = MathF.Max(power - C1, 0f);
        var denominator = C2 - C3 * power;
        return denominator <= 0f ? 1f : MathF.Pow(numerator / denominator, 1f / M1);
    }

    /// <summary>Encodes linear light (1.0 = 10 000 cd/m²) as a PQ code value.</summary>
    public static float PqEncode(float linear)
    {
        const float M1 = 0.1593017578125f;
        const float M2 = 78.84375f;
        const float C1 = 0.8359375f;
        const float C2 = 18.8515625f;
        const float C3 = 18.6875f;

        var power = MathF.Pow(MathF.Max(linear, 0f), M1);
        return MathF.Pow((C1 + C2 * power) / (1f + C3 * power), M2);
    }

    /// <summary>Encodes one linear channel to sRGB.</summary>
    public static float LinearToSrgb(float value) => value <= 0.0031308f
        ? value * 12.92f
        : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;

    // ---- XYZ and the spaces derived from it ---------------------------------------------------------

    /// <summary>sRGB → CIE XYZ (D65).</summary>
    public static (float X, float Y, float Z) ToXyz(float r, float g, float b)
    {
        var lr = SrgbToLinear(r);
        var lg = SrgbToLinear(g);
        var lb = SrgbToLinear(b);

        return (
            RgbToXyzMatrix[0] * lr + RgbToXyzMatrix[1] * lg + RgbToXyzMatrix[2] * lb,
            RgbToXyzMatrix[3] * lr + RgbToXyzMatrix[4] * lg + RgbToXyzMatrix[5] * lb,
            RgbToXyzMatrix[6] * lr + RgbToXyzMatrix[7] * lg + RgbToXyzMatrix[8] * lb);
    }

    /// <summary>CIE XYZ (D65) → sRGB.</summary>
    public static (float R, float G, float B) FromXyz(float x, float y, float z)
    {
        var lr = XyzToRgbMatrix[0] * x + XyzToRgbMatrix[1] * y + XyzToRgbMatrix[2] * z;
        var lg = XyzToRgbMatrix[3] * x + XyzToRgbMatrix[4] * y + XyzToRgbMatrix[5] * z;
        var lb = XyzToRgbMatrix[6] * x + XyzToRgbMatrix[7] * y + XyzToRgbMatrix[8] * z;

        return (LinearToSrgb(lr), LinearToSrgb(lg), LinearToSrgb(lb));
    }

    /// <summary>sRGB → CIE L*a*b* under <paramref name="white"/>.</summary>
    public static (float L, float A, float B) ToLab(float r, float g, float b, WhitePoint white = WhitePoint.D65)
    {
        var (x, y, z) = ToXyz(r, g, b);
        return XyzToLab(x, y, z, white);
    }

    /// <summary>CIE L*a*b* → sRGB.</summary>
    public static (float R, float G, float B) FromLab(float l, float a, float bb, WhitePoint white = WhitePoint.D65)
    {
        var (x, y, z) = LabToXyz(l, a, bb, white);
        return FromXyz(x, y, z);
    }

    /// <summary>CIE XYZ → CIE L*a*b*.</summary>
    public static (float L, float A, float B) XyzToLab(float x, float y, float z, WhitePoint white = WhitePoint.D65)
    {
        var (wx, wy, wz) = WhiteXyz(white);
        var fx = LabF(x / wx);
        var fy = LabF(y / wy);
        var fz = LabF(z / wz);

        return (116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    /// <summary>CIE L*a*b* → CIE XYZ.</summary>
    public static (float X, float Y, float Z) LabToXyz(float l, float a, float b, WhitePoint white = WhitePoint.D65)
    {
        var (wx, wy, wz) = WhiteXyz(white);

        var fy = (l + 16f) / 116f;
        var fx = fy + a / 500f;
        var fz = fy - b / 200f;

        return (wx * LabFInverse(fx), wy * LabFInverse(fy), wz * LabFInverse(fz));
    }

    /// <summary>Lab → LCh: hue in degrees, chroma in the a/b plane.</summary>
    public static (float L, float C, float H) ToLch(float l, float a, float b)
    {
        var chroma = MathF.Sqrt(a * a + b * b);
        var hue = MathF.Atan2(b, a) * (180f / MathF.PI);
        if (hue < 0f)
            hue += 360f;
        return (l, chroma, hue);
    }

    /// <summary>LCh → Lab.</summary>
    public static (float L, float A, float B) FromLch(float l, float c, float h)
    {
        var radians = h * (MathF.PI / 180f);
        return (l, c * MathF.Cos(radians), c * MathF.Sin(radians));
    }

    /// <summary>sRGB → CIE L*u*v*.</summary>
    public static (float L, float U, float V) ToLuv(float r, float g, float b, WhitePoint white = WhitePoint.D65)
    {
        var (x, y, z) = ToXyz(r, g, b);
        return XyzToLuv(x, y, z, white);
    }

    /// <summary>CIE XYZ → CIE L*u*v*.</summary>
    public static (float L, float U, float V) XyzToLuv(float x, float y, float z, WhitePoint white = WhitePoint.D65)
    {
        var (wx, wy, wz) = WhiteXyz(white);

        var denominator = x + 15f * y + 3f * z;
        var whiteDenominator = wx + 15f * wy + 3f * wz;

        var uPrime = denominator > 0f ? 4f * x / denominator : 0f;
        var vPrime = denominator > 0f ? 9f * y / denominator : 0f;
        var un = 4f * wx / whiteDenominator;
        var vn = 9f * wy / whiteDenominator;

        var fy = LabF(y / wy);
        var l = 116f * fy - 16f;

        return (l, 13f * l * (uPrime - un), 13f * l * (vPrime - vn));
    }

    /// <summary>CIE L*u*v* → sRGB.</summary>
    public static (float R, float G, float B) FromLuv(float l, float u, float v, WhitePoint white = WhitePoint.D65)
    {
        var (x, y, z) = LuvToXyz(l, u, v, white);
        return FromXyz(x, y, z);
    }

    /// <summary>CIE L*u*v* → CIE XYZ.</summary>
    public static (float X, float Y, float Z) LuvToXyz(float l, float u, float v, WhitePoint white = WhitePoint.D65)
    {
        var (wx, wy, wz) = WhiteXyz(white);
        var whiteDenominator = wx + 15f * wy + 3f * wz;
        var un = 4f * wx / whiteDenominator;
        var vn = 9f * wy / whiteDenominator;

        if (l <= 0f)
            return (0f, 0f, 0f);

        var uPrime = u / (13f * l) + un;
        var vPrime = v / (13f * l) + vn;

        var y = wy * LabFInverse((l + 16f) / 116f);
        if (vPrime == 0f)
            return (0f, y, 0f);

        var x = y * 9f * uPrime / (4f * vPrime);
        var z = y * (12f - 3f * uPrime - 20f * vPrime) / (4f * vPrime);
        return (x, y, z);
    }

    /// <summary>Luv → LCh(uv).</summary>
    public static (float L, float C, float H) ToLchuv(float l, float u, float v)
    {
        var chroma = MathF.Sqrt(u * u + v * v);
        var hue = MathF.Atan2(v, u) * (180f / MathF.PI);
        if (hue < 0f)
            hue += 360f;
        return (l, chroma, hue);
    }

    /// <summary>LCh(uv) → Luv.</summary>
    public static (float L, float U, float V) FromLchuv(float l, float c, float h)
    {
        var radians = h * (MathF.PI / 180f);
        return (l, c * MathF.Cos(radians), c * MathF.Sin(radians));
    }

    /// <summary>sRGB → Hunter Lab.</summary>
    public static (float L, float A, float B) ToHunterLab(float r, float g, float b, WhitePoint white = WhitePoint.D65)
    {
        var (x, y, z) = ToXyz(r, g, b);
        return XyzToHunterLab(x, y, z, white);
    }

    /// <summary>CIE XYZ → Hunter Lab.</summary>
    public static (float L, float A, float B) XyzToHunterLab(float x, float y, float z,
        WhitePoint white = WhitePoint.D65)
    {
        var (wx, wy, wz) = WhiteXyz(white);

        var sqrtY = MathF.Sqrt(MathF.Max(y / wy, 0f));
        if (sqrtY <= 0f)
            return (0f, 0f, 0f);

        var ka = 175f / 198.04f * (wx + wy);
        var kb = 70f / 218.11f * (wy + wz);

        return (100f * sqrtY, ka * (x / wx - y / wy) / sqrtY, kb * (y / wy - z / wz) / sqrtY);
    }

    /// <summary>CIE XYZ → xyY.</summary>
    public static (float X, float Y, float Lum) ToXyy(float x, float y, float z)
    {
        var sum = x + y + z;
        return sum <= 0f ? (0f, 0f, 0f) : (x / sum, y / sum, y);
    }

    /// <summary>xyY → CIE XYZ.</summary>
    public static (float X, float Y, float Z) FromXyy(float x, float y, float luminance)
    {
        if (y <= 0f)
            return (0f, 0f, 0f);

        return (x * luminance / y, luminance, (1f - x - y) * luminance / y);
    }

    /// <summary>sRGB → LMS (Hunt-Pointer-Estevez, D65).</summary>
    public static (float L, float M, float S) ToLms(float r, float g, float b)
    {
        var lr = SrgbToLinear(r);
        var lg = SrgbToLinear(g);
        var lb = SrgbToLinear(b);

        return (
            0.31399022f * lr + 0.63951294f * lg + 0.04649755f * lb,
            0.15537241f * lr + 0.75789446f * lg + 0.08670142f * lb,
            0.01775239f * lr + 0.10944209f * lg + 0.87256922f * lb);
    }

    /// <summary>LMS → sRGB.</summary>
    public static (float R, float G, float B) FromLms(float l, float m, float s)
    {
        var lr = 5.47221206f * l - 4.6419601f * m + 0.16963708f * s;
        var lg = -1.1252419f * l + 2.29317094f * m - 0.1678952f * s;
        var lb = 0.02980165f * l - 0.19318073f * m + 1.16364789f * s;

        return (LinearToSrgb(lr), LinearToSrgb(lg), LinearToSrgb(lb));
    }

    // ---- spaces defined on the encoded triple --------------------------------------------------------

    /// <summary>sRGB → HSL. Hue in 0..360, saturation and lightness in 0..1.</summary>
    public static (float H, float S, float L) ToHsl(float r, float g, float b)
    {
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var lightness = (max + min) * 0.5f;
        var delta = max - min;

        if (delta <= 0f)
            return (0f, 0f, lightness);

        var saturation = lightness > 0.5f ? delta / (2f - max - min) : delta / (max + min);
        return (Hue(r, g, b, max, delta), saturation, lightness);
    }

    /// <summary>HSL → sRGB.</summary>
    public static (float R, float G, float B) FromHsl(float h, float s, float l)
    {
        if (s <= 0f)
            return (l, l, l);

        var q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        var p = 2f * l - q;
        var hk = h / 360f;

        return (HueToChannel(p, q, hk + 1f / 3f), HueToChannel(p, q, hk), HueToChannel(p, q, hk - 1f / 3f));
    }

    /// <summary>sRGB → HSV. Hue in 0..360, saturation and value in 0..1.</summary>
    public static (float H, float S, float V) ToHsv(float r, float g, float b)
    {
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var delta = max - min;

        if (delta <= 0f)
            return (0f, 0f, max);

        return (Hue(r, g, b, max, delta), delta / max, max);
    }

    /// <summary>HSV → sRGB.</summary>
    public static (float R, float G, float B) FromHsv(float h, float s, float v)
    {
        if (s <= 0f)
            return (v, v, v);

        var sector = h / 60f;
        var index = (int)MathF.Floor(sector) % 6;
        var fraction = sector - MathF.Floor(sector);

        var p = v * (1f - s);
        var q = v * (1f - s * fraction);
        var t = v * (1f - s * (1f - fraction));

        return index switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    /// <summary>sRGB → YCbCr; full range keeps Y in 0..1 and chroma centred on 0.5.</summary>
    public static (float Y, float Cb, float Cr) ToYCbCr(float r, float g, float b,
        YCbCrStandard standard = YCbCrStandard.Bt709, bool limitedRange = false)
    {
        var (kr, kb) = Coefficients(standard);
        var kg = 1f - kr - kb;

        var luma = kr * r + kg * g + kb * b;
        var cb = (b - luma) / (2f * (1f - kb)) + 0.5f;
        var cr = (r - luma) / (2f * (1f - kr)) + 0.5f;

        if (!limitedRange)
            return (luma, cb, cr);

        return (16f / 255f + luma * 219f / 255f,
            128f / 255f + (cb - 0.5f) * 224f / 255f,
            128f / 255f + (cr - 0.5f) * 224f / 255f);
    }

    /// <summary>YCbCr → sRGB.</summary>
    public static (float R, float G, float B) FromYCbCr(float y, float cb, float cr,
        YCbCrStandard standard = YCbCrStandard.Bt709, bool limitedRange = false)
    {
        if (limitedRange)
        {
            y = (y - 16f / 255f) * 255f / 219f;
            cb = (cb - 128f / 255f) * 255f / 224f + 0.5f;
            cr = (cr - 128f / 255f) * 255f / 224f + 0.5f;
        }

        var (kr, kb) = Coefficients(standard);
        var kg = 1f - kr - kb;

        var b = y + 2f * (1f - kb) * (cb - 0.5f);
        var r = y + 2f * (1f - kr) * (cr - 0.5f);
        var g = (y - kr * r - kb * b) / kg;
        return (r, g, b);
    }

    // ---- chromatic adaptation ------------------------------------------------------------------------

    /// <summary>Adapts an XYZ tristimulus from one white to another.</summary>
    public static (float X, float Y, float Z) Adapt(float x, float y, float z, WhitePoint from, WhitePoint to,
        ChromaticAdaptation method = ChromaticAdaptation.Bradford)
    {
        if (from == to)
            return (x, y, z);

        var matrix = method == ChromaticAdaptation.Bradford ? BradfordMatrix : VonKriesMatrix;
        var inverse = method == ChromaticAdaptation.Bradford ? BradfordInverse : VonKriesInverse;

        var sourceWhite = WhiteXyz(from);
        var targetWhite = WhiteXyz(to);

        var source = Multiply(matrix, sourceWhite.X, sourceWhite.Y, sourceWhite.Z);
        var target = Multiply(matrix, targetWhite.X, targetWhite.Y, targetWhite.Z);

        var cone = Multiply(matrix, x, y, z);
        var adapted = (
            cone.X * (target.X / source.X),
            cone.Y * (target.Y / source.Y),
            cone.Z * (target.Z / source.Z));

        return Multiply(inverse, adapted.Item1, adapted.Item2, adapted.Item3);
    }

    private static (float X, float Y, float Z) Multiply(float[] matrix, float a, float b, float c) => (
        matrix[0] * a + matrix[1] * b + matrix[2] * c,
        matrix[3] * a + matrix[4] * b + matrix[5] * c,
        matrix[6] * a + matrix[7] * b + matrix[8] * c);

    private static (float Kr, float Kb) Coefficients(YCbCrStandard standard) => standard switch
    {
        YCbCrStandard.Bt601 => (0.299f, 0.114f),
        YCbCrStandard.Bt2020 => (0.2627f, 0.0593f),
        _ => (0.2126f, 0.0722f),
    };

    /// <summary>The common hue formula for both HSL and HSV.</summary>
    private static float Hue(float r, float g, float b, float max, float delta)
    {
        float hue;
        if (max == r)
            hue = (g - b) / delta + (g < b ? 6f : 0f);
        else if (max == g)
            hue = (b - r) / delta + 2f;
        else
            hue = (r - g) / delta + 4f;

        return hue * 60f;
    }

    private static float HueToChannel(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }

    private static float LabF(float t)
    {
        const float Epsilon = 216f / 24389f;   // (6/29)^3
        return t > Epsilon ? MathF.Cbrt(t) : (841f / 108f) * t + 4f / 29f;
    }

    private static float LabFInverse(float t)
    {
        const float Delta = 6f / 29f;
        return t > Delta ? t * t * t : 3f * Delta * Delta * (t - 4f / 29f);
    }
}
