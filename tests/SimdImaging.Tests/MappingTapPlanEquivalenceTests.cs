using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// P1 equivalence sweep (M8) — decision: <c>branch B</c>. The TapPlan-driven
/// <see cref="MappingTapSampler"/> matches the frozen legacy <c>Mapping.SampleBilinear</c> byte for
/// byte on Rgba8/Rgba16 (quantisation absorbs the residual), but differs by 1 ULP on isolated
/// Rgba32F pixels. Root cause (dumped by <see cref="Diagnose_DumpsBothWeightPathsForTheShearCase"/>):
/// weights, weight products, read positions and accumulation order are bit-identical, yet the JIT
/// fuses the legacy one-shot sum <c>r00*w00 + r10*w10 + r01*w01 + r11*w11</c> into FMA chains while
/// the loop-carried <c>r += tr*w</c> chain stays rounded per term — an implementation-detail asymmetry
/// that cannot be relied on across JIT versions. Therefore the legacy paths stay frozen (zero-byte
/// red line) and only the <c>…WithOptions</c> entry points consume <see cref="MappingTapSampler"/>;
/// the 1-ULP Rgba32F delta is registered in docs/known-gaps.md. A signed-zero divergence was found
/// and fixed during the sweep: the first tap seeds the accumulator so <c>(-0)·1</c> keeps its sign
/// like the legacy expression does.
/// </summary>
public class MappingTapPlanEquivalenceTests
{
    // The legacy Mapping chain samples with inline bilinear weights only, so the sweep kernel is
    // Bilinear; the other four kernels are new WithOptions capability with no legacy counterpart.
    private const Resampler SweepKernel = Resampler.Bilinear;

    private static readonly RgbaColor Background = new(10, 20, 30, 40);

    // ------------------------------------------------------------------ affine

    public static IEnumerable<object[]> Formats => new[]
    {
        new object[] { WorkingFormat.Rgba8 },
        new object[] { WorkingFormat.Rgba16 },
        new object[] { WorkingFormat.Rgba32F },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Affine_MatchesTheLegacySamplerByteForByte(WorkingFormat format)
    {
        foreach (var (name, forward, width, height) in AffineCases())
        {
            using var source = Pattern(8, 5, format);
            using var legacy = Mapping.Transform(source[0].Pixels.View, forward, width, height, Background);
            using var sweep = TransformWithTapSampler(source[0].Pixels.View, forward, width, height, Background);

            AssertSameBytes(legacy, sweep, $"affine/{name}/{format}", format);
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Perspective_MatchesTheLegacySamplerByteForByte(WorkingFormat format)
    {
        foreach (var (name, forward, width, height) in PerspectiveCases())
        {
            using var source = Pattern(8, 5, format);
            using var legacy = Mapping.Perspective(source[0].Pixels.View, forward, width, height, Background);
            using var sweep = PerspectiveWithTapSampler(source[0].Pixels.View, forward, width, height, Background);

            AssertSameBytes(legacy, sweep, $"perspective/{name}/{format}", format);
        }
    }

    // The half-pixel shift lands inverse-mapped centres exactly on sx == source.Width for a whole
    // column — the one phase where the legacy open-interval boundary (sx > W) and the unified
    // >= W convention could disagree, so it is pinned here explicitly.
    [Theory]
    [MemberData(nameof(Formats))]
    public void HalfPixelShift_HitsTheOpenBoundaryWithoutDivergence(WorkingFormat format)
    {
        // Forward translation by (0.5, 0.5); the inverse maps destination x = 8 to sx = 8 = Width.
        var forward = AffineMatrix.Translation(0.5f, 0.5f);
        using var source = Pattern(8, 5, format);
        using var legacy = Mapping.Transform(source[0].Pixels.View, forward, 10, 6, Background);
        using var sweep = TransformWithTapSampler(source[0].Pixels.View, forward, 10, 6, Background);

        AssertSameBytes(legacy, sweep, $"boundary-sx-equals-width/{format}", format);
    }

    /// <summary>Root-cause evidence for the branch-B decision: dumps both weight paths and the raw
    /// sampler outputs bit-for-bit. Deliberately fails so the dump lands in the test output; skip it
    /// in normal runs and enable it when re-investigating the 1-ULP Rgba32F divergence.</summary>
    [Fact(Skip = "root-cause evidence only — enable to dump both weight paths")]
    public void Diagnose_DumpsBothWeightPathsForTheShearCase()
    {
        var forward = new AffineMatrix(1f, 0.3f, 0f, 0.2f, 1f, 0f);
        forward.TryInvert(out var inverse);
        var sx = inverse.A * 0.5f + inverse.B * 1.5f + inverse.C;
        var sy = inverse.D * 0.5f + inverse.E * 1.5f + inverse.F;
        var x = sx - 0.5f;
        var y = sy - 0.5f;

        // Legacy inline weights.
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var tx = x - x0;
        var ty = y - y0;
        var w00 = (1f - tx) * (1f - ty);
        var w10 = tx * (1f - ty);
        var w01 = (1f - tx) * ty;
        var w11 = tx * ty;

        // TapPlan weights.
        var planX = TapPlanBuilder.ForPoint(x + 0.5, 8, Resampler.Bilinear);
        var planY = TapPlanBuilder.ForPoint(y + 0.5, 5, Resampler.Bilinear);

        var dump =
            $"sx={sx:R} sy={sy:R} x={x:R} y={y:R}\n" +
            $"legacy: x0={x0} tx={tx:R} y0={y0} ty={ty:R}\n" +
            $"legacy weights: w00={w00:R} ({BitConverter.SingleToInt32Bits(w00):X8}) " +
            $"w10={w10:R} ({BitConverter.SingleToInt32Bits(w10):X8}) " +
            $"w01={w01:R} ({BitConverter.SingleToInt32Bits(w01):X8}) " +
            $"w11={w11:R} ({BitConverter.SingleToInt32Bits(w11):X8})\n" +
            $"tap: startX={planX.Start} startY={planY.Start} centreX={x + 0.5:R} centreY={y + 0.5:R}\n" +
            string.Join("\n", planX.Weights.Select(w => $"  wx={w:R} ({BitConverter.SingleToInt32Bits(w):X8})")) + "\n" +
            string.Join("\n", planY.Weights.Select(w => $"  wy={w:R} ({BitConverter.SingleToInt32Bits(w):X8})"));

        // Raw sampler outputs on an actual pattern source.
        using var source = Pattern(8, 5, WorkingFormat.Rgba32F);
        var view = source[0].Pixels.View;
        Mapping.SampleBilinear(view, x, y, out var lr, out var lg, out var lb, out var la);
        MappingTapSampler.Sample(view, x, y, Resampler.Bilinear, out var tr2, out var tg2, out var tb2, out var ta2);
        dump += $"\nlegacy sample: r={lr:R} ({BitConverter.SingleToInt32Bits(lr):X8}) g={lg:R} ({BitConverter.SingleToInt32Bits(lg):X8}) " +
            $"b={lb:R} ({BitConverter.SingleToInt32Bits(lb):X8}) a={la:R} ({BitConverter.SingleToInt32Bits(la):X8})";
        dump += $"\ntap    sample: r={tr2:R} ({BitConverter.SingleToInt32Bits(tr2):X8}) g={tg2:R} ({BitConverter.SingleToInt32Bits(tg2):X8}) " +
            $"b={tb2:R} ({BitConverter.SingleToInt32Bits(tb2):X8}) a={ta2:R} ({BitConverter.SingleToInt32Bits(ta2):X8})";

        true.ShouldBe(false, dump);
    }

    private static IEnumerable<(string Name, AffineMatrix Forward, int Width, int Height)> AffineCases()
    {
        yield return ("identity", AffineMatrix.Identity, 8, 5);
        yield return ("fractional-translation", AffineMatrix.Translation(0.37f, 0.71f), 10, 6);
        yield return ("integer-translation", AffineMatrix.Translation(3f, 2f), 10, 6);
        yield return ("rotate-90", CentreRotation(8, 5, 90f), 8, 5);
        yield return ("rotate-180", CentreRotation(8, 5, 180f), 8, 5);
        yield return ("rotate-270", CentreRotation(8, 5, 270f), 8, 5);
        yield return ("rotate-17", CentreRotation(8, 5, 17f), 11, 9);
        yield return ("minify-0.25", CentreScale(8, 5, 0.25f, 4, 3), 4, 3);
        yield return ("magnify-4", CentreScale(8, 5, 4f, 20, 14), 20, 14);
        yield return ("shear", new AffineMatrix(1f, 0.3f, 0f, 0.2f, 1f, 0f), 12, 8);
    }

    private static IEnumerable<(string Name, float[] Forward, int Width, int Height)> PerspectiveCases()
    {
        yield return ("weak", new float[] { 1f, 0f, 0f, 0f, 1f, 0f, 0.02f, 0.01f }, 8, 5);
        // The denominator w = 1 - 0.15px - 0.12py crosses zero inside the canvas, so the far band is
        // background (w <= 1e-12) and the remaining region is heavily minified — both hard paths.
        yield return ("strong-far-band", new float[] { 1f, 0f, 0f, 0f, 1f, 0f, -0.15f, -0.12f }, 8, 5);
        yield return ("shifted", new float[] { 1f, 0f, 0.5f, 0f, 1f, 0.25f, 0f, 0f }, 10, 6);
    }

    private static AffineMatrix CentreRotation(int width, int height, float degrees) =>
        AffineMatrix.Translation(width / 2f, height / 2f)
            .Multiply(AffineMatrix.Rotation(degrees))
            .Multiply(AffineMatrix.Translation(-width / 2f, -height / 2f));

    private static AffineMatrix CentreScale(int sourceWidth, int sourceHeight, float factor, int width, int height) =>
        AffineMatrix.Translation(width / 2f, height / 2f)
            .Multiply(AffineMatrix.Scale(factor, factor))
            .Multiply(AffineMatrix.Translation(-sourceWidth / 2f, -sourceHeight / 2f));

    // ------------------------------------------------- loop replicas (verbatim bodies)

    /// <summary>Verbatim replica of <c>Mapping.Transform</c>; the only difference is that sampling
    /// goes through <see cref="MappingTapSampler"/> instead of the inline <c>SampleBilinear</c>.</summary>
    private static WorkingBuffer TransformWithTapSampler(PixelView source, AffineMatrix forward, int width, int height,
        RgbaColor background)
    {
        if (!forward.TryInvert(out var inverse))
            throw new ArgumentException("The transform is singular and cannot be inverted.", nameof(forward));

        var destination = WorkingBuffer.Rent(width, height, source.Format);
        var row = new float[width * 4];
        var (br, bg, bb, ba) = (background.R / 255f, background.G / 255f, background.B / 255f, background.A / 255f);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sx = inverse.A * (x + 0.5f) + inverse.B * (y + 0.5f) + inverse.C;
                var sy = inverse.D * (x + 0.5f) + inverse.E * (y + 0.5f) + inverse.F;
                var o = x * 4;

                if (sx < 0f || sy < 0f || sx > source.Width || sy > source.Height)
                {
                    row[o] = br;
                    row[o + 1] = bg;
                    row[o + 2] = bb;
                    row[o + 3] = ba;
                    continue;
                }

                MappingTapSampler.Sample(source, sx - 0.5f, sy - 0.5f, SweepKernel,
                    out var r, out var g, out var b, out var a);
                var inverseAlpha = a > 0f ? 1f / a : 0f;
                row[o] = r * inverseAlpha;
                row[o + 1] = g * inverseAlpha;
                row[o + 2] = b * inverseAlpha;
                row[o + 3] = a;
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>Verbatim replica of <c>Mapping.Perspective</c>; only the sampling call differs.</summary>
    private static WorkingBuffer PerspectiveWithTapSampler(PixelView source, ReadOnlySpan<float> forward, int width, int height,
        RgbaColor background)
    {
        if (forward.Length != 8)
            throw new ArgumentException("A homography needs eight parameters.", nameof(forward));

        double a = forward[0], b = forward[1], c = forward[2];
        double d = forward[3], e = forward[4], f = forward[5];
        double g = forward[6], h = forward[7];

        var ia = e - f * h;
        var ib = c * h - b;
        var ic = b * f - c * e;
        var id = f * g - d;
        var ie = a - c * g;
        var @if = c * d - a * f;
        var ig = d * h - e * g;
        var ih = b * g - a * h;
        var ii = a * e - b * d;

        var determinant = a * ia + b * id + c * ig;
        if (Math.Abs(determinant) < 1e-12)
            throw new ArgumentException("The homography is singular.", nameof(forward));

        var scale = 1.0 / determinant;
        ia *= scale; ib *= scale; ic *= scale;
        id *= scale; ie *= scale; @if *= scale;
        ig *= scale; ih *= scale; ii *= scale;

        var destination = WorkingBuffer.Rent(width, height, source.Format);
        var row = new float[width * 4];
        var (br, bg, bb, ba) = (background.R / 255f, background.G / 255f, background.B / 255f, background.A / 255f);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double px = x + 0.5;
                double py = y + 0.5;
                var w = ig * px + ih * py + ii;
                var o = x * 4;

                if (w <= 1e-12)
                {
                    row[o] = br;
                    row[o + 1] = bg;
                    row[o + 2] = bb;
                    row[o + 3] = ba;
                    continue;
                }

                var sx = (ia * px + ib * py + ic) / w;
                var sy = (id * px + ie * py + @if) / w;

                if (sx < 0 || sy < 0 || sx > source.Width || sy > source.Height)
                {
                    row[o] = br;
                    row[o + 1] = bg;
                    row[o + 2] = bb;
                    row[o + 3] = ba;
                    continue;
                }

                MappingTapSampler.Sample(source, (float)sx - 0.5f, (float)sy - 0.5f, SweepKernel,
                    out var r, out var gg, out var bl, out var al);
                var inverseAlpha = al > 0f ? 1f / al : 0f;
                row[o] = r * inverseAlpha;
                row[o + 1] = gg * inverseAlpha;
                row[o + 2] = bl * inverseAlpha;
                row[o + 3] = al;
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertSameBytes(WorkingBuffer legacy, WorkingBuffer sweep, string caseName, WorkingFormat format)
    {
        var la = legacy.View;
        var sa = sweep.View;
        la.Width.ShouldBe(sa.Width, caseName);
        la.Height.ShouldBe(sa.Height, caseName);

        for (var y = 0; y < la.Height; y++)
        {
            var legacyRow = la.Row(y);
            var sweepRow = sa.Row(y);
            for (var i = 0; i < legacyRow.Length; i++)
            {
                if (legacyRow[i] == sweepRow[i])
                    continue;

                if (format == WorkingFormat.Rgba32F)
                {
                    // Branch B: Rgba32F may diverge by at most 1 ULP per channel (JIT FMA-fusion
                    // asymmetry, registered in known-gaps.md). Anything beyond that is a real bug.
                    var l = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(legacyRow);
                    var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(sweepRow);
                    var channel = i / 4;
                    UlpDistance(l[channel], s[channel]).ShouldBeLessThanOrEqualTo(1,
                        $"{caseName}: float differs by more than 1 ULP at pixel ({channel}, {y}) " +
                        $"(legacy={l[channel]:R}, tapPlan={s[channel]:R})");
                    i += 3; // the remaining 3 bytes of this float are covered by the ULP check
                }
                else
                {
                    // Quantised formats are byte-exact (observed: the 1-ULP float residual never
                    // crosses a quantisation boundary in the sweep corpus).
                    legacyRow[i].ShouldBe(sweepRow[i],
                        $"{caseName}: byte differs at pixel ({i / 4}, {y}) channel {i % 4} " +
                        $"(legacy={legacyRow[i]}, tapPlan={sweepRow[i]})");
                }
            }
        }
    }

    /// <summary>Distance between two floats measured in representable floats (ULPs), with signed
    /// zeros mapped onto the same point.</summary>
    private static long UlpDistance(float a, float b)
    {
        if (float.IsNaN(a) || float.IsNaN(b))
            return long.MaxValue;
        var ia = BitConverter.SingleToInt32Bits(a == 0f ? 0f : a);
        var ib = BitConverter.SingleToInt32Bits(b == 0f ? 0f : b);
        if (ia < 0)
            ia = int.MinValue - ia;
        if (ib < 0)
            ib = int.MinValue - ib;
        return Math.Abs((long)ia - ib);
    }

    /// <summary>Deterministic pattern with an alpha ramp, a fully transparent band, a fully opaque
    /// band and 0/255 (or HDR/negative) channel extremes — the corners where float weight and
    /// rounding differences actually show up.</summary>
    private static Image Pattern(int width, int height, WorkingFormat format)
    {
        var image = Image.Create(width, height, format);
        var pixels = image.WritablePixels;
        var transparentBand = height / 2;

        switch (format)
        {
            case WorkingFormat.Rgba8:
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var o = (y * width + x) * 4;
                        pixels[o] = (byte)(x * 29 + 11);
                        pixels[o + 1] = (byte)(y * 47 + 3);
                        pixels[o + 2] = (byte)(x * 7 + y * 13 + 1);
                        pixels[o + 3] = y == transparentBand ? (byte)0
                            : y == transparentBand + 1 ? (byte)255
                            : (byte)((x * 31 + y * 17) & 0xFF);
                    }
                }
                break;

            case WorkingFormat.Rgba16:
            {
                var wide = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(pixels);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var o = (y * width + x) * 4;
                        wide[o] = (ushort)((x * 4093 + y * 97 + 13) & 0xFFFF);
                        wide[o + 1] = (ushort)((x * 997 + y * 631 + 7) & 0xFFFF);
                        wide[o + 2] = (ushort)((x * 631 + y * 4093 + 101) & 0xFFFF);
                        wide[o + 3] = y == transparentBand ? (ushort)0
                            : y == transparentBand + 1 ? (ushort)65535
                            : (ushort)((x * 331 + y * 67 + 1000) & 0xFFFF);
                    }
                }
                break;
            }

            default:
            {
                var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pixels);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var p = y * width + x;
                        var o = p * 4;
                        floats[o] = (p % 7) / 3f;              // includes HDR values > 1
                        floats[o + 1] = -((p % 5) / 4f);       // includes negatives
                        floats[o + 2] = (p % 11) / 8f;
                        floats[o + 3] = y == transparentBand ? 0f
                            : y == transparentBand + 1 ? 1f
                            : (p % 4) switch { 0 => 0.25f, 1 => 0.5f, 2 => 1f, _ => 2f };
                    }
                }
                break;
            }
        }

        return image;
    }
}
