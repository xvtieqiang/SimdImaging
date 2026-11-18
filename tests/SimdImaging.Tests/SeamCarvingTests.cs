using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// M12 seam carving. The assertions are structural on purpose: "the subject survived" has no numeric definition
/// until someone writes one, so these tests pin the parts that are checkable — exact sizes, the tie-break rule,
/// a high-energy square keeping its width, horizontal being the transpose of vertical, and the pixels that are
/// kept staying byte-identical.
/// </summary>
public class SeamCarvingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void RemovingSeams_ShrinksByExactlyThatManyColumns(int count)
    {
        using var image = Bars(40, 24);
        using var result = image.Pipe().RemoveSeams(SeamAxis.Vertical, count).ToImage();

        result.Width.ShouldBe(40 - count);
        result.Height.ShouldBe(24);
    }

    [Fact]
    public void RemovingRows_UsesTheSameRuleOnTheOtherAxis()
    {
        using var image = Bars(24, 30);
        using var result = image.Pipe().RemoveSeams(SeamAxis.Horizontal, 5).ToImage();

        result.Width.ShouldBe(24);
        result.Height.ShouldBe(25);
    }

    /// <summary>
    /// Every column of this image has the same energy (the row alternates 0/10 in x and is constant in y), so the
    /// programme is a field of exact ties. The documented tie-break — top neighbour first, then left, then right,
    /// and the leftmost minimum as the start — therefore has to remove column 0. The alternating pattern makes
    /// "which column went" visible in the bytes.
    /// </summary>
    [Fact]
    public void AFieldOfTies_RemovesTheLeftmostColumn()
    {
        using var image = Ties(12, 5, WorkingFormat.Rgba8);
        using var result = image.Pipe().RemoveSeams(SeamAxis.Vertical, 1).ToImage();

        var before = image.WritablePixels;
        var after = result.WritablePixels;
        after[(0 * 11 + 0) * 4 + 3].ShouldBe(before[(0 * 12 + 1) * 4 + 3],
            "the removed path must be column 0, so column 0 of the result is column 1 of the source");
        after[(4 * 11 + 0) * 4 + 3].ShouldBe(before[(4 * 12 + 1) * 4 + 3]);
    }

    [Fact]
    public void AHighEnergySquareKeepsItsWidth()
    {
        using var image = Square(40, 20);
        using var result = image.Pipe().RemoveSeams(SeamAxis.Vertical, 5).ToImage();

        BrightRun(image, 10).ShouldBe(10);
        BrightRun(result, 10).ShouldBe(10, "five seams must come out of the background, not the subject");
    }

    [Fact]
    public void HorizontalSeamsMatchTheTransposeOfVerticalOnes()
    {
        using var image = Bars(21, 17);
        using var horizontal = image.Pipe().RemoveSeams(SeamAxis.Horizontal, 3).ToImage();
        using var turned = image.Pipe().Transpose().ToImage();
        using var carved = turned.Pipe().RemoveSeams(SeamAxis.Vertical, 3).ToImage();
        using var transposed = carved.Pipe().Transpose().ToImage();

        horizontal.WritablePixels.ToArray().ShouldBe(transposed.WritablePixels.ToArray());
    }

    [Fact]
    public void InsertingThenRemoving_ReturnsTheSize()
    {
        using var image = Bars(30, 22);
        using var grown = image.Pipe().InsertSeams(SeamAxis.Vertical, 4).ToImage();
        grown.Width.ShouldBe(34);

        using var back = grown.Pipe().RemoveSeams(SeamAxis.Vertical, 4).ToImage();
        back.Width.ShouldBe(30);
        back.Height.ShouldBe(22);
    }

    [Fact]
    public void InsertionOnAFlatImage_IsInvariant()
    {
        using var image = Solid(14, 9, 73);
        using var grown = image.Pipe().InsertSeams(SeamAxis.Vertical, 3).ToImage();
        using var back = grown.Pipe().RemoveSeams(SeamAxis.Vertical, 3).ToImage();

        grown.Width.ShouldBe(17);
        back.WritablePixels.ToArray().ShouldBe(image.WritablePixels.ToArray());
    }

    [Fact]
    public void CarveTo_ReachesTheTargetOnBothAxes()
    {
        using var image = Bars(30, 24);
        using var smaller = image.Pipe().SeamCarve(24, 18).ToImage();
        smaller.Width.ShouldBe(24);
        smaller.Height.ShouldBe(18);

        using var bigger = image.Pipe().SeamCarve(33, 27).ToImage();
        bigger.Width.ShouldBe(33);
        bigger.Height.ShouldBe(27);
    }

    [Theory]
    [InlineData(WorkingFormat.Rgba8)]
    [InlineData(WorkingFormat.Rgba16)]
    [InlineData(WorkingFormat.Rgba32F)]
    public void OtherFormats_KeepTheirBytes(WorkingFormat format)
    {
        using var image = Ties(20, 15, format);
        using var result = image.Pipe().RemoveSeams(SeamAxis.Vertical, 3).ToImage();

        result.Width.ShouldBe(17);
        result.WritablePixels.ToArray().ShouldBe(KeptColumns(image, 3, result.Width));
    }

    [Fact]
    public void RunningTwiceGivesTheSameBytes()
    {
        using var image = Bars(23, 19);
        using var first = image.Pipe().RemoveSeams(SeamAxis.Vertical, 4).ToImage();
        using var second = image.Pipe().RemoveSeams(SeamAxis.Vertical, 4).ToImage();

        first.WritablePixels.ToArray().ShouldBe(second.WritablePixels.ToArray());
    }

    [Fact]
    public void CountsThatWouldEmptyTheImageThrow()
    {
        using var image = Bars(9, 6);

        Should.Throw<ArgumentOutOfRangeException>(() => image.Pipe().RemoveSeams(SeamAxis.Vertical, 9).ToImage());
        Should.Throw<ArgumentOutOfRangeException>(() => image.Pipe().RemoveSeams(SeamAxis.Vertical, -1).ToImage());
        Should.Throw<ArgumentOutOfRangeException>(() => image.Pipe().RemoveSeams(SeamAxis.Horizontal, 6).ToImage());
        Should.Throw<ArgumentOutOfRangeException>(() => image.Pipe().SeamCarve(0, 5).ToImage());
        Should.Throw<ArgumentOutOfRangeException>(() => image.Pipe().SeamCarve(5, 0).ToImage());
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Vertical three-pixel bars with a textured lower third: strong vertical edges, no flat margin.</summary>
    private static Image Bars(int width, int height) => Bars(width, height, WorkingFormat.Rgba8);

    private static Image Bars(int width, int height, WorkingFormat format)
    {
        var image = Image.Create(width, height, format);
        var pixels = image.WritablePixels;
        var bytesPerPixel = format.BytesPerPixel();

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (x / 3) % 2 == 0 ? 232 : 24;
            if (y * 3 > height * 2)
                value = (x + y) % 7 < 3 ? 196 : 48;

            var offset = (y * width + x) * bytesPerPixel;
            if (format == WorkingFormat.Rgba32F)
            {
                BitConverter.TryWriteBytes(pixels.Slice(offset, 4), value / 255f);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 4, 4), value / 255f);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 8, 4), value / 255f);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 12, 4), 1f);
            }
            else if (format == WorkingFormat.Rgba16)
            {
                var sample = (ushort)(value * 257);
                BitConverter.TryWriteBytes(pixels.Slice(offset, 2), sample);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 2, 2), sample);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 4, 2), sample);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 6, 2), ushort.MaxValue);
            }
            else
            {
                pixels[offset] = (byte)value;
                pixels[offset + 1] = (byte)value;
                pixels[offset + 2] = (byte)value;
                pixels[offset + 3] = 255;
            }
        }

        return image;
    }

    /// <summary>
    /// Every column has exactly zero energy: the colour is constant, so both central differences vanish. The
    /// alpha ramp makes the chosen path visible in the bytes without contributing to the energy — the operator
    /// reads luma from RGB only.
    /// </summary>
    private static Image Ties(int width, int height, WorkingFormat format)
    {
        var image = Image.Create(width, height, format);
        var pixels = image.WritablePixels;
        var bytesPerPixel = format.BytesPerPixel();

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * bytesPerPixel;
            var alpha = 20 + x * 10;
            if (format == WorkingFormat.Rgba32F)
            {
                for (var c = 0; c < 3; c++)
                    BitConverter.TryWriteBytes(pixels.Slice(offset + c * 4, 4), 50 / 255f);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 12, 4), alpha / 255f);
            }
            else if (format == WorkingFormat.Rgba16)
            {
                var sample = (ushort)(50 * 257);
                BitConverter.TryWriteBytes(pixels.Slice(offset, 2), sample);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 2, 2), sample);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 4, 2), sample);
                BitConverter.TryWriteBytes(pixels.Slice(offset + 6, 2), (ushort)(alpha * 257));
            }
            else
            {
                pixels[offset] = 50;
                pixels[offset + 1] = 50;
                pixels[offset + 2] = 50;
                pixels[offset + 3] = (byte)alpha;
            }
        }

        return image;
    }

    private static Image Solid(int width, int height, byte value)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return image;
    }

    /// <summary>A bright square on a dark field: removing seams through it would shrink it.</summary>
    private static Image Square(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var inside = x >= 15 && x < 25 && y >= 5 && y < 15;
            var value = (byte)(inside ? 240 : 10);
            var offset = (y * width + x) * 4;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }

        return image;
    }

    private static int BrightRun(Image image, int row)
    {
        var pixels = image.WritablePixels;
        var width = image.Width;
        var count = 0;
        for (var x = 0; x < width; x++)
        {
            if (pixels[(row * width + x) * 4] >= 200)
                count++;
        }

        return count;
    }

    /// <summary>Columns <c>from..from+count-1</c> of the source, row by row — the exact bytes a seam removal must
    /// keep when every path comes out of the left edge.</summary>
    private static byte[] KeptColumns(Image image, int from, int count)
    {
        var pixels = image.WritablePixels;
        var bytesPerPixel = image[0].Pixels.View.Format.BytesPerPixel();
        var kept = new byte[count * image.Height * bytesPerPixel];
        for (var y = 0; y < image.Height; y++)
            pixels.Slice((y * image.Width + from) * bytesPerPixel, count * bytesPerPixel)
                .CopyTo(kept.AsSpan(y * count * bytesPerPixel));

        return kept;
    }
}
