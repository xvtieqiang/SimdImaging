namespace SimdImaging.Tests;

public class QuantizeTests
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

    private static HashSet<(byte, byte, byte)> DistinctColours(Image image)
    {
        var set = new HashSet<(byte, byte, byte)>();
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
            set.Add((span[i], span[i + 1], span[i + 2]));
        return set;
    }

    [Fact]
    public void Quantize_KeepsTwoColourImagesExact()
    {
        using var source = TwoColours(8, 4);
        using var quantized = source.Quantize(2);

        var colours = DistinctColours(quantized);
        colours.Count.ShouldBe(2);
        colours.ShouldContain(((byte)220, (byte)30, (byte)30));
        colours.ShouldContain(((byte)20, (byte)40, (byte)200));
    }

    [Fact]
    public void Quantize_RespectsTheColourBudget()
    {
        using var source = Image.Create(256, 1, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var x = 0; x < 256; x++)
        {
            var o = x * 4;
            span[o] = (byte)x;
            span[o + 1] = (byte)(255 - x);
            span[o + 2] = (byte)(x / 2);
            span[o + 3] = 255;
        }

        using var quantized = source.Quantize(4);
        DistinctColours(quantized).Count.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public void Quantize_PreservesAlpha()
    {
        using var source = TwoColours(6, 2);
        var span = source.WritablePixels;
        for (var i = 3; i < span.Length; i += 4)
            span[i] = 77;

        using var quantized = source.Quantize(2);
        var result = quantized.WritablePixels;
        for (var i = 3; i < result.Length; i += 4)
            result[i].ShouldBe((byte)77);
    }

    [Fact]
    public void BuildPalette_ReturnsTheRequestedCount()
    {
        using var source = Image.Create(64, 64, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = (byte)(i * 7);
            span[i + 1] = (byte)(i * 13);
            span[i + 2] = (byte)(i * 29);
            span[i + 3] = 255;
        }

        var palette = source.BuildPalette(16);
        palette.Count.ShouldBe(16);
    }

    [Fact]
    public void MapToPalette_UsesOnlyPaletteColours()
    {
        using var source = TwoColours(4, 4);
        var palette = new ColorPalette(new[]
        {
            new RgbaColor(0, 0, 0, 255),
            new RgbaColor(255, 255, 255, 255),
        });

        using var mapped = source.MapToPalette(palette);

        foreach (var colour in DistinctColours(mapped))
            colour.ShouldBeOneOf(((byte)0, (byte)0, (byte)0), ((byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public void Quantize_RejectsIllegalBudgets()
    {
        using var source = TwoColours(2, 2);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Quantize(1));
        Should.Throw<ArgumentOutOfRangeException>(() => source.Quantize(257));
    }

    [Fact]
    public void Quantize_IsDeterministic()
    {
        using var source = TwoColours(16, 16);
        using var first = source.Quantize(3);
        using var second = source.Quantize(3);

        second.WritablePixels.ToArray().ShouldBe(first.WritablePixels.ToArray());
    }

    [Fact]
    public void Wu_KeepsTwoColourImagesClose()
    {
        using var source = TwoColours(16, 4);
        using var quantized = source.Quantize(2, SimdImaging.Processing.Quantizer.Wu);

        var colours = DistinctColours(quantized);
        if (colours.Count != 2)
            throw new Xunit.Sdk.XunitException($"count={colours.Count}; colours={string.Join(" | ", colours)}");
        colours.Any(c => Math.Abs(c.Item1 - 220) <= 2 && Math.Abs(c.Item2 - 30) <= 2 && Math.Abs(c.Item3 - 30) <= 2)
            .ShouldBeTrue();
        colours.Any(c => Math.Abs(c.Item1 - 20) <= 2 && Math.Abs(c.Item2 - 40) <= 2 && Math.Abs(c.Item3 - 200) <= 2)
            .ShouldBeTrue();
    }

    [Fact]
    public void Wu_RespectsTheColourBudget()
    {
        using var source = Image.Create(128, 2, WorkingFormat.Rgba8);
        var span = source.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = (byte)(i * 3);
            span[i + 1] = (byte)(255 - i * 5);
            span[i + 2] = (byte)(i * 11);
            span[i + 3] = 255;
        }

        using var quantized = source.Quantize(8, SimdImaging.Processing.Quantizer.Wu);
        DistinctColours(quantized).Count.ShouldBeLessThanOrEqualTo(8);
    }

    [Fact]
    public void Wu_SingleColourImageNeedsOneEntry()
    {
        var image = Image.Create(8, 8, WorkingFormat.Rgba8);
        using var _ = image;
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = 40;
            span[i + 1] = 90;
            span[i + 2] = 140;
            span[i + 3] = 255;
        }

        image.BuildPalette(8, SimdImaging.Processing.Quantizer.Wu).Count.ShouldBe(1);
    }

    [Fact]
    public void Wu_IsDeterministic()
    {
        using var source = TwoColours(12, 12);
        using var first = source.Quantize(5, SimdImaging.Processing.Quantizer.Wu);
        using var second = source.Quantize(5, SimdImaging.Processing.Quantizer.Wu);

        second.WritablePixels.ToArray().ShouldBe(first.WritablePixels.ToArray());
    }
}
