using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class OctreeQuantizeTests
{
    private static Image TwoColours(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var red = x < width / 2;
                span[o] = (byte)(red ? 220 : 20);
                span[o + 1] = (byte)(red ? 30 : 40);
                span[o + 2] = (byte)(red ? 30 : 200);
                span[o + 3] = 255;
            }
        }

        return image;
    }

    [Fact]
    public void Octree_KeepsTwoWellSeparatedColoursExact()
    {
        using var source = TwoColours(16, 4);
        var palette = source.BuildPalette(2, Quantizer.Octree);

        palette.Count.ShouldBe(2);
        palette.Colors.ToArray().ShouldContain(new RgbaColor(220, 30, 30, 255));
        palette.Colors.ToArray().ShouldContain(new RgbaColor(20, 40, 200, 255));
    }

    [Fact]
    public void Octree_RespectsTheColourBudget()
    {
        using var source = Image.Create(128, 64, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = (byte)(i * 7);
            span[i + 1] = (byte)(i * 13);
            span[i + 2] = (byte)(i * 29);
            span[i + 3] = 255;
        }

        var palette = source.BuildPalette(16, Quantizer.Octree);
        palette.Count.ShouldBeLessThanOrEqualTo(16);

        using var quantized = source.Quantize(16, Quantizer.Octree);
        DistinctColours(quantized).Count.ShouldBeLessThanOrEqualTo(16);
    }

    [Fact]
    public void Octree_SingleColourImageNeedsOneEntry()
    {
        using var source = Image.Create(16, 16, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = 40;
            span[i + 1] = 90;
            span[i + 2] = 140;
            span[i + 3] = 255;
        }

        source.BuildPalette(8, Quantizer.Octree).Count.ShouldBe(1);
    }

    [Fact]
    public void Octree_IsDeterministic()
    {
        using var source = TwoColours(32, 32);
        using var first = source.Quantize(5, Quantizer.Octree);
        using var second = source.Quantize(5, Quantizer.Octree);

        second.WritablePixels.ToArray().ShouldBe(first.WritablePixels.ToArray());
    }

    [Fact]
    public void Octree_HandlesTheFloatPath()
    {
        using var source = Image.Create(8, 8, WorkingFormat.Rgba16);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(source.WritablePixels);
        for (var i = 0; i < samples.Length; i += 4)
        {
            var left = (i / 4) % 8 < 4;
            samples[i] = (ushort)(left ? 200 * 257 : 40 * 257);
            samples[i + 1] = (ushort)(100 * 257);
            samples[i + 2] = (ushort)(left ? 30 * 257 : 220 * 257);
            samples[i + 3] = ushort.MaxValue;
        }

        var palette = source.BuildPalette(2, Quantizer.Octree);
        palette.Count.ShouldBe(2);
        palette.Colors.ToArray().ShouldContain(new RgbaColor(200, 100, 30, 255));
        palette.Colors.ToArray().ShouldContain(new RgbaColor(40, 100, 220, 255));
    }

    [Fact]
    public void ReduceColors_WithDitherStaysOnThePalette()
    {
        using var source = Gradient(64, 16);
        using var reduced = source.ReduceColors(8, Quantizer.Octree);

        var allowed = new HashSet<(byte, byte, byte)>();
        foreach (var colour in source.BuildPalette(8, Quantizer.Octree).Colors)
            allowed.Add((colour.R, colour.G, colour.B));

        foreach (var colour in DistinctColours(reduced))
            allowed.ShouldContain(colour);
    }

    [Fact]
    public void ReduceColors_WithoutDitherMapsEachPixelAlone()
    {
        using var source = Gradient(32, 8);
        var palette = source.BuildPalette(6, Quantizer.Octree);
        using var mapped = source.MapToPalette(palette);
        using var reduced = source.ReduceColors(6, Quantizer.Octree, DitherKernel.None);

        reduced.WritablePixels.ToArray().ShouldBe(mapped.WritablePixels.ToArray());
    }

    [Fact]
    public void ReduceColors_RejectsNegativeError()
    {
        using var source = TwoColours(4, 4);
        Should.Throw<ArgumentOutOfRangeException>(() => source.ReduceColors(4, maxError: -1f));
    }

    private static Image Gradient(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                span[o] = (byte)(x * 4);
                span[o + 1] = (byte)(y * 16);
                span[o + 2] = (byte)(255 - x * 3);
                span[o + 3] = 255;
            }
        }

        return image;
    }

    private static HashSet<(byte, byte, byte)> DistinctColours(Image image)
    {
        var set = new HashSet<(byte, byte, byte)>();
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
            set.Add((span[i], span[i + 1], span[i + 2]));
        return set;
    }
}
