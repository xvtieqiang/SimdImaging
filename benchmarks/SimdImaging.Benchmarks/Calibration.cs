using System.Diagnostics;

namespace SimdImaging.Benchmarks;

/// <summary>
/// Two probes that measure the machine, not the library.
/// <para>
/// A stopwatch reading is only comparable with another reading taken in the same machine state. That state
/// drifts: the same binary measured 1.5× apart within four hours on the development box, and the black-box
/// reference (ImageSharp, untouched by our code) drifted with it — so the drift is the machine, not a
/// regression. Dividing each scenario by a probe that slows down by the same factor cancels it.
/// </para>
/// <para>
/// Both probes must stay independent of <c>SimdImaging</c>: if they called our operators, a real regression
/// in the code under test would move the probe too and cancel itself out. They use <c>Parallel.For</c>
/// over <see cref="Environment.ProcessorCount"/> workers because the scenarios are row-band parallel, and a
/// single-threaded probe would not see contention from the same cores.
/// </para>
/// </summary>
internal static class Calibration
{
    private const int MemoryBufferBytes = 256 * 1024 * 1024;
    private const int AluRounds = 32_000_000;
    private const int ProbeRepeats = 7;

    /// <summary>Times both probes with the same warm-up + best-of-N protocol the scenarios use.</summary>
    public static CalibrationResult Measure(int iterations)
    {
        var buffer = new ulong[MemoryBufferBytes / sizeof(ulong)];
        for (var i = 0; i < buffer.Length; i += 512)
            buffer[i] = (ulong)i;                      // fault the pages in before timing reads

        // The probes cost milliseconds, the scenarios seconds: paying for more repeats here is nearly free
        // and is the only way the correction is steadier than the noise it is meant to remove.
        var repeats = Math.Max(iterations, ProbeRepeats);

        return new CalibrationResult(
            Math.Round(Time(() => MemoryPass(buffer), repeats), 3),
            Math.Round(Time(AluPass, repeats), 3));
    }

    private static double Time(Func<long> body, int iterations)
    {
        body();                                        // warm-up: JIT and thread-pool ramp
        var best = double.MaxValue;
        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew();
            var sink = body();
            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            GC.KeepAlive(sink);
        }

        return best;
    }

    /// <summary>Streams 256 MB past the last-level caches and sums it: a bandwidth proxy.</summary>
    private static long MemoryPass(ulong[] buffer)
    {
        var workers = Environment.ProcessorCount;
        var chunk = (buffer.Length + workers - 1) / workers;
        long total = 0;

        Parallel.For(0, workers, worker =>
        {
            var start = worker * chunk;
            var end = Math.Min(buffer.Length, start + chunk);
            ulong sum = 0;
            for (var i = start; i < end; i++)
                sum += buffer[i];
            Interlocked.Add(ref total, (long)sum);
        });

        return total;
    }

    /// <summary>Xorshift rounds in registers: a compute proxy that touches no memory worth counting.</summary>
    private static long AluPass()
    {
        var workers = Environment.ProcessorCount;
        long total = 0;

        Parallel.For(0, workers, worker =>
        {
            var x = (uint)((worker + 1) * 2654435761u);
            ulong sum = 0;
            for (var i = 0; i < AluRounds; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                sum += x;
            }

            Interlocked.Add(ref total, (long)sum);
        });

        return total;
    }
}

/// <summary>Probe readings for one run, in milliseconds. Stored in the baseline next to the scenarios.</summary>
/// <param name="MemoryMs">Streaming-sum probe: tracks memory bandwidth and core contention.</param>
/// <param name="AluMs">Xorshift probe: tracks compute throughput.</param>
internal sealed record CalibrationResult(double MemoryMs, double AluMs);
