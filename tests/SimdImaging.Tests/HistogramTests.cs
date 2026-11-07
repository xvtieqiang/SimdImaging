namespace SimdImaging.Tests;

public class HistogramTests
{
    private static Image FromValues(params byte[] values)
    {
        var image = Image.Create(values.Length, 1, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var i = 0; i < values.Length; i++)
        {
            var o = i * 4;
            span[o] = values[i];
            span[o + 1] = values[i];
            span[o + 2] = values[i];
            span[o + 3] = 255;
        }

        return image;
    }

    [Fact]
    public void Histogram_CountsCodeValues()
    {
        using var image = FromValues(0, 0, 10, 255);

        var histogram = image.Histogram(0);

        histogram.Length.ShouldBe(256);
        histogram[0].ShouldBe(2);
        histogram[10].ShouldBe(1);
        histogram[255].ShouldBe(1);
    }

    [Fact]
    public void Histogram_RejectsAnUnknownChannel()
    {
        using var image = FromValues(1);
        Should.Throw<ArgumentOutOfRangeException>(() => image.Histogram(4));
    }

    [Fact]
    public void Equalize_AUniformGradientIsIdentity()
    {
        var values = new byte[256];
        for (var i = 0; i < 256; i++)
            values[i] = (byte)i;

        using var image = FromValues(values);
        using var equalized = image.EqualizeHistogram();

        equalized.WritablePixels.ToArray().ShouldBe(image.WritablePixels.ToArray());
    }

    [Fact]
    public void Equalize_StretchesADarkRamp()
    {
        var values = new byte[11];
        for (var i = 0; i < values.Length; i++)
            values[i] = (byte)(10 + i);

        using var image = FromValues(values);
        using var equalized = image.EqualizeHistogram();

        var span = equalized.WritablePixels;
        span[0].ShouldBe((byte)0);
        span[(values.Length - 1) * 4].ShouldBe((byte)255);
    }

    [Fact]
    public void Equalize_LeavesAFlatImageAlone()
    {
        using var image = FromValues(77, 77, 77, 77);
        using var equalized = image.EqualizeHistogram();

        equalized.WritablePixels.ToArray().ShouldBe(image.WritablePixels.ToArray());
    }

    [Fact]
    public void Equalize_PreservesAlpha()
    {
        using var image = FromValues(10, 20, 30, 40);
        var span = image.WritablePixels;
        for (var i = 3; i < span.Length; i += 4)
            span[i] = 91;

        using var equalized = image.EqualizeHistogram();
        var result = equalized.WritablePixels;
        for (var i = 3; i < result.Length; i += 4)
            result[i].ShouldBe((byte)91);
    }
}
