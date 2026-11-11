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

    [Fact]
    public void Load_ScaleDenominator_ProducesTheExactReduction()
    {
        using var image = TestImages.Gradient(512, 512, WorkingFormat.Rgba8);
        var jpeg = Image.Encode(image, ImageFormat.Jpeg);

        using var scaled = Image.Load(jpeg, new LoadOptions { ScaleDenominator = 16 });

        scaled.Width.ShouldBe(32);
        scaled.Height.ShouldBe(32);
    }

    [Fact]
    public void Load_PreferNativeScale_TakesTheCodecsOwnReduction()
    {
        using var image = TestImages.Gradient(512, 512, WorkingFormat.Rgba8);
        var jpeg = Image.Encode(image, ImageFormat.Jpeg);

        // JPEG scales natively to 1/8 at most, so 1/16 (32x32) negotiates to 1/8: 64x64, without the box filter.
        using var native = Image.Load(jpeg, new LoadOptions { ScaleDenominator = 16, PreferNativeScale = true });

        native.Width.ShouldBe(64);
        native.Height.ShouldBe(64);
    }

    [Fact]
    public void Load_PreferNativeScale_RejectsARegion()
    {
        using var image = TestImages.Gradient(64, 64, WorkingFormat.Rgba8);
        var png = Image.Encode(image, ImageFormat.Png);

        // A region is expressed at the requested scale; negotiating another one would reinterpret its coordinates.
        Should.Throw<ArgumentException>(() => Image.Load(png, new LoadOptions
        {
            ScaleDenominator = 4,
            PreferNativeScale = true,
            Region = new PixelRect(0, 0, 8, 8),
        }));
    }

    [Fact]
    public void Load_Stream_BuffersTheRestAndLeavesTheStreamOpen()
    {
        using var image = TestImages.Gradient(9, 5, WorkingFormat.Rgba8);
        var png = Image.Encode(image, ImageFormat.Png);

        using var seekable = new MemoryStream(png);
        using var fromMemory = Image.Load(seekable);
        fromMemory.WritablePixels.ToArray().ShouldBe(image.WritablePixels.ToArray());
        seekable.CanRead.ShouldBeTrue();          // the caller still owns it
        seekable.Position.ShouldBe(png.Length);   // and it was consumed to the end

        using var opaque = new NonSeekableStream(png);
        using var fromStream = Image.Load(opaque);
        fromStream.WritablePixels.ToArray().ShouldBe(image.WritablePixels.ToArray());
    }

    [Fact]
    public void TryLoad_Stream_ReturnsFalseInsteadOfThrowing()
    {
        using var image = TestImages.Gradient(4, 4, WorkingFormat.Rgba8);
        var png = Image.Encode(image, ImageFormat.Png);

        using var good = new MemoryStream(png);
        Image.TryLoad(good, out var loaded).ShouldBeTrue();
        loaded.ShouldNotBeNull();
        loaded!.Dispose();

        using var bad = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        Image.TryLoad(bad, out var none).ShouldBeFalse();
        none.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_MatchesTheSynchronousResult()
    {
        using var image = TestImages.Gradient(7, 4, WorkingFormat.Rgba8);
        var png = Image.Encode(image, ImageFormat.Png);
        var expected = image.WritablePixels.ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"simd-async-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);
        try
        {
            using var fromStream = await Image.LoadAsync(new MemoryStream(png));
            fromStream.WritablePixels.ToArray().ShouldBe(expected);

            using var fromPath = await Image.LoadAsync(path);
            fromPath.WritablePixels.ToArray().ShouldBe(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryLoadAsync_ReturnsNullInsteadOfThrowing()
    {
        using var bad = new MemoryStream([1, 2, 3, 4, 5]);

        (await Image.TryLoadAsync(bad)).ShouldBeNull();
    }

    [Fact]
    public void LoadOptions_ScaleDenominatorMustBeAPowerOfTwo()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new LoadOptions { ScaleDenominator = 3 });
        Should.Throw<ArgumentOutOfRangeException>(() => new LoadOptions { ScaleDenominator = 0 });
        new LoadOptions { ScaleDenominator = 8 }.ScaleDenominator.ShouldBe(8);
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

/// <summary>A stream that only offers <see cref="Read"/>: no length, no position, no seeking — what an HTTP
/// response body looks like, and the case the byte-array fast paths cannot help with.</summary>
internal sealed class NonSeekableStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data, writable: false);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
