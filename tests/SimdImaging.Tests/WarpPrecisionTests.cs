using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// P0 precision regressions for Warp / SampleRaw. The fixed inputs come from the independent
/// reference-vector package in tests/Fixtures/vectors, so these assertions are not snapshots of
/// whatever the library happened to produce.
/// </summary>
public class WarpPrecisionTests
{
    private sealed record VectorCase(int Width, int Height, WorkingFormat Format, byte[] Input, byte[] Expected);

    private static void Identity(float x, float y, out float sourceX, out float sourceY)
    {
        sourceX = x;
        sourceY = y;
    }

    [Theory]
    [InlineData("warp-identity-rgba8", WorkingFormat.Rgba8)]
    [InlineData("warp-identity-rgba16", WorkingFormat.Rgba16)]
    [InlineData("warp-identity-rgba32f", WorkingFormat.Rgba32F)]
    public void Warp_Identity_IsByteExactInEveryWorkingFormat(string caseId, WorkingFormat format)
    {
        var vector = Load(caseId);
        vector.Format.ShouldBe(format);

        using var image = Image.Create(vector.Width, vector.Height, vector.Format);
        vector.Input.AsSpan().CopyTo(image.WritablePixels);
        using var warped = image.Pipe().Warp(vector.Width, vector.Height, Identity, Resampler.Bilinear).ToImage();

        warped.WritablePixels.ToArray().ShouldBe(vector.Expected);
    }

    [Fact]
    public void Warp_Rgba32F_KeepsHdrNegativeAndOutOfRangeFloats()
    {
        var vector = Load("warp-identity-rgba32f");
        using var image = Image.Create(vector.Width, vector.Height, vector.Format);
        vector.Input.AsSpan().CopyTo(image.WritablePixels);
        using var warped = image.Pipe().Warp(vector.Width, vector.Height, Identity, Resampler.Bilinear).ToImage();

        var samples = MemoryMarshal.Cast<byte, float>(warped.WritablePixels).ToArray();
        samples.ShouldContain(4f);       // HDR > 1
        samples.ShouldContain(-0.25f);   // negative
        samples.ShouldContain(12.5f);    // out of range
        samples.ShouldBe(MemoryMarshal.Cast<byte, float>(vector.Input.AsSpan()).ToArray());
    }

    [Fact]
    public void Warp_Rgba16_BilinearDoesNotTruncateToTheHighByte()
    {
        // Two horizontally adjacent 16-bit samples differing only in their low byte. The old
        // RgbaColor path divided by 257, so both collapsed to the same value and the midpoint
        // became exactly high8 * 257. The format-native path must preserve a fractional low byte.
        using var image = Image.Create(2, 1, WorkingFormat.Rgba16);
        var wide = MemoryMarshal.Cast<byte, ushort>(image.WritablePixels);
        for (var c = 0; c < 4; c++)
        {
            wide[c] = (ushort)(0x1200 + c * 0x11 + 0x34);
            wide[4 + c] = (ushort)(0x1200 + c * 0x11 + 0x36);
        }

        // Destination pixel centre 1.5 maps back to source x = 1.5 -> fx = 1.0 -> tx = 0,
        // so use a half-pixel shift to get a genuine 0.5/0.5 blend.
        PixelWarp halfShift = (float x, float y, out float sx, out float sy) =>
        {
            sx = x - 0.25f;
            sy = y;
        };
        using var warped = image.Pipe().Warp(2, 1, halfShift, Resampler.Bilinear).ToImage();
        var result = MemoryMarshal.Cast<byte, ushort>(warped.WritablePixels);

        // The first output pixel is background; the second one blends the two neighbours.
        var blended = result[4];
        var oldBehaviour = (ushort)((wide[0] / 257) * 257);
        blended.ShouldNotBe(oldBehaviour);
        (blended > oldBehaviour).ShouldBeTrue("the 16-bit low byte must survive interpolation");
    }

    [Theory]
    [InlineData(WorkingFormat.Rgba8)]
    [InlineData(WorkingFormat.Rgba16)]
    [InlineData(WorkingFormat.Rgba32F)]
    public void Warp_OutsideTheSource_UsesTheBackgroundInTheWorkingFormat(WorkingFormat format)
    {
        const int size = 8;
        using var image = Image.Create(size, size, format);
        var background = new RgbaColor(10, 20, 30, 255);
        PixelWarp shifted = (float x, float y, out float sx, out float sy) =>
        {
            sx = x - 4f;
            sy = y - 4f;
        };

        using var warped = image.Pipe().Warp(size, size, shifted, Resampler.Bilinear, background).ToImage();
        var row = warped.WritablePixels;

        switch (format)
        {
            case WorkingFormat.Rgba8:
                row[0].ShouldBe((byte)10);
                row[1].ShouldBe((byte)20);
                row[2].ShouldBe((byte)30);
                row[3].ShouldBe((byte)255);
                break;
            case WorkingFormat.Rgba16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                samples[0].ShouldBe((ushort)(10 / 255f * 65535f + 0.5f));
                samples[3].ShouldBe((ushort)65535);
                break;
            }
            default:
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                samples[0].ShouldBe(10 / 255f, 1e-6f);
                samples[2].ShouldBe(30 / 255f, 1e-6f);
                samples[3].ShouldBe(1f);
                break;
            }
        }
    }

    [Fact]
    public void SampleRaw_KeepsEachFormatAtItsOwnPrecision()
    {
        Span<float> rgba = stackalloc float[4];

        using (var source = WorkingBuffer.Rent(2, 1, WorkingFormat.Rgba16))
        {
            var wide = MemoryMarshal.Cast<byte, ushort>(source.View.Span);
            wide[0] = 0x1234;
            wide[1] = 0x5678;
            wide[2] = 0x9ABC;
            wide[3] = 0xFFFF;
            Sampling.SampleRaw(source.View, 0.5f, 0.5f, rgba, Resampler.Bilinear);
            rgba[0].ShouldBe(0x1234 / 65535f, 1e-7f);
            rgba[3].ShouldBe(1f);
        }

        using (var source = WorkingBuffer.Rent(1, 1, WorkingFormat.Rgba32F))
        {
            var floats = MemoryMarshal.Cast<byte, float>(source.View.Span);
            floats[0] = 7.5f;
            floats[1] = -2f;
            floats[2] = 0.25f;
            floats[3] = 1f;
            Sampling.SampleRaw(source.View, 0.5f, 0.5f, rgba, Resampler.Bilinear);
            rgba[0].ShouldBe(7.5f);
            rgba[1].ShouldBe(-2f);
            rgba[2].ShouldBe(0.25f);
        }
    }

    [Fact]
    public void SampleRaw_RejectsUnsupportedContractsInsteadOfFallingBack()
    {
        using var source = WorkingBuffer.Rent(2, 2, WorkingFormat.Rgba8);
        var linear = new SampleOptions { Domain = SampleDomain.LinearLight };
        var lanczos = new SampleOptions();

        Should.Throw<NotSupportedException>(() =>
        {
            Span<float> rgba = stackalloc float[4];
            Sampling.SampleRaw(source.View, 1f, 1f, rgba, Resampler.Bilinear, linear);
        });
        Should.Throw<NotSupportedException>(() =>
        {
            Span<float> rgba = stackalloc float[4];
            Sampling.SampleRaw(source.View, 1f, 1f, rgba, Resampler.Lanczos3, lanczos);
        });
        Should.Throw<ArgumentException>(() =>
        {
            Span<float> tooShort = stackalloc float[3];
            Sampling.SampleRaw(source.View, 1f, 1f, tooShort);
        });
    }

    [Fact]
    public void Sample_EightBitConvenienceOverloadKeepsItsOldBehaviour()
    {
        // The 8-bit convenience API is deliberately unchanged: existing callers and inline
        // assertions in GeometryExtraTests still go through RgbaColor.
        using var source = WorkingBuffer.Rent(2, 1, WorkingFormat.Rgba8);
        source.View.Span[0] = 0;
        source.View.Span[4] = 255;

        var midpoint = Sampling.Sample(source.View, 1f, 0.5f, Resampler.Bilinear);
        midpoint.R.ShouldBeInRange((byte)127, (byte)129);
        Sampling.Sample(source.View, 0.5f, 0.5f, Resampler.Nearest).R.ShouldBe((byte)0);
    }

    [Fact]
    public void Warp_ThrowingMapping_DisposesTheRentedFrame()
    {
        var pool = new OneShotPool();
        using var image = Image.Create(4, 4, WorkingFormat.Rgba8);
        PixelWarp boom = (float _, float _, out float sx, out float sy) =>
            throw new InvalidOperationException("injected mapping failure");

        Should.Throw<InvalidOperationException>(() =>
            image.Pipe(pool).Warp(4, 4, boom, Resampler.Bilinear, default).ToImage());

        pool.DisposeCount.ShouldBe(1);
    }

    private static VectorCase Load(string caseId)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "vectors");
            if (Directory.Exists(candidate))
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(candidate, caseId + ".json")));
                var root = document.RootElement;
                var input = root.GetProperty("input");
                return new VectorCase(
                    input.GetProperty("width").GetInt32(),
                    input.GetProperty("height").GetInt32(),
                    Enum.Parse<WorkingFormat>(input.GetProperty("format").GetString()!),
                    Convert.FromHexString(input.GetProperty("bytes").GetString()!),
                    Convert.FromHexString(root.GetProperty("expected").GetProperty("bytes").GetString()!));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("tests/Fixtures/vectors not found");
    }

    private sealed class OneShotPool : IPixelPool
    {
        public int DisposeCount { get; private set; }

        public IMemoryOwner<byte> Rent(int length) => new Owner(new byte[length], this);

        private sealed class Owner(byte[] array, OneShotPool pool) : IMemoryOwner<byte>
        {
            public Memory<byte> Memory => array;
            public void Dispose() => pool.DisposeCount++;
        }
    }
}
