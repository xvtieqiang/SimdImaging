using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Tests;

public class MetadataOpsTests
{
    private static Image Indexed(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                span[o] = (byte)(y * 16 + x);
                span[o + 1] = (byte)(y * 16 + x);
                span[o + 2] = (byte)(y * 16 + x);
                span[o + 3] = 255;
            }
        }

        return image;
    }

    private static byte Red(Image image, int x, int y) => image.WritablePixels[(y * image.Width + x) * 4];

    /// <summary>A minimal little-endian TIFF block with one IFD entry: Orientation = <paramref name="value"/>.</summary>
    private static byte[] ExifWithOrientation(short value)
    {
        var block = new byte[26];
        block[0] = 0x49;   // 'I'
        block[1] = 0x49;   // 'I'
        block[2] = 0x2A;
        block[3] = 0x00;
        block[4] = 0x08;   // IFD0 at offset 8

        block[8] = 0x01;   // one entry
        block[10] = 0x12;  // tag 0x0112 = Orientation
        block[11] = 0x01;
        block[12] = 0x03;  // SHORT
        block[14] = 0x01;  // count 1
        block[18] = (byte)value;
        // next IFD offset stays zero
        return block;
    }

    [Fact]
    public void AutoOrient_AppliesTheTagAndResetsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"simd-orient-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var source = Indexed(3, 2))
            using (var stamped = source.WithMetadata(new ImageMetadata { Exif = ExifWithOrientation(6) }))
            {
                Image.Save(stamped, path);
            }

            using var loaded = Image.Load(path);
            loaded.Info.Orientation.ShouldBe(ImageOrientation.Rotate90);

            using var upright = loaded.AutoOrient();

            upright.Info.Orientation.ShouldBe(ImageOrientation.Normal);
            upright.Width.ShouldBe(2);
            upright.Height.ShouldBe(3);
            // 90° clockwise: destination (0, 0) reads source (0, height - 1).
            Math.Abs(Red(upright, 0, 0) - Red(loaded, 0, 1)).ShouldBeLessThanOrEqualTo(8);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ApplyOrientation_TurnsThePixelsDuringTheDecode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"simd-load-orient-{Guid.NewGuid():N}.jpg");
        var resaved = Path.Combine(Path.GetTempPath(), $"simd-load-orient-2-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var source = Indexed(3, 2))
            using (var stamped = source.WithMetadata(new ImageMetadata { Exif = ExifWithOrientation(6) }))
            {
                Image.Save(stamped, path);
            }

            var bytes = File.ReadAllBytes(path);

            using var stored = Image.Load(bytes);
            stored.Info.Orientation.ShouldBe(ImageOrientation.Rotate90);
            stored.Width.ShouldBe(3);
            stored.Height.ShouldBe(2);

            using var shown = Image.Load(bytes, new LoadOptions { ApplyOrientation = true });
            using var viaOperator = stored.AutoOrient();

            shown.Info.Orientation.ShouldBe(ImageOrientation.Normal);
            shown.Width.ShouldBe(2);
            shown.Height.ShouldBe(3);
            // Applying the tag inside the decode and turning afterwards are the same bytes: the pushdown is lossless.
            shown.WritablePixels.ToArray().ShouldBe(viaOperator.WritablePixels.ToArray());

            // And the tag is gone from the metadata, so a save does not turn the picture a second time.
            Image.Save(shown, resaved);
            using var reloaded = Image.Load(resaved);
            reloaded.Info.Orientation.ShouldBe(ImageOrientation.Normal);
            reloaded.Width.ShouldBe(2);
            reloaded.Height.ShouldBe(3);
        }
        finally
        {
            File.Delete(path);
            File.Delete(resaved);
        }
    }

    [Fact]
    public void AutoOrient_LeavesANormalImageAlone()
    {
        using var source = Indexed(4, 3);
        using var oriented = source.AutoOrient();

        oriented.Info.Orientation.ShouldBe(ImageOrientation.Normal);
        oriented.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void WithMetadata_ReplacesTheBlocksAndKeepsThePixels()
    {
        using var source = Indexed(4, 4);
        var metadata = new ImageMetadata { Exif = ExifWithOrientation(1) };

        using var stamped = source.WithMetadata(metadata);

        stamped.Metadata.Exif.Length.ShouldBe(metadata.Exif.Length);
        stamped.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
        source.Metadata.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void WithMetadata_EmptyStripsEverything()
    {
        using var source = Indexed(2, 2);
        using var stamped = source.WithMetadata(new ImageMetadata { Xmp = new byte[] { 1, 2, 3 } });

        using var stripped = stamped.WithMetadata(ImageMetadata.Empty);

        stripped.Metadata.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void CopyMetadataFrom_MovesTheBlocks()
    {
        using var pixels = Indexed(3, 3);
        using var plain = Indexed(3, 3);
        using var other = plain.WithMetadata(new ImageMetadata { Exif = ExifWithOrientation(1) });

        using var result = pixels.CopyMetadataFrom(other);

        result.Metadata.Exif.Length.ShouldBe(other.Metadata.Exif.Length);
        result.WritablePixels.ToArray().ShouldBe(pixels.WritablePixels.ToArray());
    }

    [Fact]
    public void Transcode_WritesTheFileAndCanStripMetadata()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"simd-src-{Guid.NewGuid():N}.png");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"simd-dst-{Guid.NewGuid():N}.png");
        try
        {
            using (var source = Indexed(5, 4))
            using (var stamped = source.WithMetadata(new ImageMetadata { Exif = ExifWithOrientation(1) }))
            {
                Image.Save(stamped, sourcePath);
            }

            using (var original = Image.Load(sourcePath))
                original.Metadata.IsEmpty.ShouldBeFalse();

            Image.Transcode(sourcePath, destinationPath, ImageMetadata.Empty);

            using var reloaded = Image.Load(destinationPath);
            reloaded.Width.ShouldBe(5);
            reloaded.Height.ShouldBe(4);
            reloaded.Metadata.IsEmpty.ShouldBeTrue();
            Red(reloaded, 4, 3).ShouldBe((byte)(3 * 16 + 4));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public void ResizeToWidth_KeepsTheAspectRatio()
    {
        using var source = Indexed(4, 2);
        using var resized = source.ResizeToWidth(16, SimdImaging.Processing.Resampler.Nearest);

        resized.Width.ShouldBe(16);
        resized.Height.ShouldBe(8);
    }

    [Fact]
    public void ResizeToHeight_KeepsTheAspectRatio()
    {
        using var source = Indexed(4, 2);
        using var resized = source.ResizeToHeight(10, SimdImaging.Processing.Resampler.Nearest);

        resized.Width.ShouldBe(20);
        resized.Height.ShouldBe(10);
    }

    [Fact]
    public void ResizeByAxis_RejectsIllegalSizes()
    {
        using var source = Indexed(4, 2);
        Should.Throw<ArgumentOutOfRangeException>(() => source.ResizeToWidth(0));
        Should.Throw<ArgumentOutOfRangeException>(() => source.ResizeToHeight(-1));
    }

    [Fact]
    public void Lightness_ScalesTheColourChannels()
    {
        using var source = Indexed(2, 1);

        using var darker = source.Lightness(0.5f);
        using var brighter = source.Lightness(2f);

        ((int)Red(darker, 1, 0)).ShouldBeLessThanOrEqualTo(1);   // 1 * 0.5 rounds back to one
        Red(brighter, 1, 0).ShouldBe((byte)2);
        darker.WritablePixels[3].ShouldBe((byte)255);  // alpha is untouched
    }

    [Fact]
    public void Lightness_RejectsNegativeFactors()
    {
        using var source = Indexed(2, 2);
        Should.Throw<ArgumentOutOfRangeException>(() => source.Lightness(-0.5f));
    }
}
