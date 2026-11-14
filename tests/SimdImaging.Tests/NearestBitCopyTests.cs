using System.Buffers;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// Byte-exactness of the nearest Rgba8 fast path's single 32-bit copy (the 2026-11-15 optimisation in
/// Resize.cs). The existing NearestResizeTests pin a tightly-packed source; what they do NOT cover is the
/// shape a uint copy could get wrong: a NON-tight source whose rows have padding (stride &gt; row bytes).
/// <para>A 4-byte unaligned read at the last pixel of a row must stay inside the row's pixels and never
/// bleed into the padding, and every channel must land in its own byte. Two independent checks:</para>
/// <list type="bullet">
/// <item>Per-pixel reference: for each destination pixel, find the source pixel named by the weight
/// table (its own unit tap) and read 4 bytes from the ORIGINAL flat array by padded stride — a different
/// addressing path from the kernel's Row(y) view, so a stride mistake cannot cancel out.</item>
/// <item>Tight-vs-padded: the SAME pixel bytes laid out tight and laid out with padding must resize to
/// byte-identical output. This needs no tap derivation at all.</item>
/// </list>
/// </summary>
public class NearestBitCopyTests
{
    [Theory]
    [InlineData(26, 13, 8, 4)]     // 2:1 downscale -> fast path (unit tap fixed)
    [InlineData(13, 7, 4, 3)]      // non-integer downscale
    [InlineData(8, 19, 6, 11)]     // upscale
    [InlineData(16, 16, 5, 5)]     // identity
    [InlineData(5, 3, 2, 2)]       // tiny
    public void Nearest_OnPaddedStride_CopiesExactlyFourBytesInRange(
        int sourceWidth, int destinationWidth, int sourceHeight, int destinationHeight)
    {
        const int padding = 7;                                  // bytes of junk after each row's pixels
        var rowBytes = sourceWidth * 4;
        var stride = rowBytes + padding;
        var backing = new byte[stride * sourceHeight];

        // Every pixel gets four DISTINCT, position-dependent bytes; padding gets a sentinel 0xEE that
        // must never reach the output. A uint copy reading one byte too far would pull in the sentinel.
        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < sourceWidth; x++)
            {
                var at = y * stride + x * 4;
                backing[at] = (byte)(x * 31 + y * 17 + 1);
                backing[at + 1] = (byte)(x * 7 + y * 53 + 2);
                backing[at + 2] = (byte)(x * 13 + y * 29 + 3);
                backing[at + 3] = (byte)(x * 3 + y * 11 + 4);
            }
            for (var p = 0; p < padding; p++)
                backing[y * stride + rowBytes + p] = 0xEE;
        }

        using var source = WorkingBuffer.Wrap(new ArrayOwner(backing), sourceWidth, sourceHeight,
            stride, WorkingFormat.Rgba8);
        source.IsTight.ShouldBeFalse();

        using var result = Resample.Resize(source.View, destinationWidth, destinationHeight,
            Resampler.Nearest, pool: null);
        var actual = result.View.Span;

        var tableX = WeightTable.Build(sourceWidth, destinationWidth, Resampler.Nearest);
        var tableY = WeightTable.Build(sourceHeight, destinationHeight, Resampler.Nearest);

        for (var dy = 0; dy < destinationHeight; dy++)
        for (var dx = 0; dx < destinationWidth; dx++)
        {
            // Each destination pixel has its OWN unit tap; its position may move across the image, which
            // is exactly why the fast path only engages when they all coincide. Derive per pixel so the
            // reference is correct for both the byte-copy fast path and the separable fallback.
            var sx = SourceIndex(tableX, dx);
            var sy = SourceIndex(tableY, dy);
            var from = sy * stride + sx * 4;
            var to = dy * result.Stride + dx * 4;

            for (var channel = 0; channel < 4; channel++)
                actual[to + channel].ShouldBe(backing[from + channel],
                    $"pixel ({dx},{dy}) channel {channel} from source ({sx},{sy})");
        }
    }

    /// <summary>Robust invariant with NO tap derivation and NO sentinel scan: identical pixels packed tight
    /// vs with row padding must resize to byte-identical output. This equality alone catches a 4-byte copy
    /// that reads past the row's pixels — in the tight layout an over-read lands on the NEXT row's first
    /// pixels, in the padded layout on the 0xEE padding, so the two outputs differ. (Scanning for a fixed
    /// sentinel byte in the output would be a false-positive-prone ruler: a legitimate pixel byte can equal
    /// it. The tight/padded comparison needs no such assumption.)</summary>
    [Theory]
    [InlineData(26, 13)]
    [InlineData(13, 7)]
    [InlineData(8, 19)]
    [InlineData(16, 16)]
    public void Nearest_PaddedStride_MatchesTightLayout(int sourceWidth, int destinationWidth)
    {
        const int sourceHeight = 6, destinationHeight = 5, padding = 5;
        var rowBytes = sourceWidth * 4;
        var stride = rowBytes + padding;

        var tight = new byte[rowBytes * sourceHeight];
        var padded = new byte[stride * sourceHeight];
        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < sourceWidth; x++)
                for (var c = 0; c < 4; c++)
                    tight[y * rowBytes + x * 4 + c] = padded[y * stride + x * 4 + c] =
                        (byte)(x * 29 + y * 13 + c * 7 + 1);
            for (var p = 0; p < padding; p++)
                padded[y * stride + rowBytes + p] = 0xEE;
        }

        using var tightBuf = WorkingBuffer.Wrap(new ArrayOwner(tight), sourceWidth, sourceHeight,
            rowBytes, WorkingFormat.Rgba8);
        using var paddedBuf = WorkingBuffer.Wrap(new ArrayOwner(padded), sourceWidth, sourceHeight,
            stride, WorkingFormat.Rgba8);
        tightBuf.IsTight.ShouldBeTrue();
        paddedBuf.IsTight.ShouldBeFalse();

        using var fromTight = Resample.Resize(tightBuf.View, destinationWidth, destinationHeight,
            Resampler.Nearest, pool: null);
        using var fromPadded = Resample.Resize(paddedBuf.View, destinationWidth, destinationHeight,
            Resampler.Nearest, pool: null);

        fromPadded.View.Span.SequenceEqual(fromTight.View.Span).ShouldBeTrue(
            "padded and tight sources must produce identical nearest output; a uint copy that read past " +
            "the row's pixels into the padding would make them differ");
    }

    /// <summary>The source index a destination pixel d samples: Starts[d] plus the offset of its single
    /// unit-weight tap. Nearest tables carry exactly one tap of weight 1; the rest are zero.</summary>
    private static int SourceIndex(WeightTable table, int d)
    {
        var baseIndex = d * table.Taps;
        for (var k = 0; k < table.Taps; k++)
            if (table.Weights[baseIndex + k] == 1f)
                return table.Starts[d] + k;
        throw new InvalidOperationException($"destination {d} has no unit tap");
    }

    private sealed class ArrayOwner(byte[] array) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => array;
        public void Dispose() { }
    }
}
