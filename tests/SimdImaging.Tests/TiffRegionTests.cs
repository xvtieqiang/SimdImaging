using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;

namespace SimdImaging.Tests;

/// <summary>
/// <c>TiffRegionSource</c> and the facade over it: segment assembly, conversion and the refusal list.
/// <para>The oracle everywhere is <c>Image.Load</c> of the same bytes — the region read must produce the same
/// pixels, not merely plausible ones.</para>
/// </summary>
public class TiffRegionTests
{
    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), $"simd-region-{Guid.NewGuid():N}{extension}");

    private static byte[] Pattern(int width, int height, int channels)
    {
        var bytes = new byte[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < channels; c++)
                    bytes[((y * width) + x) * channels + c] = (byte)((x * 7) + (y * 31) + (c * 53));
            }
        }

        return bytes;
    }

    private static string Write(byte[] tiff)
    {
        var path = Temp(".tif");
        File.WriteAllBytes(path, tiff);
        return path;
    }

    /// <summary>Reads <paramref name="rect"/> through the region source and compares it against the whole-image
    /// decode of the same file, converting to <paramref name="format"/> through <c>Load</c> when asked.</summary>
    private static void ShouldMatchLoad(string path, PixelRect rect, WorkingFormat? format = null)
    {
        using var whole = Image.Load(path, format is { } f ? new LoadOptions { Format = f } : null);
        var working = format ?? whole.Format;
        var bytesPerPixel = working.BytesPerPixel();

        using var source = TiffRegionSource.Open(path, 0, format, 1L << 26);
        using var destination = WorkingBuffer.Rent(rect.Width, rect.Height, working);
        source.ReadRegion(rect, destination.View);

        var view = whole[0].Pixels.View;
        for (var y = 0; y < rect.Height; y++)
        {
            view.Row(rect.Y + y).Slice(rect.X * bytesPerPixel, rect.Width * bytesPerPixel)
                .SequenceEqual(destination.View.Row(y))
                .ShouldBeTrue($"{Path.GetFileName(path)} row {y} of {rect} differs from Load");
        }
    }

    [Fact]
    public void Rgb8_Strips_MatchLoad()
    {
        var path = Write(TiffFixture.Strips(64, 48, PixelFormat.Rgb24, Pattern(64, 48, 3)));
        try
        {
            ShouldMatchLoad(path, new PixelRect(0, 0, 64, 48));
            ShouldMatchLoad(path, new PixelRect(13, 11, 20, 17));
            ShouldMatchLoad(path, new PixelRect(60, 44, 4, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Gray8_Strips_MatchLoad()
    {
        var path = Write(TiffFixture.Strips(37, 29, PixelFormat.Gray8, Pattern(37, 29, 1)));
        try
        {
            ShouldMatchLoad(path, new PixelRect(5, 7, 11, 9));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rgb16_MatchesLoad_AndNarrowsToRgba8()
    {
        var path = Write(TiffFixture.Strips(32, 24, PixelFormat.Rgb48, Pattern(32, 24, 6)));
        try
        {
            ShouldMatchLoad(path, new PixelRect(3, 4, 19, 13));
            ShouldMatchLoad(path, new PixelRect(3, 4, 19, 13), WorkingFormat.Rgba8);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Gray16_MatchesLoad()
    {
        var path = Write(TiffFixture.Strips(20, 18, PixelFormat.Gray16, Pattern(20, 18, 2)));
        try
        {
            ShouldMatchLoad(path, new PixelRect(1, 2, 17, 13));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tiled_AcrossTileBoundaries_MatchesLoad()
    {
        var path = Write(TiffFixture.Build(64, 48, 3, 8, photometric: 2, Pattern(64, 48, 3),
            tileWidth: 16, tileHeight: 16));
        try
        {
            using var probe = Image.Load(path);
            probe.Width.ShouldBe(64);

            // Crosses tile columns 0..2 and rows 0..2, including the clipped edge tiles.
            ShouldMatchLoad(path, new PixelRect(13, 11, 20, 10));
            ShouldMatchLoad(path, new PixelRect(31, 15, 2, 2));
            ShouldMatchLoad(path, new PixelRect(48, 32, 16, 16));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MinIsWhite_MatchesLoad()
    {
        var path = Write(TiffFixture.Build(32, 24, 1, 8, photometric: 0, Pattern(32, 24, 1)));
        try
        {
            ShouldMatchLoad(path, new PixelRect(4, 3, 17, 11));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FourChannels_IsRefused_WithTheReason()
    {
        var path = Write(TiffFixture.Strips(16, 12, PixelFormat.Rgba32, Pattern(16, 12, 4)));
        try
        {
            var error = Should.Throw<NotSupportedException>(() => TiffRegionSource.Open(path, 0, null, 1L << 26));
            error.Message.ShouldContain("ExtraSamples");
            error.Message.ShouldContain("4");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FloatSamples_AreRefused()
    {
        var path = Write(TiffFixture.Build(8, 8, 3, 32, photometric: 2, new byte[8 * 8 * 3 * 4], sampleFormat: 3));
        try
        {
            var error = Should.Throw<NotSupportedException>(() => TiffRegionSource.Open(path, 0, null, 1L << 26));
            error.Message.ShouldContain("IeeeFloat");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnsupportedPhotometrics_AreRefused()
    {
        // YCbCr with three samples: the sample-count check must not be the one that fires.
        var path = Write(TiffFixture.Build(8, 8, 3, 8, photometric: 6, Pattern(8, 8, 3)));
        try
        {
            var error = Should.Throw<NotSupportedException>(() => TiffRegionSource.Open(path, 0, null, 1L << 26));
            error.Message.ShouldContain("YCbCr");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASingleOversizedSegment_IsRefusedByName()
    {
        var path = Write(TiffFixture.Strips(64, 48, PixelFormat.Rgb24, Pattern(64, 48, 3), rowsPerStrip: 48));
        try
        {
            var error = Should.Throw<NotSupportedException>(() =>
                ImageTranscode.From(path)
                    .Region(new PixelRect(0, 0, 64, 48))
                    .WithOptions(new TranscodeOptions { Region = new RegionOptions { MaxSegmentBytes = 1024 } })
                    .ToImage());
            error.Message.ShouldContain("RowsPerStrip");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NonTiff_IsRefused()
    {
        var path = Temp(".png");
        try
        {
            using (var image = TestImages.Gradient(8, 8, WorkingFormat.Rgba8))
                Image.Save(image, path);

            Should.Throw<NotSupportedException>(() => Image.DescribeRegion(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PageIndexPastTheLastPage_Throws()
    {
        var path = Write(TiffFixture.Strips(16, 12, PixelFormat.Rgb24, Pattern(16, 12, 3)));
        try
        {
            Should.Throw<ArgumentOutOfRangeException>(() => Image.DescribeRegion(path, pageIndex: 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DescribeRegion_ReportsTheLayout()
    {
        var path = Write(TiffFixture.Build(64, 48, 3, 8, photometric: 2, Pattern(64, 48, 3),
            tileWidth: 16, tileHeight: 16, orientation: 6));
        try
        {
            var capabilities = Image.DescribeRegion(path);
            capabilities.ImageWidth.ShouldBe(64);
            capabilities.ImageHeight.ShouldBe(48);
            capabilities.IsTiled.ShouldBeTrue();
            capabilities.SegmentsAcross.ShouldBe(4);
            capabilities.SegmentsDown.ShouldBe(3);
            capabilities.Format.ShouldBe(WorkingFormat.Rgba8);
            capabilities.BitDepth.ShouldBe(8);
            capabilities.Orientation.ShouldBe(ImageOrientation.Rotate90);
            capabilities.MaxSegmentBytes.ShouldBe(16 * 16 * 3);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
