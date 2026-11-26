using System.Runtime.InteropServices;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Cross-guided filtering: the coefficients come from a guide that is not the source. The reference below is the
/// definition written out with double-precision window sums, not a second box-sum implementation, so the test
/// would catch a wrong covariance as well as a wrong average.
/// </summary>
public class GuidedFilterTests
{
    private static Image FloatImage(int width, int height, Func<int, int, int, float> value)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba32F);
        var pixels = MemoryMarshal.Cast<byte, float>(image.WritablePixels);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                    pixels[offset + channel] = value(x, y, channel);
                pixels[offset + 3] = 1f;
            }

        return image;
    }

    private static float[] Plane(WorkingBuffer buffer)
    {
        var span = MemoryMarshal.Cast<byte, float>(buffer.View.Span);
        var values = new float[buffer.Width * buffer.Height * 4];
        span[..values.Length].CopyTo(values);
        return values;
    }

    private static float[] WindowMean(float[] plane, int width, int height, int radius)
    {
        var mean = new float[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var sum = 0.0;
                var count = 0;
                for (var dy = -radius; dy <= radius; dy++)
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var sy = Math.Clamp(y + dy, 0, height - 1);
                        var sx = Math.Clamp(x + dx, 0, width - 1);
                        sum += plane[sy * width + sx];
                        count++;
                    }

                mean[y * width + x] = (float)(sum / count);
            }

        return mean;
    }

    /// <summary>The definition: a = cov(I,G)/(var(G)+eps), b = mean(I) − a·mean(G), then box-average a and b.</summary>
    private static float[] CrossReference(float[] input, float[] guide, int width, int height, int radius,
        float epsilon)
    {
        var count = width * height;
        var meanI = WindowMean(input, width, height, radius);
        var meanG = WindowMean(guide, width, height, radius);

        var squares = new float[count];
        var products = new float[count];
        for (var i = 0; i < count; i++)
        {
            squares[i] = guide[i] * guide[i];
            products[i] = input[i] * guide[i];
        }

        var meanSquares = WindowMean(squares, width, height, radius);
        var meanProducts = WindowMean(products, width, height, radius);

        var a = new float[count];
        var b = new float[count];
        for (var i = 0; i < count; i++)
        {
            var variance = Math.Max(0f, meanSquares[i] - meanG[i] * meanG[i]);
            a[i] = (meanProducts[i] - meanI[i] * meanG[i]) / (variance + epsilon);
            b[i] = meanI[i] - a[i] * meanG[i];
        }

        var meanA = WindowMean(a, width, height, radius);
        var meanB = WindowMean(b, width, height, radius);

        var output = new float[count];
        for (var i = 0; i < count; i++)
            output[i] = Math.Clamp(meanA[i] * input[i] + meanB[i], 0f, 1f);

        return output;
    }

    [Fact]
    public void CrossGuidedWorkspace_SeparatesWorkerScratchFromReturnedFrame()
    {
        const int width = 96;
        const int height = 96;
        const int radius = 3;
        const int bandRows = 32;
        var op = PlanOps.GuidedBy(radius, bandRows: bandRows);
        var scratch = GuidedFilterOps.CrossGuidedPlanPerWorkerScratchBytes(width, height, radius, bandRows);
        var returned = GuidedFilterOps.CrossGuidedPlanReturnedBufferBytes(width, height, WorkingFormat.Rgba8);

        op.Scratch!(width, height, height, WorkingFormat.Rgba8).ShouldBe(scratch + returned);
        op.Contract.Workspace.HasPerWorkerEstimate.ShouldBeTrue();
        op.Contract.Workspace.HasWholeImageEstimate.ShouldBeTrue();
    }
    [Fact]
    public void TheCrossFilterMatchesADirectWindowReference()
    {
        const int width = 7;
        const int height = 6;
        const int radius = 1;
        const float epsilon = 0.02f;

        using var source = FloatImage(width, height,
            (x, y, c) => ((x * 3 + y * 5 + c * 2) % 7) / 6f);
        using var guide = FloatImage(width, height,
            (x, y, c) => ((x + y * 2 + c) % 5) / 4f);

        using var result = GuidedFilterOps.GuidedFilter(source[0].Pixels.View, guide[0].Pixels.View,
            radius, epsilon);

        var actual = Plane(result);
        var input = Plane(source[0].Pixels);
        var guideValues = Plane(guide[0].Pixels);

        for (var channel = 0; channel < 3; channel++)
        {
            var expected = CrossReference(
                Channel(input, width * height, channel), Channel(guideValues, width * height, channel),
                width, height, radius, epsilon);

            for (var i = 0; i < expected.Length; i++)
                actual[i * 4 + channel].ShouldBe(expected[i], 1e-3f, $"channel {channel}, pixel {i}");
        }
    }

    private static float[] Channel(float[] interleaved, int pixels, int channel)
    {
        var values = new float[pixels];
        for (var i = 0; i < pixels; i++)
            values[i] = interleaved[i * 4 + channel];
        return values;
    }

    [Fact]
    public void PassingTheImageItselfIsByteIdenticalToTheSelfGuidedOverload()
    {
        // The guide is the source: the same operator, so it must take the frozen self-guided path rather than
        // go through the covariance formula and drift in the last bits.
        using var image = Image.Create(32, 24, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i * 37 + 11) & 0xFF);

        using var self = image.Pipe().GuidedFilter(3, 1e-4f).ToImage();
        using var cross = image.Pipe().GuidedFilter(image, 3, 1e-4f).ToImage();

        cross.WritablePixels.ToArray().ShouldBe(self.WritablePixels.ToArray());
    }

    [Fact]
    public void TheGuideMustMatchTheSourceSize()
    {
        using var source = Image.Create(8, 6, WorkingFormat.Rgba8);
        using var guide = Image.Create(8, 7, WorkingFormat.Rgba8);

        Should.Throw<ArgumentException>(() =>
            GuidedFilterOps.GuidedFilter(source[0].Pixels.View, guide[0].Pixels.View, 2));
    }

    [Fact]
    public void ANullGuideIsRejectedByTheFacade()
    {
        using var source = Image.Create(4, 4, WorkingFormat.Rgba8);

        Should.Throw<ArgumentNullException>(() => source.Pipe().GuidedFilter((Image)null!).ToImage());
    }

    [Fact]
    public void AFlatGuideAveragesAcrossTheEdgeAndAStepGuideKeepsIt()
    {
        // Source: left half black, right half white. A flat guide knows nothing about the step, so a falls to
        // zero and the output is the local mean; a guide that carries the step keeps a ≈ 1 and the edge stays.
        const int width = 16;
        const int height = 8;

        using var source = Image.Create(width, height, WorkingFormat.Rgba8);
        var sourcePixels = source.WritablePixels;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var value = x < width / 2 ? (byte)0 : (byte)255;
                sourcePixels[offset] = value;
                sourcePixels[offset + 1] = value;
                sourcePixels[offset + 2] = value;
                sourcePixels[offset + 3] = 255;
            }

        using var flat = Image.Create(width, height, WorkingFormat.Rgba8);
        var flatPixels = flat.WritablePixels;
        for (var i = 0; i < flatPixels.Length; i++)
            flatPixels[i] = 128;

        using var blurred = source.Pipe().GuidedFilter(flat, 4, 1e-4f).ToImage();
        using var kept = source.Pipe().GuidedFilter(source, 4, 1e-4f).ToImage();

        var blurredPixels = blurred.WritablePixels;
        var keptPixels = kept.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            var left = (y * width + 7) * 4;
            var right = (y * width + 8) * 4;

            blurredPixels[left].ShouldBeInRange((byte)90, (byte)170);
            blurredPixels[right].ShouldBeInRange((byte)90, (byte)170);

            keptPixels[left].ShouldBeLessThan((byte)3);
            keptPixels[right].ShouldBeGreaterThan((byte)252);
        }
    }
}
