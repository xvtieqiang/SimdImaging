using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Exposure fusion (Mertens, Kautz and Van Reeth, 2007): several differently exposed frames of one scene are
/// blended by how good each pixel looks in each frame, instead of being combined into an HDR radiance map and
/// tone-mapped back down.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no radiance map.</b> Building HDR means estimating the camera response, which needs the exposure times
/// and is the step that goes wrong when they are missing. Fusion skips it: the frames are already displayable and
/// the only question is which one to believe at each pixel. The result is not physically linear — nobody can
/// re-expose it — which is the trade the method deliberately makes.
/// </para>
/// <para>
/// <b>Blending in the Laplacian domain.</b> The per-pixel score decides which frame wins where, but the winner
/// changes abruptly and a straight weighted average shows that as a seam. Each frame is therefore decomposed into
/// a Laplacian pyramid, the weights into a Gaussian pyramid, and the blend happens level by level: fine levels
/// carry the winner's texture, coarse levels carry a smooth transition between winners. Collapse is
/// <c>out_l = blend_l + U(out_{l+1})</c>.
/// </para>
/// <para>
/// <b>Channels are processed one at a time.</b> The pyramid helper works on single-channel planes, so the frame is
/// de-interleaved, each channel run through the pyramid, and the result re-interleaved. Doing all four at once
/// would hold four times as many planes for the same answer; sequentially the peak is one channel's pyramids plus
/// the weights.
/// </para>
/// <para>
/// <b>Determinism.</b> Fixed iteration order over frames, channels, levels and pixels; float arithmetic in one
/// order; no random source; no order-dependent tie-break. The taps are the same Burt–Adcock filters
/// <c>LocalLaplacianOps</c> uses, so the two operators cannot disagree about what a level is.
/// </para>
/// </remarks>
public static class ExposureFusionOps
{
    /// <summary>Fuses two or more exposures of the same scene.</summary>
    /// <param name="sources">The frames; all must share a size. Two is the minimum.</param>
    /// <param name="levels">Pyramid levels; 0 picks one from the image size.</param>
    /// <param name="contrast">Exponent on local contrast in the per-pixel score.</param>
    /// <param name="saturation">Exponent on colour saturation.</param>
    /// <param name="exposure">Exponent on well-exposedness.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentException">Fewer than two frames, or sizes disagree.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of range.</exception>
    public static WorkingBuffer Fuse(IReadOnlyList<PixelView> sources, int levels = 0,
        float contrast = 1f, float saturation = 1f, float exposure = 1f, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count < 2)
            throw new ArgumentException("Fusion needs at least two frames.", nameof(sources));
        if (!(contrast >= 0f) || !(saturation >= 0f) || !(exposure >= 0f))
            throw new ArgumentOutOfRangeException(nameof(contrast), "Weights must not be negative.");
        ArgumentOutOfRangeException.ThrowIfNegative(levels);

        var width = sources[0].Width;
        var height = sources[0].Height;
        var format = sources[0].Format;
        for (var i = 1; i < sources.Count; i++)
        {
            if (sources[i].Width != width || sources[i].Height != height)
                throw new ArgumentException("Every frame must have the same size.", nameof(sources));
        }

        if (width == 0 || height == 0)
            return WorkingBuffer.Rent(width, height, format, pool);

        var count = sources.Count;
        var pixels = width * height;
        var levelCount = levels > 1 ? levels : DefaultLevels(width, height);
        var sizes = LevelSizes(width, height, levelCount);

        var scores = new float[count][];
        var weightPyramids = new float[count][][];
        var destination = WorkingBuffer.Rent(width, height, format, pool);
        try
        {
            for (var i = 0; i < count; i++)
                scores[i] = Score(sources[i], contrast, saturation, exposure);

            Normalise(scores, count, pixels);
            for (var i = 0; i < count; i++)
                weightPyramids[i] = GaussianPyramid(scores[i], sizes);

            var result = new float[pixels * 4];
            for (var channel = 0; channel < 4; channel++)
            {
                var laplacians = new float[count][][];
                for (var i = 0; i < count; i++)
                {
                    var plane = Extract(sources[i], channel);
                    try
                    {
                        laplacians[i] = LaplacianPyramid(plane, sizes);
                    }
                    finally
                    {
                        FloatScratch.Return(plane);
                    }
                }

                float[] fused;
                try
                {
                    fused = Blend(laplacians, weightPyramids, sizes);
                }
                finally
                {
                    foreach (var pyramid in laplacians)
                    {
                        foreach (var plane in pyramid)
                            FloatScratch.Return(plane);
                    }
                }

                try
                {
                    for (var p = 0; p < pixels; p++)
                        result[(p * 4) + channel] = fused[p];
                }
                finally
                {
                    FloatScratch.Return(fused);
                }
            }

            var output = new float[width * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var o = x * 4;
                    var plane = ((y * width) + x) * 4;
                    output[o] = result[plane];
                    output[o + 1] = result[plane + 1];
                    output[o + 2] = result[plane + 2];
                    output[o + 3] = result[plane + 3];
                }

                Pixels.WriteRowRaw(output, width, format, destination.View.Row(y));
            }

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (weightPyramids[i] is { } pyramid)
                {
                    foreach (var plane in pyramid)
                        FloatScratch.Return(plane);
                }

                if (scores[i] is { } score)
                    FloatScratch.Return(score);
            }
        }
    }

    /// <summary>
    /// Scales each pixel's scores so they sum to one across frames, with equal shares where no frame claims the
    /// pixel (a flat grey patch has no contrast and no saturation, so all three terms can vanish).
    /// </summary>
    private static void Normalise(float[][] scores, int count, int pixels)
    {
        for (var p = 0; p < pixels; p++)
        {
            var total = 0f;
            for (var i = 0; i < count; i++)
                total += scores[i][p];

            if (total > 1e-12f)
            {
                var share = 1f / total;
                for (var i = 0; i < count; i++)
                    scores[i][p] *= share;
            }
            else
            {
                var share = 1f / count;
                for (var i = 0; i < count; i++)
                    scores[i][p] = share;
            }
        }
    }

    /// <summary>Levels that keep the coarsest plane at least eight pixels on its short side.</summary>
    private static int DefaultLevels(int width, int height)
    {
        var levels = 1;
        var shortest = Math.Min(width, height);
        while (shortest > 16 && levels < 8)
        {
            shortest = Pyramid.DownsampledLength(shortest);
            levels++;
        }

        return levels;
    }

    private static (int Width, int Height)[] LevelSizes(int width, int height, int levels)
    {
        var sizes = new (int Width, int Height)[levels];
        var w = width;
        var h = height;
        for (var i = 0; i < levels; i++)
        {
            sizes[i] = (w, h);
            w = Pyramid.DownsampledLength(w);
            h = Pyramid.DownsampledLength(h);
        }

        return sizes;
    }

    /// <summary>One channel of one frame, as a plane of <c>width × height</c> floats.</summary>
    private static float[] Extract(PixelView source, int channel)
    {
        var width = source.Width;
        var height = source.Height;
        var plane = FloatScratch.Rent(width * height);
        var row = new float[width * 4];
        for (var y = 0; y < height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            for (var x = 0; x < width; x++)
                plane[(y * width) + x] = row[(x * 4) + channel];
        }

        return plane;
    }

    /// <summary><c>L_l = G_l − U(G_{l+1})</c>, with the coarsest level kept as-is. Every returned plane is owned
    /// by the caller; the input plane is not freed here.</summary>
    private static float[][] LaplacianPyramid(float[] plane, (int Width, int Height)[] sizes)
    {
        var levels = new float[sizes.Length][];
        var current = plane;
        var ownsCurrent = false;
        for (var level = 0; level < sizes.Length; level++)
        {
            var (w, h) = sizes[level];
            if (level == sizes.Length - 1)
            {
                var last = FloatScratch.Rent(w * h);
                Array.Copy(current, last, w * h);
                levels[level] = last;

                if (ownsCurrent)
                    FloatScratch.Return(current);

                break;
            }

            var (dw, dh) = sizes[level + 1];
            var down = FloatScratch.Rent(dw * dh);
            var downScratch = FloatScratch.Rent(Math.Max(dw * h, w * dh));
            Pyramid.Downsample(current, w, h, down, dw, dh, downScratch);
            FloatScratch.Return(downScratch);

            var up = FloatScratch.Rent(w * h);
            var upScratch = FloatScratch.Rent(h * w);
            Pyramid.Upsample(down, dw, dh, up, w, h, upScratch);
            FloatScratch.Return(upScratch);

            var laplacian = FloatScratch.Rent(w * h);
            for (var p = 0; p < w * h; p++)
                laplacian[p] = current[p] - up[p];

            levels[level] = laplacian;
            FloatScratch.Return(up);

            if (ownsCurrent)
                FloatScratch.Return(current);

            current = down;
            ownsCurrent = true;
        }

        return levels;
    }

    /// <summary>Gaussian pyramid of a normalised weight plane; every level is a copy the caller owns.</summary>
    private static float[][] GaussianPyramid(float[] plane, (int Width, int Height)[] sizes)
    {
        var levels = new float[sizes.Length][];
        var current = plane;
        var ownsCurrent = false;
        for (var level = 0; level < sizes.Length; level++)
        {
            var (w, h) = sizes[level];
            var copy = FloatScratch.Rent(w * h);
            Array.Copy(current, copy, w * h);
            levels[level] = copy;

            if (level == sizes.Length - 1)
                break;

            var (dw, dh) = sizes[level + 1];
            var down = FloatScratch.Rent(dw * dh);
            var scratch = FloatScratch.Rent(Math.Max(dw * h, w * dh));
            Pyramid.Downsample(current, w, h, down, dw, dh, scratch);
            FloatScratch.Return(scratch);

            if (ownsCurrent)
                FloatScratch.Return(current);

            current = down;
            ownsCurrent = true;
        }

        return levels;
    }

    /// <summary>Weighted sum of the frames' Laplacian levels, then collapse back to the image.</summary>
    private static float[] Blend(float[][][] laplacians, float[][][] weights, (int Width, int Height)[] sizes)
    {
        var blended = new float[sizes.Length][];
        for (var level = 0; level < sizes.Length; level++)
        {
            var (w, h) = sizes[level];
            var plane = FloatScratch.Rent(w * h);

            // FloatScratch hands back the same array it was given last time, contents and all: the accumulator has
            // to be cleared or the first frame is summed onto whatever the previous call left behind. This is the
            // second time this exact mistake produced non-deterministic bytes in this file.
            Array.Clear(plane, 0, w * h);

            for (var i = 0; i < laplacians.Length; i++)
            {
                var source = laplacians[i][level];
                var weight = weights[i][level];
                for (var p = 0; p < w * h; p++)
                    plane[p] += source[p] * weight[p];
            }

            blended[level] = plane;
        }

        var current = blended[sizes.Length - 1];
        var ownsCurrent = true;
        for (var level = sizes.Length - 2; level >= 0; level--)
        {
            var (w, h) = sizes[level];
            var (dw, dh) = sizes[level + 1];
            var up = FloatScratch.Rent(w * h);
            var upScratch = FloatScratch.Rent(h * w);
            Pyramid.Upsample(current, dw, dh, up, w, h, upScratch);
            FloatScratch.Return(upScratch);

            var next = FloatScratch.Rent(w * h);
            var laplacian = blended[level];
            for (var p = 0; p < w * h; p++)
                next[p] = laplacian[p] + up[p];

            FloatScratch.Return(up);
            FloatScratch.Return(laplacian);

            if (ownsCurrent)
                FloatScratch.Return(current);

            current = next;
            ownsCurrent = true;
        }

        return current;
    }

    /// <summary><c>value^weight</c>, without the transcendental call for the two weights that mean "leave it".</summary>
    private static float Weighted(float value, float weight) =>
        weight == 0f ? 1f : weight == 1f ? value : MathF.Pow(value, weight);

    /// <summary>
    /// Per-pixel score: local contrast (Laplacian magnitude of luma), saturation (channel spread) and
    /// well-exposedness (a Gaussian around mid-grey), combined with the caller's exponents.
    /// </summary>
    private static float[] Score(PixelView source, float contrastWeight, float saturationWeight, float exposureWeight)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = width * height;

        var luma = FloatScratch.Rent(pixels);
        var channels = FloatScratch.Rent(pixels * 3);
        var scores = FloatScratch.Rent(pixels);
        var row = new float[width * 4];
        try
        {
            for (var y = 0; y < height; y++)
            {
                Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
                for (var x = 0; x < width; x++)
                {
                    var o = x * 4;
                    var p = (y * width) + x;
                    var r = row[o];
                    var g = row[o + 1];
                    var b = row[o + 2];
                    channels[p * 3] = r;
                    channels[(p * 3) + 1] = g;
                    channels[(p * 3) + 2] = b;
                    luma[p] = (0.299f * r) + (0.587f * g) + (0.114f * b);
                }
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var p = (y * width) + x;

                    var centre = luma[p];
                    var left = luma[(y * width) + Math.Max(0, x - 1)];
                    var right = luma[(y * width) + Math.Min(width - 1, x + 1)];
                    var up = luma[(Math.Max(0, y - 1) * width) + x];
                    var down = luma[(Math.Min(height - 1, y + 1) * width) + x];
                    var localContrast = MathF.Abs((4f * centre) - left - right - up - down);

                    var r = channels[p * 3];
                    var g = channels[(p * 3) + 1];
                    var b = channels[(p * 3) + 2];
                    var mean = (r + g + b) / 3f;
                    var saturation = MathF.Sqrt(((r - mean) * (r - mean)) + ((g - mean) * (g - mean))
                        + ((b - mean) * (b - mean))) / 3f;

                    var deviation = centre - 0.5f;
                    var wellExposed = MathF.Exp(-(deviation * deviation) / (2f * 0.2f * 0.2f));

                    scores[p] = Weighted(MathF.Max(localContrast, 1e-6f), contrastWeight)
                        * Weighted(MathF.Max(saturation, 1e-6f), saturationWeight)
                        * Weighted(MathF.Max(wellExposed, 1e-6f), exposureWeight);
                }
            }

            return scores;
        }
        finally
        {
            FloatScratch.Return(channels);
            FloatScratch.Return(luma);
        }
    }
}
