using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// M11 pyramid. The oracle is a double-precision transcription of the Burt–Adcock definition (5-tap
/// binomial, even-phase analysis, zero-stuffed synthesis), so this checks the indices, the parity and the
/// odd-size clamping rather than re-running the same float loop.
/// </summary>
public class PyramidTests
{
    [Theory]
    [InlineData(16, 16)]
    [InlineData(17, 9)]
    [InlineData(5, 3)]
    [InlineData(1, 9)]
    [InlineData(9, 1)]
    public void Downsample_MatchesTheDoubleReference(int width, int height)
    {
        var plane = Ramp(width * height);
        var dw = Pyramid.DownsampledLength(width);
        var dh = Pyramid.DownsampledLength(height);
        var destination = new float[dw * dh];
        var scratch = new float[dw * height];

        Pyramid.Downsample(plane, width, height, destination, dw, dh, scratch);

        var expected = NaiveDownsample(plane, width, height);
        for (var i = 0; i < destination.Length; i++)
            Math.Abs(destination[i] - expected[i]).ShouldBeLessThan(1e-6, $"sample {i}");
    }

    [Theory]
    [InlineData(16, 16, 31, 29)]
    [InlineData(9, 5, 17, 9)]
    [InlineData(1, 5, 1, 9)]
    [InlineData(5, 1, 9, 1)]
    public void Upsample_MatchesTheDoubleReferenceAndTheExplicitTarget(int width, int height,
        int targetWidth, int targetHeight)
    {
        var plane = Ramp(width * height);
        var destination = new float[targetWidth * targetHeight];
        var scratch = new float[targetWidth * height];

        Pyramid.Upsample(plane, width, height, destination, targetWidth, targetHeight, scratch);

        var expected = NaiveUpsample(plane, width, height, targetWidth, targetHeight);
        for (var i = 0; i < destination.Length; i++)
            Math.Abs(destination[i] - expected[i]).ShouldBeLessThan(1e-6, $"sample {i}");
    }

    [Fact]
    public void ConstantPlane_StaysConstantThroughBothStages()
    {
        const int width = 17, height = 9;
        var plane = new float[width * height];
        plane.AsSpan().Fill(0.375f);

        var dw = Pyramid.DownsampledLength(width);
        var dh = Pyramid.DownsampledLength(height);
        var small = new float[dw * dh];
        Pyramid.Downsample(plane, width, height, small, dw, dh, new float[dw * height]);
        foreach (var value in small)
            Math.Abs((double)value - 0.375).ShouldBeLessThan(1e-6);

        var back = new float[width * height];
        Pyramid.Upsample(small, dw, dh, back, width, height, new float[width * dh]);
        foreach (var value in back)
            Math.Abs((double)value - 0.375).ShouldBeLessThan(1e-6);
    }

    private static double[] NaiveDownsample(float[] plane, int width, int height)
    {
        double[] kernel = [1.0 / 16, 4.0 / 16, 6.0 / 16, 4.0 / 16, 1.0 / 16];
        var dw = (width + 1) / 2;
        var dh = (height + 1) / 2;
        var horizontal = new double[dw * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < dw; x++)
            {
                var sum = 0.0;
                for (var k = 0; k < 5; k++)
                    sum += kernel[k] * plane[y * width + Math.Clamp(2 * x + k - 2, 0, width - 1)];
                horizontal[y * dw + x] = sum;
            }

        var result = new double[dw * dh];
        for (var y = 0; y < dh; y++)
            for (var x = 0; x < dw; x++)
            {
                var sum = 0.0;
                for (var k = 0; k < 5; k++)
                    sum += kernel[k] * horizontal[Math.Clamp(2 * y + k - 2, 0, height - 1) * dw + x];
                result[y * dw + x] = sum;
            }

        return result;
    }

    private static double[] NaiveUpsample(float[] plane, int width, int height, int targetWidth,
        int targetHeight)
    {
        double[] kernel = [1.0 / 16, 4.0 / 16, 6.0 / 16, 4.0 / 16, 1.0 / 16];
        var horizontal = new double[targetWidth * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < targetWidth; x++)
            {
                var sum = 0.0;
                for (var k = -2; k <= 2; k++)
                {
                    var source = x + k;
                    if ((source & 1) != 0)
                        continue;
                    var j = Math.Clamp(source / 2, 0, width - 1);
                    sum += 2 * kernel[k + 2] * plane[y * width + j];
                }

                horizontal[y * targetWidth + x] = sum;
            }

        var result = new double[targetWidth * targetHeight];
        for (var y = 0; y < targetHeight; y++)
            for (var x = 0; x < targetWidth; x++)
            {
                var sum = 0.0;
                for (var k = -2; k <= 2; k++)
                {
                    var source = y + k;
                    if ((source & 1) != 0)
                        continue;
                    var j = Math.Clamp(source / 2, 0, height - 1);
                    sum += 2 * kernel[k + 2] * horizontal[j * targetWidth + x];
                }

                result[y * targetWidth + x] = sum;
            }

        return result;
    }

    private static float[] Ramp(int length)
    {
        var plane = new float[length];
        for (var i = 0; i < length; i++)
            plane[i] = (i * 37 % 101) / 101f;
        return plane;
    }
}
