using System.Runtime.InteropServices;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Exact squared EDT against hand-computed fields. Every expected number below is arithmetic a reader can
/// check on paper — the operator is D0, so there is no tolerance to hide behind.
/// </summary>
public class DistanceTransformTests
{
    private static Image Mask(bool[] foreground, int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var i = 0; i < width * height; i++)
        {
            var alpha = foreground[i] ? (byte)255 : (byte)0;
            var offset = i * 4;
            pixels[offset] = 0;
            pixels[offset + 1] = 0;
            pixels[offset + 2] = 0;
            pixels[offset + 3] = alpha;
        }

        return image;
    }

    private static float[] Field(WorkingBuffer buffer)
    {
        var span = MemoryMarshal.Cast<byte, float>(buffer.View.Span);
        var values = new float[buffer.Width * buffer.Height];
        for (var i = 0; i < values.Length; i++)
            values[i] = span[i * 4];

        return values;
    }

    [Fact]
    public void OneRowIsTheSquaredDistanceAlongTheLine()
    {
        using var mask = Mask([false, false, true, false, false], 5, 1);

        using var result = DistanceTransformOps.SquaredEuclidean(mask[0].Pixels.View);

        Field(result).ShouldBe([4f, 1f, 0f, 1f, 4f], tolerance: 1e-4f);
    }

    [Fact]
    public void ADiagonalNeighbourCostsTwo()
    {
        // 3×3 with only the centre set: the corners are √2 away, so 2 when squared, and the edges 1.
        using var mask = Mask([false, false, false,
                               false, true, false,
                               false, false, false], 3, 3);

        using var result = DistanceTransformOps.SquaredEuclidean(mask[0].Pixels.View);

        Field(result).ShouldBe([2f, 1f, 2f,
                                1f, 0f, 1f,
                                2f, 1f, 2f], tolerance: 1e-4f);
    }

    [Fact]
    public void AFullMaskIsZeroEverywhere()
    {
        using var mask = Mask([true, true, true, true], 2, 2);

        using var result = DistanceTransformOps.SquaredEuclidean(mask[0].Pixels.View);

        Field(result).ShouldBe([0f, 0f, 0f, 0f], tolerance: 1e-4f);
    }

    [Fact]
    public void AnEmptyMaskReportsEveryPixelAsFarAway()
    {
        // No foreground at all: there is no nearest pixel, so the field is the seed's infinity. Documented
        // rather than clamped to zero, which would read as "everything is foreground".
        using var mask = Mask([false, false, false, false], 2, 2);

        using var result = DistanceTransformOps.SquaredEuclidean(mask[0].Pixels.View);

        Field(result).ShouldAllBe(v => v > 1e18f);
    }

    [Fact]
    public void TheSquaredFieldMatchesABruteForceScan()
    {
        // Deterministic pattern, then the O(n²) definition for every pixel.
        const int width = 9;
        const int height = 7;
        var foreground = new bool[width * height];
        for (var i = 0; i < foreground.Length; i++)
            foreground[i] = (i * 7 + 3) % 11 == 0;

        using var mask = Mask(foreground, width, height);
        using var result = DistanceTransformOps.SquaredEuclidean(mask[0].Pixels.View);
        var field = Field(result);

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var best = float.MaxValue;
                for (var j = 0; j < height; j++)
                    for (var i = 0; i < width; i++)
                    {
                        if (!foreground[j * width + i])
                            continue;
                        best = Math.Min(best, (x - i) * (x - i) + (y - j) * (y - j));
                    }

                field[y * width + x].ShouldBe(best, 1e-4f, $"({x},{y})");
            }
    }

    [Fact]
    public void TheThresholdIsRejectedOutsideItsRange()
    {
        using var mask = Mask([true], 1, 1);
        var view = mask[0].Pixels.View;

        Should.Throw<ArgumentOutOfRangeException>(() => DistanceTransformOps.SquaredEuclidean(view, 0f));
        Should.Throw<ArgumentOutOfRangeException>(() => DistanceTransformOps.SquaredEuclidean(view, 1.5f));
    }

    [Fact]
    public void ATallImageIsTransformedColumnByColumn()
    {
        // 3×40: taller than it is wide. Every other case here is wider than tall, which is exactly why a
        // column pass with row-length scratch would have gone unnoticed.
        const int width = 3;
        const int height = 40;
        var foreground = new bool[width * height];
        foreground[0] = true;

        using var mask = Mask(foreground, width, height);
        using var result = DistanceTransformOps.SquaredEuclidean(mask[0].Pixels.View);
        var field = Field(result);

        field[0].ShouldBe(0f, 1e-4f);
        field[1].ShouldBe(1f, 1e-4f);                   // (1,0)
        field[width].ShouldBe(1f, 1e-4f);               // (0,1)
        field[width + 1].ShouldBe(2f, 1e-4f);           // (1,1)
        field[(height - 1) * width].ShouldBe((height - 1) * (height - 1), 1e-4f);
    }

    [Fact]
    public void TheFacadeRelabelsTheFloatFieldInsteadOfKeepingTheInputFormat()
    {
        // The operator is the only image-to-image entry point that widens Rgba8 to Rgba32F, so the facade has
        // to take the format from the returned buffer. Relabelled as 8-bit, the float bytes would read as noise.
        using var mask = Mask([false, true, false], 3, 1);

        using var result = mask.Pipe().DistanceTransform().ToImage();

        result.Format.ShouldBe(WorkingFormat.Rgba32F);
        Field(result[0].Pixels).ShouldBe([1f, 0f, 1f], tolerance: 1e-4f);
    }
}
