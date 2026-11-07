using System.Buffers;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Tests;

internal static class TestImages
{
    /// <summary>A deterministic, alpha-varying test image. Alpha must vary, or PNG may pick an RGB colour type
    /// and the round trip would compare 3 channels against 4.</summary>
    public static Image Gradient(int width, int height, WorkingFormat format)
    {
        var image = Image.Create(width, height, format);
        var span = image.WritablePixels;
        switch (format)
        {
            case WorkingFormat.Rgba8:
                for (var i = 0; i < span.Length; i++)
                    span[i] = (byte)(i * 31 + 7);
                break;

            case WorkingFormat.Rgba16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(span);
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = (ushort)(i * 1031 + 13);
                break;
            }

            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(span);
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = i * 0.001f;
                break;
            }
        }

        return image;
    }
}

public class ImageIoTests
{
    [Fact]
    public void Png_Rgba8_RoundTripsByteExact()
    {
        using var image = TestImages.Gradient(37, 23, WorkingFormat.Rgba8);
        var expected = image.WritablePixels.ToArray();

        var png = Image.Encode(image, ImageFormat.Png);
        using var decoded = Image.Load(png);

        decoded.Width.ShouldBe(37);
        decoded.Height.ShouldBe(23);
        decoded.Format.ShouldBe(WorkingFormat.Rgba8);
        decoded.WritablePixels.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void Png_Rgba16_RoundTripsByteExact()
    {
        using var image = TestImages.Gradient(19, 11, WorkingFormat.Rgba16);
        var expected = image.WritablePixels.ToArray();

        var png = Image.Encode(image, ImageFormat.Png);
        using var decoded = Image.Load(png);

        decoded.Format.ShouldBe(WorkingFormat.Rgba16);
        decoded.Info.BitDepth.ShouldBe(16);
        decoded.WritablePixels.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void Jpeg_Rgba8_KeepsDimensions()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var jpeg = Image.Encode(image, ImageFormat.Jpeg, new SaveOptions { JpegQuality = 85 });

        using var decoded = Image.Load(jpeg);
        decoded.Width.ShouldBe(64);
        decoded.Height.ShouldBe(48);
        decoded.Format.ShouldBe(WorkingFormat.Rgba8);
        decoded.WritablePixels.Length.ShouldBe(64 * 48 * 4);
    }

    [Fact]
    public void Region_DecodesOnlyTheRequestedRect()
    {
        using var image = TestImages.Gradient(40, 30, WorkingFormat.Rgba8);
        var png = Image.Encode(image, ImageFormat.Png);

        using var cropped = Image.Load(png, new LoadOptions { Region = new PixelRect(4, 6, 10, 8) });

        cropped.Width.ShouldBe(10);
        cropped.Height.ShouldBe(8);
    }

    [Fact]
    public void MaxPixels_RefusesBeforeAllocating()
    {
        using var image = TestImages.Gradient(40, 30, WorkingFormat.Rgba8);
        var png = Image.Encode(image, ImageFormat.Png);

        Should.Throw<InvalidOperationException>(() => Image.Load(png, new LoadOptions { MaxPixels = 100 }));
    }

    [Fact]
    public void Dispose_ReleasesTheFrame()
    {
        var image = TestImages.Gradient(4, 4, WorkingFormat.Rgba8);
        image.Dispose();

        Should.Throw<ObjectDisposedException>(() => { _ = image.WritablePixels; });
    }

    [Fact]
    public void Pool_ReturnsExactlyOneBuffer()
    {
        using var source = TestImages.Gradient(16, 16, WorkingFormat.Rgba8);
        var png = Image.Encode(source, ImageFormat.Png);

        var pool = new CountingPool();
        var image = Image.Load(png, new LoadOptions { Pool = pool });
        pool.Rented.ShouldBe(1);
        pool.Returned.ShouldBe(0);

        image.Dispose();
        pool.Returned.ShouldBe(1);
    }

    [Fact]
    public void FloatImage_CannotBeEncodedWithoutAToneMap()
    {
        using var image = Image.Create(4, 4, WorkingFormat.Rgba32F);
        Should.Throw<NotSupportedException>(() => Image.Encode(image, ImageFormat.Png));
    }

    [Fact]
    public void Garbage_IsRejected()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        Image.TryLoad(garbage, out var image).ShouldBeFalse();
        image.ShouldBeNull();
        Should.Throw<InvalidDataException>(() => { _ = Image.Load(garbage); });
    }

    [Fact]
    public void JpegQuality_OnNonJpeg_IsRejected()
    {
        using var image = TestImages.Gradient(8, 8, WorkingFormat.Rgba8);
        Should.Throw<ArgumentException>(() =>
            Image.Encode(image, ImageFormat.Png, new SaveOptions { JpegQuality = 80 }));
    }

    [Fact]
    public void SaveAndLoad_ByPathRoundTrips()
    {
        using var image = TestImages.Gradient(12, 9, WorkingFormat.Rgba8);
        var path = Path.Combine(Path.GetTempPath(), $"simdimaging-{Guid.NewGuid():N}.png");
        try
        {
            Image.Save(image, path);
            File.Exists(path).ShouldBeTrue();

            using var decoded = Image.Load(path);
            decoded.Width.ShouldBe(12);
            decoded.WritablePixels.ToArray().ShouldBe(image.WritablePixels.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnknownExtension_IsRejected()
    {
        using var image = TestImages.Gradient(4, 4, WorkingFormat.Rgba8);
        Should.Throw<NotSupportedException>(() =>
            Image.Save(image, Path.Combine(Path.GetTempPath(), "x.not-a-format")));
    }
}

internal sealed class CountingPool : IPixelPool
{
    public int Rented;
    public int Returned;

    public IMemoryOwner<byte> Rent(int length)
    {
        Rented++;
        return new Owner(this, System.Buffers.ArrayPool<byte>.Shared.Rent(length));
    }

    private sealed class Owner(CountingPool pool, byte[] array) : IMemoryOwner<byte>
    {
        private CountingPool? _pool = pool;
        private byte[]? _array = array;

        public Memory<byte> Memory => _array ?? throw new ObjectDisposedException(nameof(CountingPool));

        public void Dispose()
        {
            var p = Interlocked.Exchange(ref _pool, null);
            var a = Interlocked.Exchange(ref _array, null);
            if (p is not null && a is not null)
            {
                p.Returned++;
                System.Buffers.ArrayPool<byte>.Shared.Return(a);
            }
        }
    }
}
