using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// TapPlan-driven sampler implementing the Mapping chain contract: premultiplied reads, tap-level
/// clamp replication, Σw·c accumulation in the frozen y-major / x-ascending order, and the single
/// weight generation of <see cref="TapPlanBuilder"/> (the same generator Resize already consumes).
/// <para>Built for the M8 equivalence sweep against the frozen legacy <c>Mapping.SampleBilinear</c>
/// and consumed by the <c>…WithOptions</c> entry points. The legacy inline sampler stays verbatim
/// until the sweep decides whether the legacy paths can switch to this core (branch A) or must stay
/// frozen (branch B) — see <c>MappingTapPlanEquivalenceTests</c>.</para>
/// </summary>
internal static class MappingTapSampler
{
    /// <summary>Samples in pixel-centre coordinates (pixel <c>i</c> is centred at <c>i</c>), the same
    /// convention as the legacy <c>Mapping.SampleBilinear</c> input; returns premultiplied colour and
    /// replicates edges.</summary>
    public static void Sample(PixelView source, float x, float y, Resampler resampler,
        out float r, out float g, out float b, out float a)
    {
        // Areal centre: pixel i owns the unit interval [i, i+1) with its centre at i + 0.5, the same
        // phase convention TapPlanBuilder.FillAt (and Resize) is built on.
        var planX = TapPlanBuilder.ForPoint(x + 0.5, source.Width, resampler);
        var planY = TapPlanBuilder.ForPoint(y + 0.5, source.Height, resampler);
        var wx = planX.Weights;
        var wy = planY.Weights;
        r = 0f;
        g = 0f;
        b = 0f;
        a = 0f;
        var seeded = false;

        // The accumulation order is frozen: y-major bands, x-ascending inside each band, channels in
        // ascending order with one multiply-accumulate per channel per tap — the same order the legacy
        // r00*w00 + r10*w10 + r01*w01 + r11*w11 expression evaluates in. The per-tap weight is the
        // float product wx[kx]*wy[ky] formed once, matching the legacy (1-tx)*(1-ty) shape.
        //
        // The first tap seeds the accumulator directly instead of adding onto +0: the legacy sum is a
        // left-to-right expression whose first term keeps its sign, and Rgba32F signed-zero source
        // colours must survive byte-exactly ((-0)·1 is -0, while (+0) + (-0) is +0).
        for (var ky = 0; ky < wy.Length; ky++)
        {
            var sy = Math.Clamp(planY.Start + ky, 0, source.Height - 1);
            var wyv = wy[ky];
            for (var kx = 0; kx < wx.Length; kx++)
            {
                var sx = Math.Clamp(planX.Start + kx, 0, source.Width - 1);
                ReadPremultiplied(source, sx, sy, out var tr, out var tg, out var tb, out var ta);
                var w = wx[kx] * wyv;
                if (!seeded)
                {
                    r = tr * w;
                    g = tg * w;
                    b = tb * w;
                    a = ta * w;
                    seeded = true;
                }
                else
                {
                    r += tr * w;
                    g += tg * w;
                    b += tb * w;
                    a += ta * w;
                }
            }
        }
    }

    // Verbatim copy of Mapping.ReadPremultiplied (private there): the equivalence sweep must not
    // touch the legacy file, so the Mapping-chain premultiplied reader lives here too. If branch A
    // merges, the legacy private becomes internal and this copy is deleted; if branch B freezes the
    // legacy file, this copy stays as the Mapping-chain reader.
    private static void ReadPremultiplied(PixelView source, int x, int y,
        out float r, out float g, out float b, out float a)
    {
        var bytesPerPixel = source.Format.BytesPerPixel();
        var span = source.Row(y).Slice(x * bytesPerPixel, bytesPerPixel);

        switch (source.Format)
        {
            case WorkingFormat.Rgba8:
                a = span[3] * (1f / 255f);
                r = span[0] * (1f / 255f) * a;
                g = span[1] * (1f / 255f) * a;
                b = span[2] * (1f / 255f) * a;
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(span);
                a = samples[3] * (1f / 65535f);
                r = samples[0] * (1f / 65535f) * a;
                g = samples[1] * (1f / 65535f) * a;
                b = samples[2] * (1f / 65535f) * a;
                break;
            }

            default:
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span);
                a = samples[3];
                r = samples[0] * a;
                g = samples[1] * a;
                b = samples[2] * a;
                break;
            }
        }
    }
}
