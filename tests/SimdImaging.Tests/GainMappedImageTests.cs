using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// The sync contract: a paired operation must produce exactly the bytes that transforming each rendition
/// on its own produces with the parameters derived from the base. Every test derives those parameters
/// itself instead of calling into the type, so a wrong derivation in production cannot hide.
/// </summary>
public class GainMappedImageTests
{
    private static Image Pattern(int width, int height, int seed)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)((x * 13 + y * 7 + seed * 31) & 255);
                pixels[offset + 1] = (byte)((y * 29 + x * 3 + seed * 17) & 255);
                pixels[offset + 2] = (byte)((x * 5 + y * 11 + seed * 7) & 255);
                pixels[offset + 3] = 255;
            }
        }

        return image;
    }

    private static GainMappedImage Pair(int baseWidth, int baseHeight, int gainWidth, int gainHeight) =>
        new(Pattern(baseWidth, baseHeight, 1), Pattern(gainWidth, gainHeight, 2), GainMapParameters.Default);

    private static byte[] Bytes(Image image) => image.WritablePixels.ToArray();

    private static void SameBytes(Image actual, Image expected, string what) =>
        Bytes(actual).ShouldBe(Bytes(expected), what);

    // The derivation rules, written out again: floor at the origin, ceil at the far edge.
    private static PixelRect DeriveCrop(PixelRect region, int width, int height, int gainWidth, int gainHeight)
    {
        var x0 = (int)((long)region.X * gainWidth / width);
        var x1 = (int)(((long)(region.X + region.Width) * gainWidth + width - 1) / width);
        var y0 = (int)((long)region.Y * gainHeight / height);
        var y1 = (int)(((long)(region.Y + region.Height) * gainHeight + height - 1) / height);

        x0 = Math.Clamp(x0, 0, gainWidth - 1);
        x1 = Math.Clamp(x1, x0 + 1, gainWidth);
        y0 = Math.Clamp(y0, 0, gainHeight - 1);
        y1 = Math.Clamp(y1, y0 + 1, gainHeight);

        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    private static int Scaled(int value, int source, int target) =>
        Math.Max(1, (int)Math.Round(value * ((double)target / source)));

    private static AffineMatrix Conjugate(AffineMatrix m, int baseWidth, int baseHeight, int gainWidth,
        int gainHeight, int width, int height)
    {
        var sx = (double)gainWidth / baseWidth;
        var sy = (double)gainHeight / baseHeight;
        var tx = (double)Scaled(width, baseWidth, gainWidth) / width;
        var ty = (double)Scaled(height, baseHeight, gainHeight) / height;

        return new AffineMatrix(
            (float)(m.A * sx / tx), (float)(m.B * sy / tx), (float)(m.C / tx),
            (float)(m.D * sx / ty), (float)(m.E * sy / ty), (float)(m.F / ty));
    }

    private static float[] Conjugate(float[] h, int baseWidth, int baseHeight, int gainWidth, int gainHeight,
        int width, int height)
    {
        var sx = (float)((double)gainWidth / baseWidth);
        var sy = (float)((double)gainHeight / baseHeight);
        var tx = (float)((double)Scaled(width, baseWidth, gainWidth) / width);
        var ty = (float)((double)Scaled(height, baseHeight, gainHeight) / height);

        return
        [
            h[0] * sx / tx, h[1] * sy / tx, h[2] / tx,
            h[3] * sx / ty, h[4] * sy / ty, h[5] / ty,
            h[6] * sx, h[7] * sy,
        ];
    }

    [Theory]
    [InlineData(97, 61, 25, 16, 0, 0, 97, 61)]
    [InlineData(97, 61, 25, 16, 1, 1, 95, 59)]
    [InlineData(97, 61, 25, 16, 13, 7, 40, 20)]
    [InlineData(96, 64, 24, 16, 8, 16, 32, 32)]
    [InlineData(25, 16, 97, 61, 3, 2, 20, 12)]
    [InlineData(97, 61, 1, 1, 10, 10, 30, 30)]
    public void Crop_MatchesIndependentCrops(int baseWidth, int baseHeight, int gainWidth, int gainHeight,
        int x, int y, int width, int height)
    {
        using var pair = Pair(baseWidth, baseHeight, gainWidth, gainHeight);
        var region = new PixelRect(x, y, width, height);

        using var cropped = pair.Crop(region);
        using var expectedBase = pair.Base.Pipe().Crop(region).ToImage();
        using var expectedGain = pair.GainMap.Pipe().Crop(
            DeriveCrop(region, baseWidth, baseHeight, gainWidth, gainHeight)).ToImage();

        SameBytes(cropped.Base, expectedBase, "base crop");
        SameBytes(cropped.GainMap, expectedGain, "gain crop");
    }

    [Fact]
    public void Crop_RoundsTheGainRectangleOutwardRatherThanToNearest()
    {
        // Ten pixels in, ten wide, on a 100-wide base with a 7-wide gain map: the far edge is at
        // 20·7/100 = 1.4 grid units. Nearest-rounding would keep one texel; the covering rectangle needs
        // two, or the last tenth of the region has no gain sample behind it.
        using var pair = Pair(100, 100, 7, 7);

        using var cropped = pair.Crop(new PixelRect(10, 10, 10, 10));

        cropped.GainMap.Width.ShouldBe(2);
        cropped.GainMap.Height.ShouldBe(2);
    }

    [Fact]
    public void Crop_AgreesWithTheDerivationForEveryRectangleOfASmallImage()
    {
        const int baseWidth = 13, baseHeight = 11, gainWidth = 5, gainHeight = 4;
        using var pair = Pair(baseWidth, baseHeight, gainWidth, gainHeight);

        for (var y = 0; y < baseHeight; y++)
            for (var height = 1; y + height <= baseHeight; height++)
                for (var x = 0; x < baseWidth; x++)
                    for (var width = 1; x + width <= baseWidth; width++)
                    {
                        var region = new PixelRect(x, y, width, height);
                        using var cropped = pair.Crop(region);
                        using var expected = pair.GainMap.Pipe().Crop(
                            DeriveCrop(region, baseWidth, baseHeight, gainWidth, gainHeight)).ToImage();

                        Bytes(cropped.GainMap).ShouldBe(Bytes(expected), $"gain crop of {region}");
                    }
    }

    [Fact]
    public void FlipAndRotate_MatchTheIndependentOperations()
    {
        using var pair = Pair(97, 61, 25, 16);

        using (var flipped = pair.FlipHorizontal())
        {
            using var expected = pair.Base.Pipe().FlipHorizontal().ToImage();
            SameBytes(flipped.Base, expected, "base flip");
        }

        using (var flipped = pair.FlipVertical())
        {
            using var expected = pair.GainMap.Pipe().FlipVertical().ToImage();
            SameBytes(flipped.GainMap, expected, "gain flip");
        }

        using (var rotated = pair.Rotate90())
        {
            using var expected = pair.GainMap.Pipe().Rotate90(true).ToImage();
            SameBytes(rotated.GainMap, expected, "gain rotate90");
        }

        using (var rotated = pair.Rotate180())
        {
            using var expected = pair.Base.Pipe().Rotate180().ToImage();
            SameBytes(rotated.Base, expected, "base rotate180");
        }
    }

    [Fact]
    public void FlipAndRotate_AreReversible()
    {
        using var pair = Pair(97, 61, 25, 16);
        var baseBytes = Bytes(pair.Base);
        var gainBytes = Bytes(pair.GainMap);

        using var roundTrip = pair.FlipHorizontal().Rotate90().Rotate90(false).FlipHorizontal();

        Bytes(roundTrip.Base).ShouldBe(baseBytes);
        Bytes(roundTrip.GainMap).ShouldBe(gainBytes);
    }

    [Theory]
    [InlineData(97, 61, 25, 16, 40, 30)]
    [InlineData(96, 64, 24, 16, 200, 128)]
    [InlineData(97, 61, 25, 16, 12, 9)]
    public void Resize_MatchesIndependentResizes(int baseWidth, int baseHeight, int gainWidth, int gainHeight,
        int width, int height)
    {
        using var pair = Pair(baseWidth, baseHeight, gainWidth, gainHeight);

        using var resized = pair.Resize(width, height, Resampler.Bicubic);
        using var expectedBase = pair.Base.Pipe().Resize(width, height, Resampler.Bicubic).ToImage();
        using var expectedGain = pair.GainMap.Pipe().Resize(Scaled(width, baseWidth, gainWidth),
            Scaled(height, baseHeight, gainHeight), Resampler.Bicubic).ToImage();

        SameBytes(resized.Base, expectedBase, "base resize");
        SameBytes(resized.GainMap, expectedGain, "gain resize");
    }

    [Fact]
    public void Affine_MatchesIndependentAffine()
    {
        using var pair = Pair(97, 61, 25, 16);
        var forward = new AffineMatrix(1.1f, 0.2f, 3f, -0.1f, 0.9f, -2f);

        using var result = pair.Affine(forward, 60, 40, new RgbaColor(10, 20, 30, 255), new RgbaColor(0, 0, 0, 255));
        using var expectedBase = pair.Base.Pipe().Affine(forward, 60, 40, new RgbaColor(10, 20, 30, 255)).ToImage();
        using var expectedGain = pair.GainMap.Pipe().Affine(
            Conjugate(forward, 97, 61, 25, 16, 60, 40),
            Scaled(60, 97, 25), Scaled(40, 61, 16), new RgbaColor(0, 0, 0, 255)).ToImage();

        SameBytes(result.Base, expectedBase, "base affine");
        SameBytes(result.GainMap, expectedGain, "gain affine");
    }

    [Fact]
    public void Affine_ConjugationMeansTheSameMappingInBothPixelGrids()
    {
        // The one test that pins the direction: apply the base transform through the scale, and the
        // gain transform directly, and the points must land in the same normalised place.
        const int baseWidth = 97, baseHeight = 61, gainWidth = 25, gainHeight = 16;
        const int width = 60, height = 40;

        var forward = new AffineMatrix(1.1f, 0.2f, 3f, -0.1f, 0.9f, -2f);
        var gainForward = Conjugate(forward, baseWidth, baseHeight, gainWidth, gainHeight, width, height);

        var sx = (double)gainWidth / baseWidth;
        var sy = (double)gainHeight / baseHeight;
        var tx = (double)Scaled(width, baseWidth, gainWidth) / width;
        var ty = (double)Scaled(height, baseHeight, gainHeight) / height;

        foreach (var (px, py) in new[] { (0f, 0f), (12.5f, 7.25f), (24f, 15f) })
        {
            var (baseX, baseY) = forward.Apply(px * (float)sx, py * (float)sy);
            var expectedX = baseX / tx;
            var expectedY = baseY / ty;

            var (actualX, actualY) = gainForward.Apply(px, py);

            ((double)actualX).ShouldBe(expectedX, 1e-3);
            ((double)actualY).ShouldBe(expectedY, 1e-3);
        }
    }

    [Fact]
    public void Rotate_MatchesIndependentRotation()
    {
        using var pair = Pair(97, 61, 25, 16);

        using var rotated = pair.Rotate(23f, new RgbaColor(5, 6, 7, 255), new RgbaColor(0, 0, 0, 255));
        using var expectedBase = pair.Base.Pipe().Rotate(23f, new RgbaColor(5, 6, 7, 255)).ToImage();

        SameBytes(rotated.Base, expectedBase, "base rotate");

        // The gain canvas comes from the base canvas, not from re-running the ceiling on its own size.
        rotated.GainMap.Width.ShouldBe(Scaled(expectedBase.Width, 97, 25));
        rotated.GainMap.Height.ShouldBe(Scaled(expectedBase.Height, 61, 16));
    }

    [Fact]
    public void Perspective_MatchesIndependentPerspective()
    {
        using var pair = Pair(97, 61, 25, 16);
        var homography = new[] { 1.05f, 0.12f, -3f, -0.08f, 0.98f, 2f, 0.0008f, 0.0004f };

        using var result = pair.Perspective(homography, 70, 50, new RgbaColor(9, 9, 9, 255));
        using var expectedBase = pair.Base.Pipe().Perspective(homography, 70, 50, new RgbaColor(9, 9, 9, 255)).ToImage();
        using var expectedGain = pair.GainMap.Pipe().Perspective(
            Conjugate(homography, 97, 61, 25, 16, 70, 50),
            Scaled(70, 97, 25), Scaled(50, 61, 16)).ToImage();

        SameBytes(result.Base, expectedBase, "base perspective");
        SameBytes(result.GainMap, expectedGain, "gain perspective");
    }

    [Fact]
    public void WarpNormalized_AppliesTheSameNormalisedMappingToBoth()
    {
        using var pair = Pair(97, 61, 25, 16);

        static void Map(float u, float v, out float sourceU, out float sourceV)
        {
            sourceU = u * 0.9f + 0.03f;
            sourceV = v * 0.92f + 0.02f;
        }

        using var result = pair.WarpNormalized(70, 50, Map);

        // The same normalised mapping, expressed per rendition the way a caller would have to.
        static PixelWarp Scale(NormalizedWarp map, int width, int height) =>
            (float x, float y, out float sourceX, out float sourceY) =>
            {
                map(x / width, y / height, out var u, out var v);
                sourceX = u * width;
                sourceY = v * height;
            };

        using var expectedBase = pair.Base.Pipe().Warp(70, 50, Scale(Map, 97, 61)).ToImage();
        using var expectedGain = pair.GainMap.Pipe().Warp(Scaled(70, 97, 25), Scaled(50, 61, 16), Scale(Map, 25, 16)).ToImage();

        SameBytes(result.Base, expectedBase, "base warp");
        SameBytes(result.GainMap, expectedGain, "gain warp");
    }

    [Fact]
    public void ThePairDoesNotOfferOperationsWhoseAnswerTwoRenditionsWouldDisagreeOn()
    {
        // Content-determined and per-image-canvas operations are absent by design, not forgotten. If one
        // is added later, this test is where the decision has to be re-made.
        var forbidden = new[] { "Pad", "Trim", "EntropyCrop", "SeamCarve", "RemoveSeams", "InsertSeams",
            "ResizeCanvas", "Tile", "RoundCorners", "AutoOrient", "ResizeToWidth", "ResizeToHeight" };

        foreach (var name in forbidden)
        {
            typeof(GainMappedImage).GetMember(name).ShouldBeEmpty(
                $"{name} would have to decide per rendition and has no faithful pairing");
        }
    }

    [Fact]
    public void ToHdr_ReconstructsAboveOneAndKeepsTheSize()
    {
        using var pair = new GainMappedImage(
            Pattern(32, 24, 1),
            Pattern(8, 6, 2),
            new GainMapParameters(0, 2, 1, 1.0 / 64, 1.0 / 64, 0, 1, false));

        using var hdr = pair.ToHdr(4f);

        hdr.Format.ShouldBe(WorkingFormat.Rgba32F);
        hdr.Width.ShouldBe(32);
        hdr.Height.ShouldBe(24);
    }

    [Fact]
    public void Dispose_IsIdempotentAndReleasesBothRenditions()
    {
        var pair = Pair(16, 12, 4, 3);
        var baseImage = pair.Base;
        var gainImage = pair.GainMap;

        pair.Dispose();
        pair.Dispose();

        Should.Throw<ObjectDisposedException>(() => baseImage.WritablePixels.ToArray());
        Should.Throw<ObjectDisposedException>(() => gainImage.WritablePixels.ToArray());
        Should.Throw<ObjectDisposedException>(() => pair.Crop(new PixelRect(0, 0, 4, 4)));
    }

    [Fact]
    public void TheConstructorRejectsMetadataThatCannotDescribeAReconstruction()
    {
        using var baseImage = Pattern(4, 4, 1);
        using var gain = Pattern(2, 2, 2);

        Should.Throw<ArgumentException>(() => new GainMappedImage(baseImage, gain, new GainMapParameters(1, 0, 1, 0, 0, 0, 1, false)));
        Should.Throw<ArgumentOutOfRangeException>(() => new GainMappedImage(baseImage, gain, new GainMapParameters(0, 1, 0, 0, 0, 0, 1, false)));
    }
}
