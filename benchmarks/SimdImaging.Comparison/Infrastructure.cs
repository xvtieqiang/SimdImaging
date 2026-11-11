using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SimdImaging.Abstractions;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>Mirrors SimdCodecs' benchmark infrastructure: in-process interleaved timing, warm-up
/// past tier-0, per-contestant medians, and managed allocation accounting.</summary>
internal static class Env
{
    public static readonly string Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string MachineLine =>
        $"{RuntimeInformation.OSDescription} | {RuntimeInformation.ProcessArchitecture} | " +
        $"{RuntimeInformation.FrameworkDescription} | avx512={Vector512.IsHardwareAccelerated} " +
        $"avx2={Avx2.IsSupported} v128={Vector128.IsHardwareAccelerated}";

    public static string OracleVersion =>
        typeof(SixLabors.ImageSharp.Image).Assembly.GetCustomAttributes(false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";
}

internal static class Paths
{
    public static readonly string Root = FindRoot();
    public static string Results => Path.Combine(Root, "results");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SimdImaging.Comparison.csproj")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Cannot locate SimdImaging.Comparison.csproj");
    }
}

internal static class Fixture
{
    /// <summary>Both sides run at their REAL default parallelism. Pinning the oracle to one thread flattered
    /// our own numbers; `SIMDIMAGING_COMPARE_PARALLEL=0` only reproduces that historical single-thread A/B.
    /// Our side is not single-threaded either: `RowParallel.Degree` defaults to ProcessorCount.</summary>
    public static readonly Configuration OracleConfiguration = CreateConfiguration();

    private static Configuration CreateConfiguration()
    {
        var configuration = Configuration.Default.Clone();
        // Default is REAL capability: pinning the oracle to one thread flattered our own numbers.
        // Set SIMDIMAGING_COMPARE_PARALLEL=0 only to reproduce the historical single-thread A/B.
        if (Environment.GetEnvironmentVariable("SIMDIMAGING_COMPARE_PARALLEL") == "0")
            configuration.MaxDegreeOfParallelism = 1;
        return configuration;
    }

    /// <summary>Deterministic synthetic raster. Self-generated, so no third-party corpus enters the repo.</summary>
    public static byte[] Make(int width, int height)
    {
        var data = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            data[i] = (byte)((x * 3 + y * 5) & 0xFF);
            data[i + 1] = (byte)((x * x / 7 + y * 11) & 0xFF);
            data[i + 2] = (byte)(((x ^ y) * 7 + 128) & 0xFF);
            // 200..255 only. The old `200 + (x & 0x3F)` wrapped through byte overflow and produced
            // near-transparent pixels (alpha 0..7); unpremultiplying those amplified tiny colour
            // differences into 255-level spikes in the resize parity table. That was a fixture bug.
            data[i + 3] = (byte)(200 + x % 56);
        }

        return data;
    }

    public static OurImage Ours(byte[] data, int width, int height)
    {
        var image = OurImage.Create(width, height, WorkingFormat.Rgba8);
        data.AsSpan().CopyTo(image.WritablePixels);
        return image;
    }

    public static TheirImage Theirs(byte[] data, int width, int height) =>
        SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(OracleConfiguration, data, width, height);

    public static byte[] Bytes(OurImage image) => image.WritablePixels.ToArray();

    public static byte[] Bytes(TheirImage image)
    {
        var buffer = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(buffer);
        return buffer;
    }
}

internal static class Metrics
{
    public readonly record struct Parity(long ExactChannels, int MaxChannelDiff, double MeanAbsDiff,
        double PsnrRgb, long DifferentPixels);

    /// <summary>PSNR over RGB, alpha ignored — the same definition SimdCodecs uses for oracle parity.</summary>
    public static Parity Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return new Parity(0, 255, double.NaN, double.NaN, -1);
        long exact = 0, sq = 0, sum = 0, different = 0;
        var max = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var pixelDiffers = false;
            for (var ch = 0; ch < 3; ch++)
            {
                var d = Math.Abs(a[i + ch] - b[i + ch]);
                if (d == 0) exact++;
                else pixelDiffers = true;
                if (d > max) max = d;
                sum += d;
                sq += (long)d * d;
            }

            if (pixelDiffers) different++;
        }

        var channels = a.Length / 4 * 3;
        var mean = (double)sum / channels;
        var psnr = sq == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / ((double)sq / channels));
        return new Parity(exact, max, mean, psnr, different);
    }

    public static string Grade(in Parity p, long totalPixels)
    {
        if (p.DifferentPixels < 0) return "尺寸不一致";
        if (p.DifferentPixels == 0) return "逐字节一致";
        if (p.MaxChannelDiff <= 1) return "≤1 LSB";
        if (double.IsPositiveInfinity(p.PsnrRgb) || p.PsnrRgb >= 40) return "视觉等价 (PSNR≥40)";
        if (p.PsnrRgb >= 30) return "接近 (PSNR≥30)";
        if (p.PsnrRgb >= 20) return "偏差明显 (PSNR≥20)";
        return "算法/语义不同";
    }
}

internal sealed record Runner(string Name, Action Run);

internal static class Timing
{
    /// <summary>Warm every contestant for at least 300 ms, then interleave rounds so machine drift lands
    /// on both alike. Returns the median per contestant. Copied from SimdCodecs' Infrastructure.Timing.</summary>
    public static Dictionary<string, double> Time(IReadOnlyList<Runner> runners, int rounds = 7,
        Dictionary<string, long>? allocated = null, double sampleMs = 30)
    {
        foreach (var runner in runners)
        {
            var warmStart = Stopwatch.GetTimestamp();
            for (var i = 0; i < 3 || Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds < 300; i++)
                runner.Run();
            if (allocated is not null)
            {
                // Process-wide, not current-thread: under parallelism the work happens on pool threads, so
                // GetAllocatedBytesForCurrentThread only saw a fraction of either side and made the opponent
                // look allocation-free. The two runners alternate, so background allocation is symmetric.
                var before = GC.GetTotalAllocatedBytes(precise: false);
                runner.Run();
                allocated[runner.Name] = GC.GetTotalAllocatedBytes(precise: false) - before;
            }
        }

        var samples = runners.ToDictionary(r => r.Name, _ => new List<double>());
        for (var round = 0; round < rounds; round++)
        foreach (var runner in runners)
        {
            var ops = 0;
            var start = Stopwatch.GetTimestamp();
            do
            {
                runner.Run();
                ops++;
            } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < sampleMs);
            samples[runner.Name].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / ops);
        }

        return samples.ToDictionary(kv => kv.Key, kv =>
        {
            kv.Value.Sort();
            return kv.Value[kv.Value.Count / 2];
        });
    }

    public static string F(double value) => value.ToString(value < 1 ? "F4" : value < 10 ? "F3" : "F1",
        CultureInfo.InvariantCulture);

    public static string Ratio(double ours, double theirs) =>
        (theirs / ours).ToString("F2", CultureInfo.InvariantCulture) + "×";
}

internal sealed class Report
{
    private readonly StringBuilder _sb = new();

    public void Line(string text = "") => _sb.AppendLine(text);
    public void Heading(int level, string text) => Line(new string('#', level) + " " + text);

    public void Table(string[] header, IEnumerable<string[]> rows)
    {
        Line("| " + string.Join(" | ", header) + " |");
        Line("|" + string.Join("|", header.Select(_ => "---")) + "|");
        foreach (var row in rows) Line("| " + string.Join(" | ", row) + " |");
        Line();
    }

    public string Text => _sb.ToString();

    public string Save(string name)
    {
        Directory.CreateDirectory(Paths.Results);
        var path = Path.Combine(Paths.Results, name);
        File.WriteAllText(path, _sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
