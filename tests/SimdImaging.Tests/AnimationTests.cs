using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Gif;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class AnimationTests
{
    private static byte[] SolidPixels(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static byte[] AnimatedGif()
    {
        var frames = new[]
        {
            new GifAnimationFrame(SolidPixels(4, 4, 220, 30, 30), TimeSpan.FromMilliseconds(100)),
            new GifAnimationFrame(SolidPixels(4, 4, 30, 30, 220), TimeSpan.FromMilliseconds(200)),
        };

        return GifEncoder.EncodeAnimation(frames, 4, 4, 4);
    }

    [Fact]
    public void Load_DecodesEveryFrameWithItsDuration()
    {
        using var image = Image.Load(AnimatedGif());

        image.FrameCount.ShouldBe(2);
        image.Frames[0].Duration.ShouldBe(TimeSpan.FromMilliseconds(100));
        image.Frames[1].Duration.ShouldBe(TimeSpan.FromMilliseconds(200));

        var first = image.Frames[0].Pixels.View.Span;
        first[0].ShouldBeGreaterThan((byte)180);   // red frame
        first[2].ShouldBeLessThan((byte)80);

        var second = image.Frames[1].Pixels.View.Span;
        second[2].ShouldBeGreaterThan((byte)180);  // blue frame
        second[0].ShouldBeLessThan((byte)80);
    }

    [Fact]
    public void KeepFrames_SelectsAndReorders()
    {
        using var image = Image.Load(AnimatedGif());
        using var reordered = image.KeepFrames(1, 0);

        reordered.FrameCount.ShouldBe(2);
        reordered.Frames[0].Duration.ShouldBe(TimeSpan.FromMilliseconds(200));
        reordered.Frames[1].Duration.ShouldBe(TimeSpan.FromMilliseconds(100));
        reordered.Frames[0].Pixels.View.Span[2].ShouldBeGreaterThan((byte)180);
    }

    [Fact]
    public void KeepFrames_RejectsBadIndices()
    {
        using var image = Image.Load(AnimatedGif());
        Should.Throw<ArgumentException>(() => image.KeepFrames());
        Should.Throw<ArgumentOutOfRangeException>(() => image.KeepFrames(2));
    }

    [Fact]
    public void Save_RejectsMultiFrameInsteadOfDroppingFrames()
    {
        using var image = Image.Load(AnimatedGif());
        var path = Path.Combine(Path.GetTempPath(), $"simd-anim-{Guid.NewGuid():N}.gif");
        try
        {
            Should.Throw<NotSupportedException>(() => Image.Save(image, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComposeAnimation_AccumulatesEachFrame()
    {
        using var first = Image.Create(4, 4, WorkingFormat.Rgba8);
        first.FillRect(new PixelRect(0, 0, 2, 2), new RgbaColor(255, 0, 0, 255));

        using var second = Image.Create(4, 4, WorkingFormat.Rgba8);
        second.FillRect(new PixelRect(2, 2, 2, 2), new RgbaColor(0, 0, 255, 255));

        using var animation = Image.ComposeAnimation(4, 4, new[] { first, second }, TimeSpan.FromMilliseconds(50));

        animation.FrameCount.ShouldBe(2);
        animation.Frames[1].Duration.ShouldBe(TimeSpan.FromMilliseconds(50));

        var composed = animation.Frames[1].Pixels.View.Span;
        composed[0].ShouldBe((byte)255);            // the red square survives into frame 2
        var blue = (2 * 4 + 2) * 4;
        composed[blue + 2].ShouldBe((byte)255);
    }

    [Fact]
    public void ComposeAnimation_RejectsOversizedFrames()
    {
        using var big = Image.Create(8, 8, WorkingFormat.Rgba8);
        Should.Throw<ArgumentException>(() => Image.ComposeAnimation(4, 4, new[] { big }));
    }

    [Fact]
    public void Thumbnail_FitsTheBoxWithoutUpscaling()
    {
        using var source = Image.Create(100, 50, WorkingFormat.Rgba8);
        source.Fill(RgbaColor.White);

        using var thumbnail = source.Thumbnail(20, 20);

        thumbnail.Width.ShouldBe(20);
        thumbnail.Height.ShouldBe(10);

        using var small = Image.Create(10, 10, WorkingFormat.Rgba8);
        using var untouched = small.Thumbnail(64, 64);
        untouched.Width.ShouldBe(10);
        untouched.Height.ShouldBe(10);
    }

    [Fact]
    public void TryLoadEmbeddedThumbnail_ReturnsFalseWhenTheFileHasNone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"simd-thumb-{Guid.NewGuid():N}.png");
        try
        {
            using (var source = Image.Create(8, 8, WorkingFormat.Rgba8))
            {
                source.Fill(RgbaColor.White);
                Image.Save(source, path);
            }

            Image.TryLoadEmbeddedThumbnail(path, out var thumbnail).ShouldBeFalse();
            thumbnail.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
