using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Tests;

public class AnalysisTests
{
    private static Image Empty(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        image.Clear();
        return image;
    }

    [Fact]
    public void AlphaBounds_FindsTheOpaqueRectangle()
    {
        using var image = Empty(8, 8);
        image.FillRect(new PixelRect(2, 3, 4, 2), new RgbaColor(10, 20, 30, 255));

        image.AlphaBounds().ShouldBe(new PixelRect(2, 3, 4, 2));
    }

    [Fact]
    public void AlphaBounds_IsEmptyWhenEverythingIsTransparent()
    {
        using var image = Empty(6, 6);
        var bounds = image.AlphaBounds();

        bounds.Width.ShouldBe(0);
        bounds.Height.ShouldBe(0);
    }

    [Fact]
    public void AlphaBounds_HonoursTheThreshold()
    {
        using var image = Empty(6, 6);
        image.FillRect(new PixelRect(0, 0, 6, 6), new RgbaColor(0, 0, 0, 40));
        image.FillRect(new PixelRect(3, 3, 2, 2), new RgbaColor(0, 0, 0, 200));

        image.AlphaBounds(0.5f).ShouldBe(new PixelRect(3, 3, 2, 2));
        image.AlphaBounds().ShouldBe(new PixelRect(0, 0, 6, 6));
    }

    [Fact]
    public void Statistics_OfATwoLevelImage()
    {
        using var image = Empty(8, 8);
        image.Fill(RgbaColor.Black);
        image.FillRect(new PixelRect(0, 0, 4, 8), RgbaColor.White);

        var stats = image.Statistics();

        stats.R.Mean.ShouldBe(0.5f, 1e-3f);
        stats.R.StandardDeviation.ShouldBe(0.5f, 1e-3f);
        stats.R.Entropy.ShouldBe(1f, 1e-3f);        // two equally likely levels
        stats.R.Min.ShouldBe(0f);
        stats.R.Max.ShouldBe(1f);
        stats.A.Mean.ShouldBe(1f, 1e-3f);           // every pixel is opaque
        stats.A.Entropy.ShouldBe(0f, 1e-6f);
    }

    [Fact]
    public void Statistics_OfAFlatImageHasNoDeviationOrEntropy()
    {
        using var image = Empty(6, 6);
        image.Fill(new RgbaColor(120, 120, 120, 255));

        var stats = image.Statistics();

        stats.G.Mean.ShouldBe(120f / 255f, 1e-4f);
        stats.G.StandardDeviation.ShouldBe(0f, 1e-6f);
        stats.G.Entropy.ShouldBe(0f, 1e-6f);
        stats.G.Min.ShouldBe(stats.G.Max);
    }

    [Fact]
    public void Psnr_IsInfiniteForIdenticalImages()
    {
        using var first = Empty(4, 4);
        using var second = Empty(4, 4);
        first.Fill(RgbaColor.White);
        second.Fill(RgbaColor.White);

        first.Psnr(second).ShouldBe(double.PositiveInfinity);
    }

    [Fact]
    public void Psnr_DropsAsTheDifferenceGrows()
    {
        using var first = Empty(4, 4);
        using var close = Empty(4, 4);
        using var far = Empty(4, 4);
        first.Fill(new RgbaColor(100, 100, 100, 255));
        close.Fill(new RgbaColor(101, 101, 101, 255));
        far.Fill(new RgbaColor(140, 140, 140, 255));

        var closePsnr = first.Psnr(close);
        var farPsnr = first.Psnr(far);

        closePsnr.ShouldBeGreaterThan(40);
        farPsnr.ShouldBeGreaterThan(0);
        farPsnr.ShouldBeLessThan(closePsnr);
    }

    [Fact]
    public void Psnr_RejectsMismatchedSizes()
    {
        using var first = Empty(4, 4);
        using var second = Empty(4, 5);

        Should.Throw<ArgumentException>(() => first.Psnr(second));
    }

    [Fact]
    public void Diff_ReportsZeroForIdenticalImages()
    {
        using var first = Empty(4, 4);
        using var second = Empty(4, 4);
        first.Fill(new RgbaColor(20, 40, 60, 255));
        second.Fill(new RgbaColor(20, 40, 60, 255));

        var result = first.Diff(second);

        result.MaxDifference.ShouldBe(0f);
        result.MeanDifference.ShouldBe(0f);
        result.PixelsOverThreshold.ShouldBe(0);
    }

    [Fact]
    public void Diff_FindsTheSingleDifferentPixel()
    {
        using var first = Empty(4, 4);
        using var second = Empty(4, 4);
        first.Fill(RgbaColor.Black);
        second.Fill(RgbaColor.Black);
        second.WritablePixels[(1 * 4 + 2) * 4 + 1] = 255;

        var result = first.Diff(second, threshold: 1f / 510f);

        result.MaxDifference.ShouldBe(1f, 1e-6f);
        result.PixelsOverThreshold.ShouldBe(1);
        result.MeanDifference.ShouldBe(1f / 16f, 1e-5f);
    }

    [Fact]
    public void Diff_RejectsMismatchedSizes()
    {
        using var first = Empty(4, 4);
        using var second = Empty(3, 4);

        Should.Throw<ArgumentException>(() => first.Diff(second));
    }
}
