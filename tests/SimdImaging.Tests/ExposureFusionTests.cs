using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Exposure fusion has no oracle, so it is checked against frames it was handed: a scene with a dark textured
/// half and a bright half, exposed three ways so that every single frame loses one end, and the fused result has
/// to keep both.
/// </summary>
public class ExposureFusionTests
{
    private const int Width = 64;
    private const int Height = 48;

    /// <summary>
    /// The point of fusion: each frame clips 20% of the scene, and the blend does not clip either end.
    /// </summary>
    [Fact]
    public void FusedFrame_KeepsBothEndsThatNoSingleFrameKeeps()
    {
        using var dark = Expose(0.35f);
        using var middle = Expose(1f);
        using var bright = Expose(2.6f);

        using var fused = Fuse(dark, middle, bright);

        var span = fused.WritablePixels;

        // The dark half is at 0.04–0.12 in scene units: only the brightest exposure lifts it off black, and only
        // the fused result must keep it from being crushed by the other two.
        var darkHalf = Mean(span, 0, Width / 2);
        darkHalf.ShouldBeGreaterThan(0.06, "the dark half must not be crushed to black");

        // The bright half is at 0.88–0.96: the bright exposure blows it out, the fused result must not.
        var brightHalf = Mean(span, Width / 2, Width);
        brightHalf.ShouldBeLessThan(0.98, "the bright half must not be blown out");

        // Clipping is compared against the frames rather than required to be zero. Laplacian-domain blending
        // overshoots slightly at strong edges - the band-pass carries the edge's energy into a wider radius - so a
        // small number of clipped pixels is a property of the method, not a defect. What must not happen is the
        // fused frame clipping like a single exposure does: each of these clips about half the scene.
        var clipped = 0;
        var total = span.Length / 4;
        for (var i = 0; i < span.Length; i += 4)
        {
            if (span[i] is 0 or 255 || span[i + 1] is 0 or 255 || span[i + 2] is 0 or 255)
                clipped++;
        }

        clipped.ShouldBeLessThan(total / 50, $"{clipped} of {total} pixels are clipped in the fused result");

        var framesClipped = Math.Max(ClippedFraction(dark), Math.Max(ClippedFraction(middle), ClippedFraction(bright)));
        framesClipped.ShouldBeGreaterThan(0.3, "the test scene must actually clip in the frames, or it proves nothing");
    }

    /// <summary>The dark half keeps its texture, which is what "lifted off black" is for.</summary>
    [Fact]
    public void DarkHalfKeepsItsTexture()
    {
        using var dark = Expose(0.35f);
        using var middle = Expose(1f);
        using var bright = Expose(2.6f);

        using var fused = Fuse(dark, middle, bright);

        var fusedContrast = LocalContrast(fused.WritablePixels, 4, 20, 8, 16);
        var bestSingle = Math.Max(
            LocalContrast(dark.WritablePixels, 4, 20, 8, 16),
            Math.Max(LocalContrast(middle.WritablePixels, 4, 20, 8, 16),
                LocalContrast(bright.WritablePixels, 4, 20, 8, 16)));

        fusedContrast.ShouldBeGreaterThan(0.7 * bestSingle, "fusion must not flatten the dark detail");
    }

    /// <summary>
    /// The point of blending in the Laplacian domain rather than per pixel: where the winning frame changes, the
    /// transition is spread over scales instead of showing up as a step.
    /// </summary>
    /// <remarks>
    /// Two frames: A is dark everywhere except a textured bright square, B is uniformly mid-bright. The score
    /// picks A inside the square (texture) and B outside it, so a per-pixel blend would jump by the difference
    /// between the two frames at the square's edge — about 0.4 here. A Laplacian blend carries that edge through
    /// the coarse levels and lands well below it.
    /// </remarks>
    [Fact]
    public void WinnerChangesWithoutAStep()
    {
        using var a = Image.Create(Width, Height, WorkingFormat.Rgba8);
        using var b = Image.Create(Width, Height, WorkingFormat.Rgba8);
        var sa = a.WritablePixels;
        var sb = b.WritablePixels;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var o = ((y * Width) + x) * 4;
                // A is dark everywhere, with fine stripes inside the square only. B is uniformly bright. Neither
                // frame has a hard edge of its own: the only thing that changes at the square's boundary is which
                // frame the score prefers.
                var inside = x >= 20 && x < 44 && y >= 12 && y < 36;
                var stripe = inside && ((x + y) % 4) < 2 ? 1.55f : 1f;
                sa[o] = (byte)Math.Clamp(0.20f * stripe * 255f, 0f, 255f);
                sa[o + 1] = sa[o];
                sa[o + 2] = sa[o];
                sa[o + 3] = 255;

                sb[o] = (byte)(0.80f * 255f);
                sb[o + 1] = sb[o];
                sb[o + 2] = sb[o];
                sb[o + 3] = 255;
            }
        }

        using var fused = Image.FuseExposures([a, b]);
        var span = fused.WritablePixels;

        // The largest step between horizontally adjacent pixels, taken across the square's left edge.
        var worst = 0;
        var worstX = 0;
        var worstY = 0;
        for (var y = 12; y < 36; y++)
        {
            for (var x = 1; x < Width; x++)
            {
                var left = span[((y * Width) + x - 1) * 4];
                var right = span[((y * Width) + x) * 4];
                var delta = Math.Abs(left - right);
                if (delta > worst)
                {
                    worst = delta;
                    worstX = x;
                    worstY = y;
                }
            }
        }

        // Measured 37 levels here. A per-pixel winner would step by the full difference between the frames at that
        // boundary — 0.6 in scene units, about 153 levels — so the pyramid is carrying most of it into the coarse
        // levels. The threshold is set with room for the taps, not at the measured value.
        worst.ShouldBeLessThan(60, $"largest adjacent step was {worst} levels at ({worstX},{worstY})");
    }

    /// <summary>Identical frames fuse back to themselves: the weights are uniform, so the blend is the input.</summary>
    [Fact]
    public void IdenticalFrames_FuseToTheInput()
    {
        using var a = Expose(1f);
        using var b = Expose(1f);

        using var fused = Fuse(a, b);

        var source = a.WritablePixels;
        var span = fused.WritablePixels;
        var worst = 0;
        for (var i = 0; i < span.Length; i++)
        {
            if (i % 4 == 3)
                continue;

            worst = Math.Max(worst, Math.Abs(span[i] - source[i]));
        }

        var worstIndex = 0;
        var worstDelta = 0;
        for (var i = 0; i < span.Length; i++)
        {
            if (i % 4 == 3)
                continue;

            var delta = Math.Abs(span[i] - source[i]);
            if (delta > worstDelta)
            {
                worstDelta = delta;
                worstIndex = i;
            }
        }

        worst.ShouldBeLessThanOrEqualTo(2,
            $"worst channel differs by {worst} at byte {worstIndex}: fused {span[worstIndex]} vs source {source[worstIndex]}");
    }

    /// <summary>Same input, same bytes.</summary>
    [Fact]
    public void IsDeterministic()
    {
        using var a = Expose(0.35f);
        using var b = Expose(2.6f);

        using var first = Fuse(a, b);
        using var second = Fuse(a, b);

        first.WritablePixels.ToArray().ShouldBe(second.WritablePixels.ToArray());
    }

    [Fact]
    public void ASingleFrame_IsRejected()
    {
        using var a = Expose(1f);
        Should.Throw<ArgumentException>(() => ExposureFusionOps.Fuse([a[0].Pixels.View]));
    }

    [Fact]
    public void MismatchedSizes_AreRejected()
    {
        using var a = Expose(1f);
        using var b = Image.Create(Width / 2, Height, WorkingFormat.Rgba8);
        Should.Throw<ArgumentException>(() => ExposureFusionOps.Fuse([a[0].Pixels.View, b[0].Pixels.View]));
    }

    /// <summary>Diagnostic: two copies of a flat frame must fuse back to that flat value.</summary>
    [Fact]
    public void FlatFrames_FuseToTheSameFlatValue()
    {
        var image = Image.Create(8, 8, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = 128;
            span[i + 1] = 128;
            span[i + 2] = 128;
            span[i + 3] = 255;
        }

        using (image)
        using (var fused = Image.FuseExposures([image, image]))
        {
            fused.WritablePixels[0].ShouldBe((byte)128);
        }
    }

    private static Image Fuse(Image a, Image b, Image? c = null) =>
        c is null ? Image.FuseExposures([a, b]) : Image.FuseExposures([a, b, c]);

    /// <summary>
    /// A scene with a dark textured half on the left and a bright half on the right, scaled by
    /// <paramref name="exposure"/> and clipped — which is what a camera would hand you.
    /// </summary>
    private static Image Expose(float exposure)
    {
        var image = Image.Create(Width, Height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var o = ((y * Width) + x) * 4;
                var baseValue = x < Width / 2
                    ? 0.04f + (0.08f * ((x + y) % 8) / 8f)      // dark, striped
                    : 0.88f + (0.08f * ((x * 3 + y) % 8) / 8f); // bright, striped
                var value = (byte)Math.Clamp(baseValue * exposure * 255f, 0f, 255f);
                span[o] = value;
                span[o + 1] = value;
                span[o + 2] = value;
                span[o + 3] = 255;
            }
        }

        return image;
    }

    /// <summary>Fraction of pixels with a channel at an extreme.</summary>
    private static double ClippedFraction(Image image)
    {
        var span = image.WritablePixels;
        var clipped = 0;
        for (var i = 0; i < span.Length; i += 4)
        {
            if (span[i] is 0 or 255 || span[i + 1] is 0 or 255 || span[i + 2] is 0 or 255)
                clipped++;
        }

        return clipped / (double)(span.Length / 4);
    }

    private static double Mean(ReadOnlySpan<byte> span, int x0, int x1)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                sum += span[((y * Width) + x) * 4];
                count++;
            }
        }

        return sum / count / 255.0;
    }

    /// <summary>Mean absolute difference between horizontally adjacent pixels, as a stand-in for texture.</summary>
    private static double LocalContrast(ReadOnlySpan<byte> span, int x0, int x1, int y0, int y1)
    {
        double sum = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1 - 1; x++)
            {
                var left = span[((y * Width) + x) * 4];
                var right = span[((y * Width) + x + 1) * 4];
                sum += Math.Abs(left - right);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }
}
