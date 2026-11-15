using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// <see cref="Image.Mutate"/> is the only entry that writes to the image it is called on. Every other operator
/// returns a new image, so this file pins the two things that make the difference safe: the result really lands
/// in the original instance, and a failure leaves the original untouched.
/// </summary>
public class ImageMutateTests
{
    [Fact]
    public void Mutate_AppliesTheChainToTheSameInstance()
    {
        using var image = Pattern(64, 48);
        var identity = image;

        var returned = image.Mutate(x => x.Resize(32, 24).Crop(new Sdcb.SimdCodecs.Abstractions.PixelRect(4, 2, 8, 6)));

        returned.ShouldBeSameAs(identity);
        image.Width.ShouldBe(8);
        image.Height.ShouldBe(6);
    }

    [Fact]
    public void Mutate_ReplacesThePixels()
    {
        using var image = Pattern(8, 8);
        var before = image.WritablePixels.ToArray();

        image.Mutate(x => x.Rotate180());

        image.WritablePixels.ToArray().SequenceEqual(before).ShouldBeFalse();
        image.Width.ShouldBe(8);
    }

    [Fact]
    public void Mutate_LeavesOnlyTheNewFrameOutstanding()
    {
        var pool = new CountingPool();
        var image = Pattern(32, 32);

        image.Mutate(x => x.Resize(16, 16), pool);

        // The chain may rent intermediates, but once the swap is done exactly one buffer is still held: the new
        // frame. Every intermediate was released as the next step consumed it, and the old frame went back to
        // the pool it came from (not this one).
        pool.Rented.ShouldBeGreaterThan(0);
        pool.Rented.ShouldBe(pool.Returned + 1);

        image.Dispose();
        pool.Rented.ShouldBe(pool.Returned);
    }

    [Fact]
    public void Mutate_WhenTheChainThrows_LeavesTheImageUntouched()
    {
        using var image = Pattern(16, 16);
        var before = image.WritablePixels.ToArray();

        Should.Throw<ArgumentException>(() =>
            image.Mutate(x => x.Crop(new Sdcb.SimdCodecs.Abstractions.PixelRect(10, 10, 20, 20))));

        image.Width.ShouldBe(16);
        image.Height.ShouldBe(16);
        image.WritablePixels.ToArray().ShouldBe(before);
    }

    [Fact]
    public void Mutate_WithNoSteps_IsStillAValidImage()
    {
        using var image = Pattern(12, 9);
        var before = image.WritablePixels.ToArray();

        image.Mutate(_ => { });

        image.Width.ShouldBe(12);
        image.Height.ShouldBe(9);
        image.WritablePixels.ToArray().ShouldBe(before);
    }

    [Fact]
    public void Mutate_AfterDispose_Throws()
    {
        var image = Pattern(4, 4);
        image.Dispose();

        Should.Throw<ObjectDisposedException>(() => image.Mutate(x => x.Rotate90()));
    }

    [Fact]
    public void Mutate_RejectsANullChain() =>
        Should.Throw<ArgumentNullException>(() =>
        {
            using var image = Pattern(4, 4);
            image.Mutate(null!);
        });

    private static Image Pattern(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i] = (byte)((i * 37 + 11) & 0xFF);
        return image;
    }
}
