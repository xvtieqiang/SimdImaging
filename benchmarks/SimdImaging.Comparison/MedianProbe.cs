using System.Diagnostics;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Comparison;

/// <summary>
/// Median3x3 measured across worker counts, straight through the operator and with the output hashed at each count.
/// If the hashes agree, the timing difference is a scheduling effect; if they do not, the fast reading was a fast
/// wrong answer.
/// </summary>
internal static class MedianProbe
{
    public static void Run(int width, int height, int rounds)
    {
        var data = Fixture.Make(width, height);
        using var image = Fixture.Ours(data, width, height);
        var view = image[0].Pixels.View;

        string? reference = null;
        foreach (var degree in new[] { 1, 2, 4, 8, 16 })
        {
            RowParallel.DegreeOverride = degree;

            var times = new List<double>();
            string hash = string.Empty;
            for (var i = 0; i < rounds + 2; i++)
            {
                var clock = Stopwatch.StartNew();
                using (var result = FilterOps.Median3x3(view, null))
                {
                    clock.Stop();
                    if (i >= 2)
                    {
                        times.Add(clock.Elapsed.TotalMilliseconds);
                        hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(result.View.Span))[..12];
                    }
                }
            }

            times.Sort();
            reference ??= hash;
            var mark = hash == reference ? string.Empty : "  ← 与其他并行度不同";
            Console.WriteLine($"degree={degree,2}  中位 {times[times.Count / 2],7:F2} ms   最小 {times[0],7:F2}   {hash}{mark}");
        }

        // Three ways to ask for the same operator, in one process, so a difference cannot be the machine.
        RowParallel.DegreeOverride = 0;
        var plan = new ProcessingPlan().Add(PlanOps.Median3x3());
        foreach (var (name, body) in new (string, Action)[]
        {
            ("Median 直接", () => { using var r = FilterOps.Median3x3(view, null); }),
            ("Median Pipe", () => { using var r = image.Pipe().Median3x3().ToImage(); }),
            ("Emboss 直接", () => { using var r = FilterOps.Emboss(view, 0.6f, null); }),
            ("Emboss Pipe", () => { using var r = image.Pipe().Emboss(0.6f).ToImage(); }),
            ("Erode 直接", () => { using var r = FilterOps.Erode3x3(view, null); }),
            ("Erode Pipe", () => { using var r = image.Pipe().Erode3x3().ToImage(); }),
            ("Sharpen 直接", () => { using var r = FilterOps.Sharpen(view, 0.6f, 1f, null); }),
            ("Sharpen Pipe", () => { using var r = image.Pipe().Sharpen(0.6f).ToImage(); }),
            ("Vignette 直接", () => { using var r = FilterOps.Vignette(view, 0.75f, 1f, null); }),
            ("Glow 直接", () => { using var r = FilterOps.Glow(view, 0.7f, 8f, 0.6f, null); }),
            ("Median r=3 直接", () => { using var r = FilterOps.Median(view, 3, null); }),
            ("Bilateral 直接", () => { using var r = FilterOps.Bilateral(view, 2, 0f, 0.1f, null); }),
            ("BokehBlur 直接", () => { using var r = FilterOps.BokehBlur(view, 8, null); }),
            ("Laplacian 直接", () => { using var r = FilterOps.Laplacian(view, false, null); }),
            ("BoxBlur r=2", () => { using var r = FilterOps.BoxBlur(view, 2, null); }),
            ("GaussianBlur σ=2", () => { using var r = FilterOps.GaussianBlur(view, 2f, null); }),
            ("Convolve 5x5", () => { var k = new float[25]; Array.Fill(k, 0.04f); using var r = FilterOps.Convolve(view, k, 2, BorderMode.Clamp, 0f, 0f, null); }),
        })
        {
            var times = new List<double>();
            for (var i = 0; i < rounds + 2; i++)
            {
                var clock = Stopwatch.StartNew();
                body();
                clock.Stop();
                if (i >= 2) times.Add(clock.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"  {name,-16} 中位 {times[times.Count / 2],7:F2} ms   最小 {times[0],7:F2}");
        }

        // The float median is the 16-bit path; Rgba8 with radius > 1 takes the sliding histogram instead.
        using var wide = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba16);
        var widePixels = wide.WritablePixels;
        for (var i = 0; i < widePixels.Length; i++)
            widePixels[i] = (byte)(i * 31);
        var wideView = wide[0].Pixels.View;

        string wideHash = string.Empty;
        var wideTimes = new List<double>();
        for (var i = 0; i < rounds + 2; i++)
        {
            var clock = Stopwatch.StartNew();
            using (var r = FilterOps.Median(wideView, 3, null))
            {
                clock.Stop();
                if (i >= 2)
                {
                    wideTimes.Add(clock.Elapsed.TotalMilliseconds);
                    wideHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(r.View.Span))[..16];
                }
            }
        }
        wideTimes.Sort();
        Console.WriteLine($"  {"16 位中值 r=3",-14} 中位 {wideTimes[wideTimes.Count / 2],7:F2} ms   最小 {wideTimes[0],7:F2}   {wideHash}");

        // Byte-identity baselines for the two-input combines.
        foreach (var (threshold, sigma, amount) in new[] { (0.6f, 6f, 0.8f), (0.7f, 2f, 0.5f) })
        {
            using var glow = FilterOps.Glow(view, threshold, sigma, amount, null);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(glow.View.Span))[..16];
            Console.WriteLine($"  Glow {threshold}/{sigma}/{amount} {hash}");
        }

        // Byte-identity baseline for the sharpen combine.
        foreach (var (amount, sigma) in new[] { (0.6f, 1f), (1f, 1.5f), (0.3f, 2f) })
        {
            using var sharp = FilterOps.Sharpen(view, amount, sigma, null);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sharp.View.Span))[..16];
            Console.WriteLine($"  Sharpen {amount}/{sigma} {hash}");
        }

        // Byte-identity baseline for the convolution rewrite: the same kernel through every border mode.
        var k5 = new float[25];
        Array.Fill(k5, 0.04f);
        foreach (var border in new[] { BorderMode.Clamp, BorderMode.Mirror, BorderMode.Wrap, BorderMode.Constant })
        {
            using var result = FilterOps.Convolve(view, k5, 2, border, 0.01f, 0.25f, null);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(result.View.Span))[..16];
            Console.WriteLine($"  Convolve {border,-8} {hash}");
        }
    }
}