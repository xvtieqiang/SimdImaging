using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class PorterDuffTests
{
    private static Image Solid(int size, byte r, byte g, byte b, byte a)
    {
        var image = Image.Create(size, size, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = r;
            span[i + 1] = g;
            span[i + 2] = b;
            span[i + 3] = a;
        }

        return image;
    }

    private static byte[] At(Image image, int x, int y)
    {
        var o = (y * image.Width + x) * 4;
        return image.WritablePixels.Slice(o, 4).ToArray();
    }

    private static Image Compose(PorterDuff mode, byte sourceAlpha = 255)
    {
        var background = Solid(3, 0, 0, 255, 255);
        var overlay = Solid(1, 255, 0, 0, sourceAlpha);
        using (background)
        using (overlay)
        {
            return background.Composite(overlay, 1, 1, mode);
        }
    }

    [Theory]
    [InlineData(PorterDuff.Source, 255, 0, 0, 255)]
    [InlineData(PorterDuff.SourceOver, 255, 0, 0, 255)]
    [InlineData(PorterDuff.SourceIn, 255, 0, 0, 255)]
    [InlineData(PorterDuff.SourceAtop, 255, 0, 0, 255)]
    [InlineData(PorterDuff.Destination, 0, 0, 255, 255)]
    [InlineData(PorterDuff.DestinationIn, 0, 0, 255, 255)]
    [InlineData(PorterDuff.DestinationAtop, 0, 0, 255, 255)]
    [InlineData(PorterDuff.DestinationOver, 0, 0, 255, 255)]
    [InlineData(PorterDuff.Clear, 0, 0, 0, 0)]
    [InlineData(PorterDuff.SourceOut, 0, 0, 0, 0)]
    [InlineData(PorterDuff.DestinationOut, 0, 0, 0, 0)]
    [InlineData(PorterDuff.Xor, 0, 0, 0, 0)]
    public void OpaqueOverOpaque(PorterDuff mode, byte r, byte g, byte b, byte a)
    {
        using var result = Compose(mode);
        At(result, 1, 1).ShouldBe(new[] { r, g, b, a });
    }

    [Fact]
    public void DestinationOut_ActsAsAnEraserAtPartialAlpha()
    {
        using var result = Compose(PorterDuff.DestinationOut, sourceAlpha: 128);

        // 1 - 128/255 ≈ 0.498 → alpha ≈ 127
        var centre = At(result, 1, 1);
        ((int)centre[3]).ShouldBeInRange(126, 128);
        centre[2].ShouldBe((byte)255);
    }

    [Fact]
    public void Xor_OfPartialCoIsPartialOnBothSides()
    {
        using var result = Compose(PorterDuff.Xor, sourceAlpha: 128);

        var centre = At(result, 1, 1);
        // Fa = 1 - da = 0, Fb = 1 - sa ≈ 0.498 → only the destination survives at half alpha.
        ((int)centre[3]).ShouldBeInRange(126, 128);
        centre[2].ShouldBe((byte)255);
    }

    [Fact]
    public void OutsideTheOverlayIsUntouched()
    {
        using var result = Compose(PorterDuff.Clear);
        At(result, 0, 0).ShouldBe(new byte[] { 0, 0, 255, 255 });
    }

    [Fact]
    public void MismatchedFormatsAreRejected()
    {
        using var background = Solid(2, 0, 0, 0, 255);
        using var overlay = Image.Create(1, 1, WorkingFormat.Rgba16);

        Should.Throw<ArgumentException>(() => background.Composite(overlay, 0, 0, PorterDuff.SourceOver));
    }
}
