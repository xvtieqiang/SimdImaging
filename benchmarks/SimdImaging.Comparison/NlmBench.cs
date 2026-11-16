using System.Diagnostics;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// End-to-end stopwatch for <see cref="NonLocalMeansOps.NonLocalMeans"/> on the same fixture the benchmark
/// harness uses. It measures the PRODUCT operator (not a replica), at any size/radii, and prints the
/// output SHA-256 so byte identity is checked on every run, not just in xUnit.
/// <para>Usage: <c>nlm-bench [WxH] [S] [P] [h] [sigma] [rounds]</c>.</para>
/// </summary>
internal static class NlmBench
{
    // SHA-256 (first 16 hex) of NonLocalMeans(1080p fixture, S=3, P=1, h=0.1, sigma=0). Verified against
    // the shipped operator AND the byte-identical replica in NlmAblation before any optimisation landed.
    // Any change to the default output MUST move this anchor; it is the byte-identity gate for the effort.
    private const string Anchor1080p = "131CC44092703366";

    public static void Run(string[] args)
    {
        var width = 1920;
        var height = 1080;
        var s = 3;
        var p = 1;
        var h = 0.1f;
        var sigma = 0f;
        var rounds = 5;
        if (args.Length > 1) ParseSize(args[1], ref width, ref height);
        if (args.Length > 2) s = int.Parse(args[2], CultureInfo.InvariantCulture);
        if (args.Length > 3) p = int.Parse(args[3], CultureInfo.InvariantCulture);
        if (args.Length > 4) h = float.Parse(args[4], CultureInfo.InvariantCulture);
        if (args.Length > 5) sigma = float.Parse(args[5], CultureInfo.InvariantCulture);
        if (args.Length > 6) rounds = int.Parse(args[6], CultureInfo.InvariantCulture);

        using var image = OurImage.Create(width, height, WorkingFormat.Rgba8);
        var pix = image.WritablePixels;
        for (var i = 0; i < pix.Length; i++)
            pix[i] = (byte)((i * 37 + 11) & 0xFF);
        var view = image[0].Pixels.View;

        string sha;
        using (var warm = NonLocalMeansOps.NonLocalMeans(view, s, p, h, sigma))
            sha = Metrics.Sha256(warm.View.Span);

        Console.WriteLine($"machine: {Env.MachineLine}");
        Console.WriteLine($"size {width}x{height} S={s} P={p} h={h} sigma={sigma} " +
                          $"workers={RowParallel.LastWorkerCount} rounds={rounds}");
        Console.WriteLine($"SHA-256[..16] = {sha[..16]}");
        if (width == 1920 && height == 1080 && s == 3 && p == 1 && h == 0.1f && sigma == 0f)
            Console.WriteLine($"byte-identity anchor: {(sha[..16] == Anchor1080p ? "MATCH" : "*** MISMATCH ***")} " +
                              $"(expected {Anchor1080p})");

        var times = new List<double>();
        for (var r = 0; r < rounds; r++)
        {
            var t = Stopwatch.GetTimestamp();
            using var result = NonLocalMeansOps.NonLocalMeans(view, s, p, h, sigma);
            times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        times.Sort();
        Console.WriteLine($"times(ms): {string.Join(", ", times.Select(t => t.ToString("F0", CultureInfo.InvariantCulture)))}");
        Console.WriteLine($"min={times[0]:F0} ms   median={times[times.Count / 2]:F0} ms   " +
                          $"max={times[^1]:F0} ms");
    }

    private static void ParseSize(string value, ref int w, ref int h)
    {
        var parts = value.Split('x');
        w = int.Parse(parts[0], CultureInfo.InvariantCulture);
        h = int.Parse(parts[1], CultureInfo.InvariantCulture);
    }
}
