namespace SimdImaging.Processing.Internal;

/// <summary>
/// Burt–Adcock Gaussian/Laplacian pyramid over one float plane. The analysis and synthesis stages share
/// this file so a collapse cannot accidentally use a different kernel from the one that split the plane —
/// that mismatch is the classic identity-test failure in a Laplacian pyramid.
/// <para>
/// <b>Frozen arithmetic.</b> The 5-tap kernel is <c>[1,4,6,4,1]/16</c> (each weight an exact binary
/// fraction), taps run left to right and top to bottom, and every output starts from a zero accumulator.
/// Downsampling keeps the even positions of the full convolution (<c>out[j] = Σ h[k]·in[2j+k]</c>);
/// upsampling is the same kernel doubled, which leaves a 3-tap/2-tap interpolation by output parity.
/// There is no SIMD twin because there is no vector kernel yet: the first vector body added here must
/// keep the same tap order and be compared byte for byte, like every other kernel in the repository.
/// </para>
/// </summary>
internal static class Pyramid
{
    /// <summary>Padded source size for a downsampled length; odd sizes round up.</summary>
    public static int DownsampledLength(int length) => (length + 1) / 2;

    /// <summary>Downsamples <paramref name="source"/> (sw×sh) into <paramref name="destination"/>
    /// (dw×dh). <paramref name="scratch"/> must hold <c>dw × sh</c> floats.</summary>
    public static void Downsample(ReadOnlySpan<float> source, int sw, int sh,
        Span<float> destination, int dw, int dh, Span<float> scratch)
    {
        const float h0 = 1f / 16f, h1 = 4f / 16f, h2 = 6f / 16f, h3 = 4f / 16f, h4 = 1f / 16f;

        // Horizontal: keep the even samples of the full 5-tap convolution.
        for (var y = 0; y < sh; y++)
        {
            var row = source.Slice(y * sw, sw);
            var line = scratch.Slice(y * dw, dw);
            for (var x = 0; x < dw; x++)
            {
                var c = 2 * x;
                var m2 = c - 2 < 0 ? 0 : c - 2;
                var m1 = c - 1 < 0 ? 0 : c - 1;
                var p1 = c + 1 >= sw ? sw - 1 : c + 1;
                var p2 = c + 2 >= sw ? sw - 1 : c + 2;
                line[x] = h0 * row[m2] + h1 * row[m1] + h2 * row[c] + h3 * row[p1] + h4 * row[p2];
            }
        }

        // Vertical: the same kernel down the columns of the horizontally filtered plane.
        for (var y = 0; y < dh; y++)
        {
            var c = 2 * y;
            var m2 = c - 2 < 0 ? 0 : c - 2;
            var m1 = c - 1 < 0 ? 0 : c - 1;
            var p1 = c + 1 >= sh ? sh - 1 : c + 1;
            var p2 = c + 2 >= sh ? sh - 1 : c + 2;
            var r2 = scratch.Slice(m2 * dw, dw);
            var r1 = scratch.Slice(m1 * dw, dw);
            var r0 = scratch.Slice(c * dw, dw);
            var rp1 = scratch.Slice(p1 * dw, dw);
            var rp2 = scratch.Slice(p2 * dw, dw);
            var target = destination.Slice(y * dw, dw);
            for (var x = 0; x < dw; x++)
                target[x] = h0 * r2[x] + h1 * r1[x] + h2 * r0[x] + h3 * rp1[x] + h4 * rp2[x];
        }
    }

    /// <summary>Upsamples <paramref name="source"/> (sw×sh) to an <b>explicit</b> target size
    /// (tw×th). The target is a parameter because <c>2·sh</c> is one row too many when the level below
    /// had an odd height. <paramref name="scratch"/> must hold <c>tw × sh</c> floats.</summary>
    public static void Upsample(ReadOnlySpan<float> source, int sw, int sh,
        Span<float> destination, int tw, int th, Span<float> scratch)
    {
        // Horizontal expand: 2·h gives 0.125/0.75/0.125 on even targets and 0.5/0.5 on odd ones.
        for (var y = 0; y < sh; y++)
        {
            var row = source.Slice(y * sw, sw);
            var line = scratch.Slice(y * tw, tw);
            for (var x = 0; x < tw; x++)
            {
                if ((x & 1) == 0)
                {
                    var j = x >> 1;
                    var jm = j - 1 < 0 ? 0 : j - 1;
                    var jc = j >= sw ? sw - 1 : j;
                    var jp = j + 1 >= sw ? sw - 1 : j + 1;
                    line[x] = 0.125f * row[jm] + 0.75f * row[jc] + 0.125f * row[jp];
                }
                else
                {
                    var j = x >> 1;
                    var jc = j >= sw ? sw - 1 : j;
                    var jn = j + 1 >= sw ? sw - 1 : j + 1;
                    line[x] = 0.5f * row[jc] + 0.5f * row[jn];
                }
            }
        }

        for (var y = 0; y < th; y++)
        {
            var target = destination.Slice(y * tw, tw);
            if ((y & 1) == 0)
            {
                var j = y >> 1;
                var jm = j - 1 < 0 ? 0 : j - 1;
                var jc = j >= sh ? sh - 1 : j;
                var jp = j + 1 >= sh ? sh - 1 : j + 1;
                var above = scratch.Slice(jm * tw, tw);
                var centre = scratch.Slice(jc * tw, tw);
                var below = scratch.Slice(jp * tw, tw);
                for (var x = 0; x < tw; x++)
                    target[x] = 0.125f * above[x] + 0.75f * centre[x] + 0.125f * below[x];
            }
            else
            {
                var j = y >> 1;
                var jc = j >= sh ? sh - 1 : j;
                var jn = j + 1 >= sh ? sh - 1 : j + 1;
                var centre = scratch.Slice(jc * tw, tw);
                var below = scratch.Slice(jn * tw, tw);
                for (var x = 0; x < tw; x++)
                    target[x] = 0.5f * centre[x] + 0.5f * below[x];
            }
        }
    }
}
