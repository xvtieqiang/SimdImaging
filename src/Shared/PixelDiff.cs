using System.Security.Cryptography;
using System.Text;

namespace SimdImaging.Shared;

/// <summary>Accumulated error for one RGBA channel over the whole image.</summary>
internal readonly record struct ChannelError(long Sum, long SumSquares, int Max, long Differing)
{
    public double Mean(long totalSamples) => totalSamples == 0 ? 0 : (double)Sum / totalSamples;
}

/// <summary>One located pixel mismatch; <see cref="Channel"/> is the channel with the largest delta.</summary>
internal readonly record struct Mismatch(int PixelIndex, int X, int Y, int Channel, int Delta);

/// <summary>
/// Byte-level pixel diff used by the reference-vector, fusion-parity and SIMD-tier gates.
/// Source-shared on purpose: the test project and the comparison tool link this one file, so
/// "how different are these two images" has exactly one definition in the repository.
/// </summary>
internal sealed class DiffReport
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool SizeMismatch { get; init; }
    public required long TotalPixels { get; init; }
    public required long DifferentPixels { get; init; }
    public required long ExactChannels { get; init; }
    public required int MaxChannelDiff { get; init; }
    public required double MeanAbsDiff { get; init; }
    public required double PsnrRgb { get; init; }
    public required int FirstMismatchPixel { get; init; }
    public required int FirstMismatchX { get; init; }
    public required int FirstMismatchY { get; init; }
    public required int FirstMismatchChannel { get; init; }
    public required string Sha256A { get; init; }
    public required string Sha256B { get; init; }
    public required ChannelError[] PerChannel { get; init; }
    public required Mismatch[] TopMismatches { get; init; }
    public required byte[] HeatMapPpm { get; init; }

    /// <summary>Grades on the same scale as <c>Metrics.Grade</c>: no second ruler is introduced.</summary>
    public string Grade()
    {
        if (SizeMismatch) return "size mismatch";
        if (DifferentPixels == 0) return "byte-exact";
        if (MaxChannelDiff <= 1) return "<=1 LSB";
        if (double.IsPositiveInfinity(PsnrRgb) || PsnrRgb >= 40) return "visually equivalent (PSNR>=40)";
        if (PsnrRgb >= 30) return "close (PSNR>=30)";
        if (PsnrRgb >= 20) return "clearly different (PSNR>=20)";
        return "algorithm/semantics differ";
    }
}

internal static class PixelDiff
{
    /// <summary>Full SHA-256 of a pixel or file buffer, upper-case hex.</summary>
    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// Compares two tightly packed RGBA8 buffers. Alpha participates in the per-channel statistics and
    /// in mismatch locating, while PSNR/mean stay on RGB so the grade matches the oracle parity tool.
    /// </summary>
    public static DiffReport Analyze(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height, int topN = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(topN);

        var shaA = Sha256(a);
        var shaB = Sha256(b);
        var expected = checked((long)width * height * 4);
        if (a.Length != b.Length || a.Length != expected)
        {
            return new DiffReport
            {
                Width = width, Height = height, SizeMismatch = true,
                TotalPixels = 0, DifferentPixels = -1, ExactChannels = 0,
                MaxChannelDiff = 255, MeanAbsDiff = double.NaN, PsnrRgb = double.NaN,
                FirstMismatchPixel = -1, FirstMismatchX = -1, FirstMismatchY = -1, FirstMismatchChannel = -1,
                Sha256A = shaA, Sha256B = shaB,
                PerChannel = new ChannelError[4], TopMismatches = [], HeatMapPpm = [],
            };
        }

        var pixels = width * height;
        var perChannel = new ChannelError[4];
        var top = new List<Mismatch>(Math.Min(topN, 64));
        var heat = new byte[checked(pixels * 3)];
        long exact = 0, different = 0, sumRgb = 0, sqRgb = 0;
        var max = 0;
        var firstPixel = -1;
        var firstChannel = -1;

        for (var p = 0; p < pixels; p++)
        {
            var off = p * 4;
            var pixelMax = 0;
            var pixelMaxChannel = 0;

            for (var ch = 0; ch < 4; ch++)
            {
                var d = Math.Abs(a[off + ch] - b[off + ch]);
                if (d == 0)
                {
                    exact++;
                }
                else
                {
                    var prev = perChannel[ch];
                    perChannel[ch] = new ChannelError(prev.Sum + d, prev.SumSquares + (long)d * d,
                        Math.Max(prev.Max, d), prev.Differing + 1);
                }

                if (ch < 3)
                {
                    sumRgb += d;
                    sqRgb += (long)d * d;
                }

                if (d > max) max = d;
                if (d > pixelMax)
                {
                    pixelMax = d;
                    pixelMaxChannel = ch;
                }
            }

            var h = p * 3;
            heat[h] = (byte)pixelMax;

            if (pixelMax == 0) continue;

            different++;
            if (firstPixel < 0)
            {
                firstPixel = p;
                firstChannel = pixelMaxChannel;
            }

            Consider(top, topN, new Mismatch(p, p % width, p / width, pixelMaxChannel, pixelMax));
        }

        var rgbSamples = (long)pixels * 3;
        var mean = (double)sumRgb / rgbSamples;
        var psnr = sqRgb == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / ((double)sqRgb / rgbSamples));

        return new DiffReport
        {
            Width = width, Height = height, SizeMismatch = false,
            TotalPixels = pixels, DifferentPixels = different, ExactChannels = exact,
            MaxChannelDiff = max, MeanAbsDiff = mean, PsnrRgb = psnr,
            FirstMismatchPixel = firstPixel,
            FirstMismatchX = firstPixel < 0 ? -1 : firstPixel % width,
            FirstMismatchY = firstPixel < 0 ? -1 : firstPixel / width,
            FirstMismatchChannel = firstChannel,
            Sha256A = shaA, Sha256B = shaB,
            PerChannel = perChannel, TopMismatches = top.ToArray(),
            HeatMapPpm = Ppm(width, height, heat),
        };
    }

    // 'top' is kept sorted best-first: larger delta wins; on a tie the earlier pixel wins.
    // That tie-break makes the report independent of traversal order and therefore deterministic.
    private static void Consider(List<Mismatch> top, int topN, in Mismatch candidate)
    {
        if (topN == 0) return;
        if (top.Count == topN)
        {
            var last = top[^1];
            if (candidate.Delta < last.Delta ||
                (candidate.Delta == last.Delta && candidate.PixelIndex > last.PixelIndex)) return;
        }

        var at = top.Count;
        for (var i = 0; i < top.Count; i++)
        {
            var cur = top[i];
            if (candidate.Delta > cur.Delta ||
                (candidate.Delta == cur.Delta && candidate.PixelIndex < cur.PixelIndex))
            {
                at = i;
                break;
            }
        }

        if (at == topN) return;
        top.Insert(at, candidate);
        if (top.Count > topN) top.RemoveAt(top.Count - 1);
    }

    /// <summary>P6 PPM heat map: black = identical, red = larger per-pixel delta.</summary>
    private static byte[] Ppm(int width, int height, byte[] red)
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        var result = new byte[checked(header.Length + red.Length)];
        header.CopyTo(result, 0);
        red.CopyTo(result, header.Length);
        return result;
    }
}
