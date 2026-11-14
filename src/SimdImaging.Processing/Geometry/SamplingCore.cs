using System.Runtime.InteropServices;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// Format-native point sampling. This is the precision-critical path: it never routes a pixel through
/// <see cref="RgbaColor"/> (four bytes), so Rgba16 keeps all 16 bits, Rgba32F keeps its original floats,
/// and HDR / negative / out-of-range values survive a warp intact.
/// <para>The scalar arithmetic here is the reference every future vector tier must reproduce byte for byte.
/// Operation order is frozen: no reassociation and no FMA across the four taps.</para>
/// </summary>
internal static class SamplingCore
{
    /// <summary>Reads one pixel into four floats in the image's own sample space.</summary>
    public static void ReadPixel(PixelView source, int x, int y, Span<float> rgba)
    {
        var row = source.Row(y);
        switch (source.Format)
        {
            case WorkingFormat.Rgba8:
            {
                var o = x * 4;
                rgba[0] = row[o] * (1f / Pixels.MaxCode8);
                rgba[1] = row[o + 1] * (1f / Pixels.MaxCode8);
                rgba[2] = row[o + 2] * (1f / Pixels.MaxCode8);
                rgba[3] = row[o + 3] * (1f / Pixels.MaxCode8);
                break;
            }

            case WorkingFormat.Rgba16:
            {
                var wide = MemoryMarshal.Cast<byte, ushort>(row);
                var o = x * 4;
                rgba[0] = wide[o] * (1f / Pixels.MaxCode16);
                rgba[1] = wide[o + 1] * (1f / Pixels.MaxCode16);
                rgba[2] = wide[o + 2] * (1f / Pixels.MaxCode16);
                rgba[3] = wide[o + 3] * (1f / Pixels.MaxCode16);
                break;
            }

            default:
            {
                // Rgba32F: copy verbatim. No clamp to [0,1], no quantisation, no NaN rewrite.
                var floats = MemoryMarshal.Cast<byte, float>(row);
                var o = x * 4;
                rgba[0] = floats[o];
                rgba[1] = floats[o + 1];
                rgba[2] = floats[o + 2];
                rgba[3] = floats[o + 3];
                break;
            }
        }
    }

    /// <summary>Samples a continuous coordinate in the source's own precision.</summary>
    public static void Sample(PixelView source, float x, float y, Resampler resampler,
        SampleOptions options, Span<float> rgba)
    {
        if (options.Domain != SampleDomain.Encoded || options.Alpha != AlphaMode.Straight ||
            options.Edge != EdgeMode.Clamp || options.OutOfGamut != OutOfGamutPolicy.Preserve)
        {
            throw new NotSupportedException(
                "This sampler implements Encoded / Straight / Clamp / Preserve only; " +
                "other sampling domains are added when Warp and Resize share one core. " +
                "Silent fallback is not allowed.");
        }

        switch (resampler)
        {
            case Resampler.Nearest:
                Nearest(source, x, y, rgba);
                break;
            case Resampler.Bilinear:
                Bilinear(source, x, y, rgba);
                break;
            default:
                throw new NotSupportedException(
                    $"{resampler} is not available for point sampling; use Nearest or Bilinear.");
        }
    }

    private static void Nearest(PixelView source, float x, float y, Span<float> rgba)
    {
        var sx = Math.Clamp((int)MathF.Floor(x), 0, source.Width - 1);
        var sy = Math.Clamp((int)MathF.Floor(y), 0, source.Height - 1);
        ReadPixel(source, sx, sy, rgba);
    }

    private static void Bilinear(PixelView source, float x, float y, Span<float> rgba)
    {
        var fx = Math.Clamp(x - 0.5f, 0f, source.Width - 1f);
        var fy = Math.Clamp(y - 0.5f, 0f, source.Height - 1f);

        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var x1 = Math.Min(x0 + 1, source.Width - 1);
        var y1 = Math.Min(y0 + 1, source.Height - 1);
        var tx = fx - x0;
        var ty = fy - y0;

        Span<float> c00 = stackalloc float[4];
        Span<float> c10 = stackalloc float[4];
        Span<float> c01 = stackalloc float[4];
        Span<float> c11 = stackalloc float[4];
        ReadPixel(source, x0, y0, c00);
        ReadPixel(source, x1, y0, c10);
        ReadPixel(source, x0, y1, c01);
        ReadPixel(source, x1, y1, c11);

        for (var channel = 0; channel < 4; channel++)
        {
            var top = Lerp(c00[channel], c10[channel], tx);
            var bottom = Lerp(c01[channel], c11[channel], tx);
            rgba[channel] = Lerp(top, bottom, ty);
        }
    }

    /// <summary>
    /// Unified reconstruction for the explicit <see cref="SampleOptions"/> path: all five kernels,
    /// per-sample domain transform, alpha mode, edge policy and out-of-gamut handling. Tap weights
    /// come from <see cref="TapPlanBuilder"/>, the same builder Resize uses.
    /// <para>The accumulation order is frozen (y major, x ascending, channel ascending, one multiply
    /// per tap) so every SIMD tier reproduces the same bytes. The historical point-sampling entry
    /// (<see cref="Sample"/>) keeps its own <c>Lerp</c> order and is deliberately untouched.</para>
    /// </summary>
    public static void SampleWithOptions(PixelView source, float x, float y, Resampler resampler,
        SampleOptions options, ReadOnlySpan<float> background, Span<float> rgba)
    {
        var planX = TapPlanBuilder.ForPoint(x, source.Width, resampler);
        var planY = TapPlanBuilder.ForPoint(y, source.Height, resampler);
        SampleWithOptions(source, planX, planY, options, background, rgba);
    }

    /// <summary>Plan-injecting core of <see cref="SampleWithOptions(PixelView,float,float,Resampler,SampleOptions,ReadOnlySpan{float},Span{float})"/>.
    /// ResizeWithOptions builds scale-aware tap plans (the point-sampling overload forces scale == 1),
    /// so the semantic pipeline — per-sample domain transform, alpha, edge policy, frozen accumulation
    /// order, out-of-gamut — is shared through this overload. Pure code motion; no arithmetic changed.</summary>
    public static void SampleWithOptions(PixelView source, TapPlan planX, TapPlan planY,
        SampleOptions options, ReadOnlySpan<float> background, Span<float> rgba)
    {
        var linear = options.Domain == SampleDomain.LinearLight;
        var premultiplied = options.Alpha == AlphaMode.Premultiplied;

        Span<float> sum = stackalloc float[4];
        sum.Clear();
        Span<float> tap = stackalloc float[4];

        for (var ky = 0; ky < planY.Count; ky++)
        {
            var wy = planY.Weights[ky];
            if (wy == 0f) continue;
            var ty = planY.Start + ky;
            var outsideY = ty < 0 || ty >= source.Height;

            for (var kx = 0; kx < planX.Count; kx++)
            {
                var wx = planX.Weights[kx];
                if (wx == 0f) continue;
                var tx = planX.Start + kx;
                var outside = outsideY || tx < 0 || tx >= source.Width;
                var weight = wx * wy;

                if (outside && options.Edge == EdgeMode.Background)
                {
                    background.CopyTo(tap);
                }
                else
                {
                    ReadPixel(source, Math.Clamp(tx, 0, source.Width - 1),
                        Math.Clamp(ty, 0, source.Height - 1), tap);
                }

                if (linear)
                {
                    tap[0] = SampleTransfer.ToLinear(tap[0], options.Curve);
                    tap[1] = SampleTransfer.ToLinear(tap[1], options.Curve);
                    tap[2] = SampleTransfer.ToLinear(tap[2], options.Curve);
                }

                if (premultiplied)
                {
                    tap[0] *= tap[3];
                    tap[1] *= tap[3];
                    tap[2] *= tap[3];
                }

                for (var channel = 0; channel < 4; channel++)
                    sum[channel] += tap[channel] * weight;
            }
        }

        if (premultiplied)
        {
            var inverse = sum[3] > 0f ? 1f / sum[3] : 0f;
            sum[0] *= inverse;
            sum[1] *= inverse;
            sum[2] *= inverse;
        }

        if (linear)
        {
            sum[0] = SampleTransfer.FromLinear(sum[0], options.Curve);
            sum[1] = SampleTransfer.FromLinear(sum[1], options.Curve);
            sum[2] = SampleTransfer.FromLinear(sum[2], options.Curve);
        }

        Finish(sum, options, rgba);
    }

    /// <summary>Shared out-of-gamut checkpoint: Preserve keeps values verbatim, Clamp trims to the
    /// nominal range, Throw rejects NaN/Inf/out-of-range loudly. Pure code motion from the tap-plan
    /// core so the EWA path shares the exact same policy.</summary>
    private static void Finish(Span<float> sum, SampleOptions options, Span<float> rgba)
    {
        for (var channel = 0; channel < 4; channel++)
        {
            var value = sum[channel];
            switch (options.OutOfGamut)
            {
                case OutOfGamutPolicy.Clamp:
                    value = float.IsNaN(value) || value < 0f ? 0f : value > 1f ? 1f : value;
                    break;
                case OutOfGamutPolicy.Throw:
                    if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                        throw new InvalidDataException(
                            $"Sample {value} is outside the nominal [0,1] range for the working format.");
                    break;
            }

            rgba[channel] = value;
        }
    }

    /// <summary>
    /// EWA entry (M9): <see cref="SampleAaMode.Elliptical"/> shapes a truncated elliptical Gaussian
    /// footprint from the mapping's local Jacobian (see <see cref="EwaKernel"/>). When the Jacobian is
    /// degenerate the plan reports <see cref="EwaStatus.DegenerateIsotropic"/> and the reconstruction
    /// falls back to <see cref="TapPlanBuilder.ForPoint"/> — the status is observable via
    /// <paramref name="status"/>, never a silent swap. The elliptical window is clipped to the source
    /// box at build time, so every tap reads an in-bounds pixel and the edge mode is a no-op here.
    /// <para>The tap order is frozen (y-major, x-ascending, channel ascending, one multiply-add per
    /// channel per tap) and the accumulation stays scalar by design: no vector tier exists for EWA in
    /// M9, so no FMA-shape asymmetry between reference and accelerated paths can arise (see the M8
    /// sweep in known-gaps.md).</para>
    /// </summary>
    public static void SampleWithOptions(PixelView source, float x, float y, Resampler resampler,
        SampleOptions options, ReadOnlySpan<float> background, SourceJacobian jacobian, out EwaStatus status,
        Span<float> rgba)
    {
        if (options.AaMode != SampleAaMode.Elliptical)
            throw new NotSupportedException(
                "This overload implements SampleAaMode.Elliptical only; callers without a Jacobian " +
                "must use the overload without one.");

        if (resampler == Resampler.Nearest)
            throw new NotSupportedException(
                "Nearest sampling has no reconstruction kernel to adapt; Elliptical AA needs one.");

        var detJ = jacobian.DuDx * jacobian.DvDy - jacobian.DuDy * jacobian.DvDx;
        var maxTaps = AdaptiveAa.Evaluate(detJ);

        // maxTaps + 1: a half-integer phase puts an extra tap on each axis at the budget edge.
        var width = maxTaps + 1;
        var weights = DoubleScratch.Rent(width * width);
        try
        {
            var plan = EwaKernel.Build(jacobian, x, y, source.Width, source.Height,
                maxTaps, weights);
            status = plan.Status;

            if (plan.Status == EwaStatus.DegenerateIsotropic)
            {
                // Registered fallback: isotropic reconstruction with the selected kernel.
                var planX = TapPlanBuilder.ForPoint(x, source.Width, resampler);
                var planY = TapPlanBuilder.ForPoint(y, source.Height, resampler);
                SampleWithOptions(source, planX, planY, options, background, rgba);
                return;
            }

            AccumulateEwa(source, plan, options, rgba);
        }
        finally
        {
            DoubleScratch.Return(weights);
        }
    }

    /// <summary>The EWA accumulation loop. Same channel/tap discipline as the plan-injecting core,
    /// but the weight grid is 2-D (an elliptical Gaussian is not separable).</summary>
    private static void AccumulateEwa(PixelView source, in EwaPlan plan, SampleOptions options, Span<float> rgba)
    {
        var linear = options.Domain == SampleDomain.LinearLight;
        var premultiplied = options.Alpha == AlphaMode.Premultiplied;

        Span<float> sum = stackalloc float[4];
        sum.Clear();
        Span<float> tap = stackalloc float[4];

        for (var ky = 0; ky < plan.CountY; ky++)
        {
            var ty = plan.StartY + ky;
            var rowBase = ky * plan.CountX;
            for (var kx = 0; kx < plan.CountX; kx++)
            {
                // One truncation, here: the plan stores double-normalised weights.
                var weight = (float)plan.Weights[rowBase + kx];
                if (weight == 0f) continue;
                ReadPixel(source, plan.StartX + kx, ty, tap);

                if (linear)
                {
                    tap[0] = SampleTransfer.ToLinear(tap[0], options.Curve);
                    tap[1] = SampleTransfer.ToLinear(tap[1], options.Curve);
                    tap[2] = SampleTransfer.ToLinear(tap[2], options.Curve);
                }

                if (premultiplied)
                {
                    tap[0] *= tap[3];
                    tap[1] *= tap[3];
                    tap[2] *= tap[3];
                }

                for (var channel = 0; channel < 4; channel++)
                    sum[channel] += tap[channel] * weight;
            }
        }

        if (premultiplied)
        {
            var inverse = sum[3] > 0f ? 1f / sum[3] : 0f;
            sum[0] *= inverse;
            sum[1] *= inverse;
            sum[2] *= inverse;
        }

        if (linear)
        {
            sum[0] = SampleTransfer.FromLinear(sum[0], options.Curve);
            sum[1] = SampleTransfer.FromLinear(sum[1], options.Curve);
            sum[2] = SampleTransfer.FromLinear(sum[2], options.Curve);
        }

        Finish(sum, options, rgba);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
