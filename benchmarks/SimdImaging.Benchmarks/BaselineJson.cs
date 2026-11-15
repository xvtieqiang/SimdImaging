using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Benchmarks;

/// <summary>
/// Writes a run in the versioned-baseline format so <c>check-baseline.sh</c> can compare like with like.
/// <para>
/// Hand-rolled rather than <c>JsonSerializer</c>: reflection-based serialisation raises a trimming warning
/// and requires dynamic code, and this project is built with the AOT analyzers on. The shapes here are six
/// scalars and a list of eight, so a builder is both smaller and honest about what it emits.
/// </para>
/// <para>
/// The machine id is deliberately coarse — OS, architecture, core count — and the comparison also requires
/// the same binary hash: a number is only comparable with itself.
/// </para>
/// <para>
/// Since 2026-11-15 the file also carries the <c>calibration</c> block. The check divides each scenario by
/// the probe scale, because the machine itself drifts 1.2–1.8× within a day and an absolute reading cannot
/// tell that drift apart from a regression (<c>Calibration.cs</c> has the measurement that established it).
/// </para>
/// </summary>
internal static class BaselineJson
{
    public static void Write(string path, IReadOnlyList<Result> results, int repeats, bool slow,
        CalibrationResult calibration)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine($"  \"machine\": \"{Escape(Machine)}\",");
        builder.AppendLine($"  \"date\": \"{LocalDate()}\",");
        builder.AppendLine($"  \"gitSha\": \"{Escape(Environment.GetEnvironmentVariable("SIMDIMAGING_GIT_SHA") ?? "unknown")}\",");
        builder.AppendLine($"  \"binarySha256\": \"{Sha256Of(typeof(ProcessingPlan))}:{Sha256Of(typeof(PixelView))}\",");
        builder.AppendLine($"  \"protocol\": \"best-of-N warm single process; SimdImaging.Benchmarks; slow={(slow ? 1 : 0)}; norm=max(mem,alu)\",");
        builder.AppendLine($"  \"repeats\": {repeats},");
        builder.AppendLine("  \"calibration\": { " +
                           $"\"memory\": {calibration.MemoryMs.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                           $"\"alu\": {calibration.AluMs.ToString("0.###", CultureInfo.InvariantCulture)} }},");
        builder.AppendLine("  \"entries\": [");

        for (var i = 0; i < results.Count; i++)
        {
            var entry = results[i];
            builder.Append("    { ");
            builder.Append($"\"scenario\": \"{Escape(entry.Scenario)}\", ");
            builder.Append("\"size\": \"as named\", ");
            builder.Append("\"metric\": \"milliseconds\", ");
            builder.Append($"\"value\": {entry.Milliseconds.ToString("0.###", CultureInfo.InvariantCulture)}, ");
            builder.Append("\"unit\": \"ms\", ");
            builder.Append("\"thresholdPercent\": null, ");
            builder.Append($"\"megapixelsPerSecond\": {entry.MegapixelsPerSecond.ToString("0.##", CultureInfo.InvariantCulture)}, ");
            builder.Append($"\"allocatedBytes\": {entry.AllocatedBytes}");
            builder.Append(i == results.Count - 1 ? " }" : " },");
            builder.AppendLine();
        }

        builder.AppendLine("  ]");
        builder.AppendLine("}");
        File.WriteAllText(path, builder.ToString());
        Console.WriteLine($"json: {path} ({results.Count} scenarios)");
    }

    /// <summary>The identity a baseline is keyed on. Documented in <c>benchmarks/README.md</c>.</summary>
    public static string Machine =>
        $"{RuntimeInformation.OSDescription}|{RuntimeInformation.ProcessArchitecture}|{Environment.ProcessorCount}";

    /// <summary>
    /// Local calendar date, not UTC: a recording made at 07:00 +08:00 is not "yesterday", and a baseline
    /// file whose date contradicts the day it was taken is a trap for whoever reads the ledger next.
    /// </summary>
    private static string LocalDate() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Sha256Of(Type type)
    {
        // Not Assembly.Location: it is empty for a single-file publish, which the trimming analyser rightly
        // flags even though a benchmark is never published that way.
        var path = Path.Combine(AppContext.BaseDirectory, type.Assembly.GetName().Name + ".dll");
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>One timed scenario, in the shape the versioned baseline stores.</summary>
internal sealed record Result(string Scenario, double Milliseconds, double MegapixelsPerSecond, long AllocatedBytes);
