using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// `Resize` with <see cref="Resampler.Nearest"/> now takes a byte-copy fast path instead of the separable
/// resampler: it reads the sampling index straight out of <c>WeightTable.Starts</c> and copies four bytes.
///
/// <para>That is only equivalent to the old path if the nearest table really is a single tap of weight one
/// over an in-range index — with one tap, the horizontal pass is <c>v · 1</c>, the vertical pass is the same,
/// and the float round trip is the identity on bytes. This test pins that precondition rather than assuming
/// it: if the table ever grows a second tap or stops normalising to one, the fast path must be switched off
/// and this fails instead of the resize quietly changing its pixels.</para>
/// </summary>
public class NearestResizeTests
{
    [Theory]
    [InlineData(1920, 960)]
    [InlineData(100, 33)]      // not an integer ratio
    [InlineData(7, 23)]        // upscale
    [InlineData(1, 1)]
    public void NearestTable_IsASingleUnitTapOverAValidIndex(int sourceLength, int destinationLength)
    {
        var table = WeightTable.Build(sourceLength, destinationLength, Resampler.Nearest);

        // The table keeps a window of taps; for nearest exactly one of them carries weight 1 and the rest are
        // zero. Asserting Taps == 1 would have been wrong and would have silently limited the fast path to the
        // ratios where the window happens to collapse to a single tap.
        for (var x = 0; x < destinationLength; x++)
        {
            var nonZero = 0;
            for (var k = 0; k < table.Taps; k++)
            {
                var weight = table.Weights[x * table.Taps + k];
                if (weight == 0f)
                    continue;

                Assert.Equal(1f, weight);
                nonZero++;
                Assert.InRange(table.Starts[x] + k, 0, sourceLength - 1);
            }

            Assert.Equal(1, nonZero);
        }
    }

    /// <summary>
    /// A stronger check than "a constant image stays constant": every source pixel gets a distinct byte, so a
    /// wrong sampling index changes the answer. The expected index is taken from the same table the fast path
    /// uses, which is the point — the claim being tested is that the operator copies the pixel the table names,
    /// not that the table is right about which pixel to name.
    /// </summary>
    [Theory]
    [InlineData(64, 16)]
    [InlineData(37, 11)]
    [InlineData(9, 20)]
    public void NearestResize_CopiesThePixelTheTableNames(int sourceWidth, int destinationWidth)
    {
        const int Height = 6;
        var source = new byte[sourceWidth * Height * 4];
        for (var i = 0; i < sourceWidth * Height; i++)
        {
            source[i * 4] = (byte)(i * 37 + 11);
            source[i * 4 + 1] = (byte)(i * 91 + 3);
            source[i * 4 + 2] = (byte)(i * 13 + 7);
            source[i * 4 + 3] = 255;
        }

        var tableX = WeightTable.Build(sourceWidth, destinationWidth, Resampler.Nearest);
        var tableY = WeightTable.Build(Height, Height, Resampler.Nearest);
        var startsX = tableX.Starts;
        var startsY = tableY.Starts;
        var tapX = UnitTap(tableX);
        var tapY = UnitTap(tableY);

        using var image = SimdImaging.Image.Create(sourceWidth, Height, WorkingFormat.Rgba8);
        source.AsSpan().CopyTo(image.WritablePixels);

        using var resized = image.Resize(destinationWidth, Height, Resampler.Nearest);
        var actual = resized.WritablePixels;

        for (var y = 0; y < Height; y++)
        for (var x = 0; x < destinationWidth; x++)
        {
            var from = ((startsY[y] + tapY) * sourceWidth + startsX[x] + tapX) * 4;
            var to = (y * destinationWidth + x) * 4;
            Assert.Equal(source[from], actual[to]);
            Assert.Equal(source[from + 1], actual[to + 1]);
            Assert.Equal(source[from + 2], actual[to + 2]);
            Assert.Equal(source[from + 3], actual[to + 3]);
        }
    }
    /// <summary>The one tap index that carries the weight for a nearest table.</summary>
    private static int UnitTap(WeightTable table)
    {
        for (var k = 0; k < table.Taps; k++)
        {
            if (table.Weights[k] == 1f)
                return k;
        }

        throw new InvalidOperationException("no unit tap");
    }

}
