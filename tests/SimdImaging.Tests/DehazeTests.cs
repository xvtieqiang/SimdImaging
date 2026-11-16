using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Dehazing has no oracle to compare against, so it is checked against haze it was given on purpose: take a
/// scene, put a known transmission in front of it, and see whether the operator gets the scene back.
/// </summary>
public class DehazeTests
{
    private const int Width = 64;
    private const int Height = 48;

    /// <summary>A constant veil: <c>I = J·t + A·(1 − t)</c>. Recovering <c>J</c> is the whole job.</summary>
    [Theory]
    [InlineData(0.6f)]
    [InlineData(0.4f)]
    [InlineData(0.8f)]
    public void ConstantVeil_IsRemoved(float transmission)
    {
        using var scene = Scene();
        using var hazy = Veil(scene, transmission, 0.85f);

        using var recovered = hazy.Dehaze(patchRadius: 3, omega: 1f, tMin: 0.1f, refineRadius: 0);

        // The recovered contrast is amplified by 1/t, so the prior's own bias is amplified with it: a thicker
        // veil is allowed a proportionally larger error rather than being held to the thin case.
        var error = MeanAbsoluteError(recovered, scene);
        error.ShouldBeLessThan(0.05 / transmission, $"mean absolute error was {error:F4} at t={transmission}");
    }

    /// <summary>No haze means nothing to do: a clear image must come back essentially untouched.</summary>
    [Fact]
    public void ClearImage_IsLeftAlone()
    {
        using var scene = Scene();
        using var recovered = scene.Dehaze(patchRadius: 3, omega: 0.95f, tMin: 0.1f, refineRadius: 0);

        var error = MeanAbsoluteError(recovered, scene);
        error.ShouldBeLessThan(0.03, $"mean absolute error was {error:F4}");
    }

    /// <summary>The refinement smooths the map; it must not undo the recovery.</summary>
    [Fact]
    public void RefinedTransmission_StillRecovers()
    {
        using var scene = Scene();
        using var hazy = Veil(scene, 0.5f, 0.85f);

        using var recovered = hazy.Dehaze(patchRadius: 3, omega: 1f, tMin: 0.1f, refineRadius: 6);

        var error = MeanAbsoluteError(recovered, scene);
        error.ShouldBeLessThan(0.05, $"mean absolute error was {error:F4}");
    }

    /// <summary>Same input, same bytes — no ordering, no randomness.</summary>
    [Fact]
    public void IsDeterministic()
    {
        using var hazy = Veil(Scene(), 0.5f, 0.85f);

        using var first = hazy.Dehaze();
        using var second = hazy.Dehaze();

        first.WritablePixels.ToArray().ShouldBe(second.WritablePixels.ToArray());
    }

    /// <summary>Alpha is not haze; it passes through untouched.</summary>
    [Fact]
    public void AlphaPassesThrough()
    {
        using var scene = Scene();
        var span = scene.WritablePixels;
        for (var i = 3; i < span.Length; i += 4)
            span[i] = (byte)(i % 251);

        using var recovered = scene.Dehaze();

        var before = scene.WritablePixels.ToArray();
        var after = recovered.WritablePixels.ToArray();
        for (var i = 3; i < before.Length; i += 4)
            after[i].ShouldBe(before[i]);
    }

    /// <summary>The pipeline path must agree with the direct call; the whole-image step is the same code.</summary>
    [Fact]
    public void PipelinePath_MatchesDirectCall()
    {
        using var hazy = Veil(Scene(), 0.5f, 0.85f);

        using var piped = hazy.Pipe().Dehaze(3, 0.95f, 0.1f, 4).ToImage();
        using var direct = hazy.Dehaze(3, 0.95f, 0.1f, 4);

        piped.WritablePixels.ToArray().ShouldBe(direct.WritablePixels.ToArray());
    }

    /// <summary>
    /// A bright, flat sky must survive: the dark-channel prior reads it as "almost no transmission" and divides by
    /// the floor, which turned an overcast sky into speckle on the first real photograph this ran on.
    /// </summary>
    [Fact]
    public void BrightSky_IsNotAmplified()
    {
        using var scene = SkyScene();
        var before = MeanLuma(scene);

        using var recovered = scene.Dehaze(patchRadius: 7, omega: 0.95f, tMin: 0.1f, refineRadius: 20);

        // The sky occupies the top half. Its mean must stay within a few levels of the input — with the sky guard
        // removed this rises by roughly a third, and the gradient in it is multiplied by ten.
        var skyBefore = MeanLuma(scene, 0, 48, 0, 20);
        var skyAfter = MeanLuma(recovered, 0, 48, 0, 20);
        Math.Abs(skyAfter - skyBefore).ShouldBeLessThan(8, $"sky moved from {skyBefore:F0} to {skyAfter:F0}");

        // The structure under the sky is not hazy, and dehazing a non-hazy subject means expanding contrast around
        // the atmospheric light — which pushes a dark object darker. That is the formula, not a defect: J = (I−A)/t
        // + A moves values away from A by 1/t. What is asserted here is only that the guard did not overshoot into
        // the subject: it must not be crushed to black or blown to white.
        var structureAfter = MeanLuma(recovered, 0, 48, 28, 48);
        structureAfter.ShouldBeGreaterThan(12, "the guard must not crush the subject to black");

        Math.Abs(MeanLuma(recovered) - before).ShouldBeLessThan(40);
    }

    /// <summary>A bright flat sky over a darker structured band.</summary>
    private static Image SkyScene()
    {
        var image = Image.Create(48, 48, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 48; x++)
            {
                var o = ((y * 48) + x) * 4;
                byte value;
                if (y < 20)
                {
                    value = (byte)(226 + ((x + y) % 4));            // near-white overcast sky, almost no texture
                }
                else
                {
                    value = (byte)((y % 8) < 4 ? 40 : 90);          // dark structure with some contrast
                }

                span[o] = value;
                span[o + 1] = value;
                span[o + 2] = value;
                span[o + 3] = 255;
            }
        }

        return image;
    }

    private static double MeanLuma(Image image) => MeanLuma(image, 0, image.Width, 0, image.Height);

    private static double MeanLuma(Image image, int x0, int x1, int y0, int y1)
    {
        var span = image.WritablePixels;
        double sum = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var o = ((y * image.Width) + x) * 4;
                sum += (0.299 * span[o]) + (0.587 * span[o + 1]) + (0.114 * span[o + 2]);
                count++;
            }
        }

        return sum / count;
    }

    /// <summary>A scene with structure in every channel, so the dark channel is not degenerate.</summary>
    private static Image Scene()
    {
        var image = Image.Create(Width, Height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var o = ((y * Width) + x) * 4;
                var block = ((x / 8) + (y / 8)) % 2 == 0;
                // One block has a near-zero channel, so the scene's own dark channel is ~0 — which is what the
                // prior assumes. A scene whose darkest channel is 30/255 tells the operator there is haze in it.
                span[o] = (byte)(block ? 2 : 210);
                span[o + 1] = (byte)(block ? 60 : 190);
                span[o + 2] = (byte)(block ? 40 : 200);
                span[o + 3] = 255;
            }
        }

        return image;
    }

    /// <summary>Puts a constant veil of <paramref name="transmission"/> in front of the scene.</summary>
    private static Image Veil(Image scene, float transmission, float atmosphere)
    {
        var image = Image.Create(scene.Width, scene.Height, scene.Format);
        var source = scene.WritablePixels;
        var span = image.WritablePixels;
        for (var i = 0; i < source.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var hazy = ((source[i + c] / 255f) * transmission) + (atmosphere * (1f - transmission));
                span[i + c] = (byte)Math.Clamp(hazy * 255f, 0f, 255f);
            }

            span[i + 3] = source[i + 3];
        }

        return image;
    }

    private static double MeanAbsoluteError(Image actual, Image expected)
    {
        var a = actual.WritablePixels;
        var e = expected.WritablePixels;
        double sum = 0;
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                sum += Math.Abs(a[i + c] - e[i + c]);
                count++;
            }
        }

        return sum / count / 255.0;
    }
}
