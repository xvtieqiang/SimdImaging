using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Tests;

public class PixelBufferTests
{
    [Fact]
    public void Slice_IsZeroCopyAndKeepsStride()
    {
        using var image = Image.Create(4, 4, WorkingFormat.Rgba8);
        var pixels = image[0].Pixels;
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)i;

        var slice = pixels.View.Slice(new PixelRect(1, 1, 2, 2));

        slice.Width.ShouldBe(2);
        slice.Height.ShouldBe(2);
        slice.Stride.ShouldBe(pixels.Stride);
        slice.Row(0)[0].ShouldBe((byte)((1 * 4 + 1) * 4));
    }

    [Fact]
    public void TryCopyTo_Bgr24_SwizzlesChannels()
    {
        using var image = Image.Create(1, 1, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        span[0] = 10;   // R
        span[1] = 20;   // G
        span[2] = 30;   // B
        span[3] = 255;  // A

        var destination = new byte[3];
        image[0].Pixels.TryCopyTo(destination, stride: 0, PixelFormat.Bgr24).ShouldBeTrue();

        destination.ShouldBe(new byte[] { 30, 20, 10 });
    }

    [Fact]
    public void TryCopyTo_TooSmall_ReturnsFalse()
    {
        using var image = Image.Create(4, 4, WorkingFormat.Rgba8);
        var destination = new byte[10];

        image[0].Pixels.TryCopyTo(destination, stride: 0, PixelFormat.Rgba32).ShouldBeFalse();
    }

    [Fact]
    public void NonTightBuffer_IsNotExposedAsContiguous()
    {
        var memory = new byte[2 * 12];             // 2 rows of 2 Rgba8 pixels, 4 bytes of padding each
        using var buffer = WorkingBuffer.Wrap(new ArrayOwner(memory), 2, 2, stride: 12, WorkingFormat.Rgba8);

        buffer.IsTight.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => { _ = ((IMemoryOwner<byte>)buffer).Memory; });
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        using var image = Image.Create(2, 2, WorkingFormat.Rgba8);
        image.WritablePixels.Fill(7);

        using var clone = image[0].Pixels.Clone();
        clone.View.Span.Fill(9);

        image.WritablePixels[0].ShouldBe((byte)7);
        clone.View.Span[0].ShouldBe((byte)9);
        clone.IsTight.ShouldBeTrue();
    }

    [Fact]
    public void Rent_IsTightAndSizedInBytes()
    {
        using var buffer = WorkingBuffer.Rent(3, 2, WorkingFormat.Rgba16);

        buffer.Stride.ShouldBe(3 * 8);
        buffer.IsTight.ShouldBeTrue();
        buffer.View.Span.Length.ShouldBe(3 * 2 * 8);
    }

    private sealed class ArrayOwner(byte[] array) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => array;
        public void Dispose() { }
    }
}
