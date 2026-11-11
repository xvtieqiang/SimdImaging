using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>How chroma is subsampled relative to luma.</summary>
public enum ChromaSubsampling : byte
{
    /// <summary>No subsampling.</summary>
    Yuv444,

    /// <summary>Two luma samples per chroma sample horizontally.</summary>
    Yuv422,

    /// <summary>Two by two luma samples per chroma sample.</summary>
    Yuv420,
}

/// <summary>
/// Colour-space conversion as an image operator. The source is read as sRGB, and the result is always a float
/// image: XYZ, Lab and Luv do not fit in 0..1, and squeezing them into bytes would invent a range nobody
/// agreed on. Alpha passes through untouched.
/// </summary>
public static class ColorConversionOps
{
    /// <summary>Converts every pixel into <paramref name="target"/>.</summary>
    /// <param name="source">The pixels, read as sRGB.</param>
    /// <param name="target">The colour space to express the result in.</param>
    /// <param name="ycbcr">Luma coefficients when the target is YCbCr.</param>
    /// <param name="limitedRange">Use the 16..235 video range instead of 0..255.</param>
    /// <param name="white">Reference white for XYZ, xyY, Lab, Lch, Luv, Lchuv and Hunter Lab.</param>
    /// <param name="adaptation">How XYZ moves from D65 to <paramref name="white"/>.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static WorkingBuffer Convert(PixelView source, ColorSpace target, YCbCrStandard ycbcr = YCbCrStandard.Bt709,
        bool limitedRange = false, WhitePoint white = WhitePoint.D65,
        ChromaticAdaptation adaptation = ChromaticAdaptation.Bradford, IPixelPool? pool = null)
    {
        var width = source.Width;
        var stride = width * 4;
        var destination = WorkingBuffer.Rent(width, source.Height, WorkingFormat.Rgba32F, pool);
        var row = new float[stride];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                var (first, second, third) = ConvertPixel(row[offset], row[offset + 1], row[offset + 2], target,
                    ycbcr, limitedRange, white, adaptation);

                row[offset] = first;
                row[offset + 1] = second;
                row[offset + 2] = third;
            }

            Pixels.WriteRowRaw(row, width, WorkingFormat.Rgba32F, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>
    /// Applies 4:2:2 or 4:2:0 chroma subsampling: convert to YCbCr, average chroma per cell, upsample it back
    /// bilinearly, convert to RGB. The luma plane is untouched, which is what makes the result look like a
    /// chroma-limited codec rather than a blur.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="mode">Subsampling layout.</param>
    /// <param name="ycbcr">Luma coefficients for the intermediate YCbCr.</param>
    /// <param name="limitedRange">Use the 16..235 video range.</param>
    /// <param name="bandRows">Rows per band; the chroma rows for a band plus its neighbours are held in
    /// memory.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static WorkingBuffer ChromaResample(PixelView source, ChromaSubsampling mode,
        YCbCrStandard ycbcr = YCbCrStandard.Bt601, bool limitedRange = false, int bandRows = 128,
        IPixelPool? pool = null)
    {
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var destination = WorkingBuffer.Rent(width, height, source.Format, pool);
        if (mode == ChromaSubsampling.Yuv444)
        {
            for (var y = 0; y < height; y++)
                source.Row(y).CopyTo(destination.View.Row(y));
            return destination;
        }

        // 4:2:2 halves horizontally only; 4:2:0 halves both. Reading this the other way round leaves 4:2:0
        // averaging a single column and the output unchanged, which is exactly what the first test caught.
        var horizontal = mode == ChromaSubsampling.Yuv444 ? 1 : 2;
        var vertical = mode == ChromaSubsampling.Yuv420 ? 2 : 1;

        var chromaWidth = (width + horizontal - 1) / horizontal;
        var chromaHeight = (height + vertical - 1) / vertical;

        var capacity = (bandRows / vertical + 2) * chromaWidth;
        var cb = new float[capacity];
        var cr = new float[capacity];
        var cbSum = new double[chromaWidth];
        var crSum = new double[chromaWidth];
        var counts = new int[chromaWidth];
        var row = new float[stride];

        for (var bandTop = 0; bandTop < height; bandTop += bandRows)
        {
            var bandBottom = Math.Min(height, bandTop + bandRows);

            // Only the chroma rows the bilinear tap can reach are computed for this band.
            var vFirst = Math.Clamp((bandTop + 0.5f) / vertical - 0.5f, 0f, chromaHeight - 1f);
            var vLast = Math.Clamp((bandBottom - 0.5f) / vertical - 0.5f, 0f, chromaHeight - 1f);
            var cellTop = (int)MathF.Floor(vFirst);
            var cellBottom = Math.Min((int)MathF.Floor(vLast) + 1, chromaHeight - 1);

            for (var i = 0; i <= cellBottom - cellTop; i++)
            {
                var firstRow = (cellTop + i) * vertical;
                var lastRow = Math.Min(firstRow + vertical, height);

                Array.Clear(cbSum);
                Array.Clear(crSum);
                Array.Clear(counts);

                for (var y = firstRow; y < lastRow; y++)
                {
                    Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
                    for (var x = 0; x < width; x++)
                    {
                        var offset = x * 4;
                        var (_, blue, red) = ColorMath.ToYCbCr(row[offset], row[offset + 1], row[offset + 2],
                            ycbcr, limitedRange);
                        var cell = x / horizontal;
                        cbSum[cell] += blue;
                        crSum[cell] += red;
                        counts[cell]++;
                    }
                }

                var target = i * chromaWidth;
                for (var cell = 0; cell < chromaWidth; cell++)
                {
                    if (counts[cell] == 0)
                        continue;

                    cb[target + cell] = (float)(cbSum[cell] / counts[cell]);
                    cr[target + cell] = (float)(crSum[cell] / counts[cell]);
                }
            }

            for (var y = bandTop; y < bandBottom; y++)
            {
                Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

                // Chroma cell centres sit on the luma grid at (cell + 0.5) * step - 0.5.
                var v = Math.Clamp((y + 0.5f) / vertical - 0.5f, 0f, chromaHeight - 1f);
                var v0 = (int)MathF.Floor(v);
                var v1 = Math.Min(v0 + 1, chromaHeight - 1);
                var fv = v - v0;
                var localV0 = v0 - cellTop;
                var localV1 = v1 - cellTop;

                for (var x = 0; x < width; x++)
                {
                    var u = Math.Clamp((x + 0.5f) / horizontal - 0.5f, 0f, chromaWidth - 1f);
                    var u0 = (int)MathF.Floor(u);
                    var u1 = Math.Min(u0 + 1, chromaWidth - 1);
                    var fu = u - u0;

                    var blue = Sample(cb, chromaWidth, u0, u1, localV0, localV1, fu, fv);
                    var red = Sample(cr, chromaWidth, u0, u1, localV0, localV1, fu, fv);

                    var offset = x * 4;
                    var (luma, _, _) = ColorMath.ToYCbCr(row[offset], row[offset + 1], row[offset + 2], ycbcr,
                        limitedRange);
                    var (r, g, b) = ColorMath.FromYCbCr(luma, blue, red, ycbcr, limitedRange);

                    row[offset] = Math.Clamp(r, 0f, 1f);
                    row[offset + 1] = Math.Clamp(g, 0f, 1f);
                    row[offset + 2] = Math.Clamp(b, 0f, 1f);
                }

                Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
            }
        }

        return destination;
    }

    private static float Sample(float[] plane, int width, int u0, int u1, int v0, int v1, float fu, float fv)
    {
        var top = plane[v0 * width + u0] + (plane[v0 * width + u1] - plane[v0 * width + u0]) * fu;
        var bottom = plane[v1 * width + u0] + (plane[v1 * width + u1] - plane[v1 * width + u0]) * fu;
        return top + (bottom - top) * fv;
    }

    private static (float First, float Second, float Third) ConvertPixel(float r, float g, float b, ColorSpace target,
        YCbCrStandard ycbcr, bool limitedRange, WhitePoint white, ChromaticAdaptation adaptation)
    {
        switch (target)
        {
            case ColorSpace.Hsl:
                return ColorMath.ToHsl(r, g, b);

            case ColorSpace.Hsv:
                return ColorMath.ToHsv(r, g, b);

            case ColorSpace.YCbCr:
                return ColorMath.ToYCbCr(r, g, b, ycbcr, limitedRange);

            case ColorSpace.Lms:
                return ColorMath.ToLms(r, g, b);
        }

        // Everything below is derived from XYZ. A non-D65 reference white means the tristimulus is adapted
        // first — that is what "Lab under D50" means for an sRGB pixel, not just relabelling the white.
        var (x, y, z) = ColorMath.ToXyz(r, g, b);
        if (white != WhitePoint.D65)
            (x, y, z) = ColorMath.Adapt(x, y, z, WhitePoint.D65, white, adaptation);

        switch (target)
        {
            case ColorSpace.Xyz:
                return (x, y, z);

            case ColorSpace.Xyy:
            {
                var (cx, cy, luminance) = ColorMath.ToXyy(x, y, z);
                return (cx, cy, luminance);
            }

            case ColorSpace.Lab:
            {
                var (l, a, bb) = ColorMath.XyzToLab(x, y, z, white);
                return (l, a, bb);
            }

            case ColorSpace.Lch:
            {
                var lab = ColorMath.XyzToLab(x, y, z, white);
                var (l, c, h) = ColorMath.ToLch(lab.L, lab.A, lab.B);
                return (l, c, h);
            }

            case ColorSpace.Luv:
            {
                var (l, u, v) = ColorMath.XyzToLuv(x, y, z, white);
                return (l, u, v);
            }

            case ColorSpace.Lchuv:
            {
                var luv = ColorMath.XyzToLuv(x, y, z, white);
                var (l, c, h) = ColorMath.ToLchuv(luv.L, luv.U, luv.V);
                return (l, c, h);
            }

            case ColorSpace.HunterLab:
                return ColorMath.XyzToHunterLab(x, y, z, white);

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown colour space.");
        }
    }
}
