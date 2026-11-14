using System.Security.Cryptography;
using SimdImaging.Shared;

namespace SimdImaging.Tests;

public class DiffToolTests
{
    [Fact]
    public void Diff_IdenticalBuffersAreByteExact()
    {
        var image = RandomPixels(7, 5);
        var report = PixelDiff.Analyze(image, image, 7, 5);

        report.SizeMismatch.ShouldBeFalse();
        report.DifferentPixels.ShouldBe(0);
        report.MaxChannelDiff.ShouldBe(0);
        report.ExactChannels.ShouldBe(7L * 5 * 4);
        report.FirstMismatchPixel.ShouldBe(-1);
        report.TopMismatches.ShouldBeEmpty();
        report.Grade().ShouldBe("byte-exact");
        report.Sha256A.ShouldBe(report.Sha256B);
        report.Sha256A.ShouldBe(Convert.ToHexString(SHA256.HashData(image)));
        double.IsPositiveInfinity(report.PsnrRgb).ShouldBeTrue();
    }

    [Fact]
    public void Diff_LocatesTheFirstMismatchAndAccumulatesPerChannelError()
    {
        var a = new byte[4 * 4 * 4]; // 4x4 RGBA8, all zero
        var b = (byte[])a.Clone();
        b[6 * 4 + 0] = 10;           // pixel (2, 1): red
        b[6 * 4 + 2] = 3;            // pixel (2, 1): blue
        b[15 * 4 + 1] = 200;         // pixel (3, 3): green, the largest delta

        var report = PixelDiff.Analyze(a, b, 4, 4);

        report.DifferentPixels.ShouldBe(2);
        report.FirstMismatchPixel.ShouldBe(6);
        report.FirstMismatchX.ShouldBe(2);
        report.FirstMismatchY.ShouldBe(1);
        report.FirstMismatchChannel.ShouldBe(0);
        report.MaxChannelDiff.ShouldBe(200);
        report.PerChannel[0].Sum.ShouldBe(10);
        report.PerChannel[0].Max.ShouldBe(10);
        report.PerChannel[0].Differing.ShouldBe(1);
        report.PerChannel[1].Sum.ShouldBe(200);
        report.PerChannel[2].Sum.ShouldBe(3);
        report.PerChannel[3].Differing.ShouldBe(0);
        report.Grade().ShouldBe("algorithm/semantics differ");
    }

    [Fact]
    public void Diff_TopNAreLargestDeltasAndTiesKeepTheEarlierPixel()
    {
        var a = new byte[8 * 4]; // 8x1
        var b = (byte[])a.Clone();
        b[1 * 4] = 50;
        b[3 * 4] = 50;
        b[5 * 4] = 50;
        b[7 * 4] = 90;

        var report = PixelDiff.Analyze(a, b, 8, 1, topN: 2);

        report.TopMismatches.Length.ShouldBe(2);
        report.TopMismatches[0].PixelIndex.ShouldBe(7);
        report.TopMismatches[0].Delta.ShouldBe(90);
        report.TopMismatches[1].PixelIndex.ShouldBe(1);
        report.TopMismatches[1].Delta.ShouldBe(50);
        report.TopMismatches[1].X.ShouldBe(1);
    }

    [Fact]
    public void Diff_HeatMapIsAP6PpmSizedToTheImage()
    {
        var a = new byte[4 * 3 * 4]; // 4x3
        var b = (byte[])a.Clone();
        b[0] = 255;

        var report = PixelDiff.Analyze(a, b, 4, 3);
        var header = System.Text.Encoding.ASCII.GetString(report.HeatMapPpm, 0, 11);

        header.ShouldBe("P6\n4 3\n255\n");
        report.HeatMapPpm.Length.ShouldBe(11 + 4 * 3 * 3);
        report.HeatMapPpm[11].ShouldBe((byte)255); // first pixel carries the largest delta
        report.HeatMapPpm[11 + 3].ShouldBe((byte)0);
    }

    [Fact]
    public void Diff_SizeMismatchIsExplicitNeverASilentFallback()
    {
        var a = new byte[4 * 4];
        var report = PixelDiff.Analyze(a, new byte[4], 2, 2);

        report.SizeMismatch.ShouldBeTrue();
        report.DifferentPixels.ShouldBe(-1);
        report.FirstMismatchPixel.ShouldBe(-1);
        report.Grade().ShouldBe("size mismatch");
        report.Sha256A.ShouldNotBe(report.Sha256B);
    }

    private static byte[] RandomPixels(int width, int height)
    {
        var data = new byte[checked(width * height * 4)];
        new Random(20261114).NextBytes(data);
        return data;
    }
}
