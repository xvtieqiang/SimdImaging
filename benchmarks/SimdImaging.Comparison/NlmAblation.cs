using System.Diagnostics;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// Step 1 of the non-local means optimisation: where does the inner loop actually spend its time?
/// <para>
/// A faithful, single-threaded transcription of <see cref="NonLocalMeansOps"/>' Rgba8 per-pixel loop
/// (the 1080p / S=3 / P=1 / h=0.1 benchmark path, same fixture, same ring layout). ONE component at a
/// time is replaced by a live cheap stand-in; nothing else changes. Each stand-in deliberately keeps the
/// component's inputs alive so the JIT cannot dead-code-eliminate the upstream SSD: stubbing exp with a
/// constant would make <c>distance</c> dead and silently delete the entire patch loop, which is the classic
/// way this measurement lies.
/// </para>
/// <para>
/// Schablone follows the repo's own ablations (<c>NearestAblation</c>, <c>MatrixRecordsAblation</c>):
/// replicate only the inner loop, prove the full variant is byte-identical to the shipped operator, then
/// time with <see cref="Timing.Time"/> (interleaved rounds, median). No shipped code, no default parameter
/// and no public API is touched. This is a measurement, not an optimisation claim and not a second ruler.
/// </para>
/// </summary>
/// <remarks>
/// Four requested cost centres, mapped to the real loop:
/// <list type="bullet">
/// <item>patch SSD summation — the squared-difference multiply-add in <c>Ssd</c>.</item>
/// <item>square root — <b>absent</b>: the weight is <c>exp(-max(SSD*normalizer-2σ²,0)/h²)</c>, no sqrt.</item>
/// <item>exponential — the <c>Math.Exp</c> call, one per candidate.</item>
/// <item>memory access — the scattered patch row reads from the ring.</item>
/// </list>
/// </remarks>
internal static class NlmAblation
{
    // Stand-in flags. Every one keeps data flowing so upstream work stays live:
    //   StubExp : Exp(e) -> 1 - e          (still consumes e, hence the SSD and its loads).
    //   StubSsd : d*d sum -> d sum         (still loads, still subtracts, still feeds e; drops 4 multiplies).
    //   StubMem : patch reads come from two hot L1 rows instead of the scattered ring rows; the clamp,
    //             span arithmetic, subtraction and squaring are all unchanged.
    private const int StubExp = 1;
    private const int StubSsd = 2;      // fine: squared differences -> plain difference sum (loads kept)
    private const int StubMem = 4;      // patch reads -> two hot L1 rows (arithmetic kept)
    private const int StubSsdScan = 8;  // the WHOLE patch SSD scan -> one O(1) value (rolling-SSD ceiling)
    // exp + whole-scan: the scan already subsumes the memory reads and the squaring, so -all is the
    // "optimise both exp and the patch scan" floor and the additivity check is (exp + scan) vs -all.
    private const int StubAll = StubExp | StubSsdScan;

    private const int W = 1920, H = 1080, S = 3, P = 1;
    private const float HParam = 0.1f, Sigma = 0f;
    private const int Stride = W * 4;
    private const int Support = S + P;
    private const int RingRows = 2 * Support + 1;

    public static void Run()
    {
        // The exact NLM fixture SimdImaging.Benchmarks uses, so exp argument magnitudes match the report.
        var src = new byte[W * H * 4];
        for (var i = 0; i < src.Length; i++)
            src[i] = (byte)((i * 37 + 11) & 0xFF);

        using var image = OurImage.Create(W, H, WorkingFormat.Rgba8);
        src.AsSpan().CopyTo(image.WritablePixels);

        // Shipped operator: absolute baseline + the parallelism diagnostic. For(0, height) passes a zero
        // work width, which trips RowParallel.For's 64K-pixel floor and runs this operator single-threaded.
        var sw = Stopwatch.StartNew();
        using var shipped = NonLocalMeansOps.NonLocalMeans(image[0].Pixels.View, S, P, HParam, Sigma);
        sw.Stop();
        var shippedBytes = shipped.View.Span.ToArray();
        Console.WriteLine($"machine: {Env.MachineLine}");
        Console.WriteLine($"shipped 1080p S={S} P={P} h={HParam} sigma={Sigma}: " +
                          $"{sw.Elapsed.TotalMilliseconds:F0} ms, LastWorkerCount={RowParallel.LastWorkerCount}");

        // Preallocated working set: no timed path allocates, so GC never enters a cell.
        var dst = new byte[W * H * 4];
        var ring = new byte[RingRows * Stride];
        var output = new float[W * 4];
        var hotA = new byte[Stride];
        var hotB = new byte[Stride];
        for (var i = 0; i < Stride; i++)
        {
            hotA[i] = (byte)((i * 31 + 7) & 0xFF);
            hotB[i] = (byte)((i * 17 + 200) & 0xFF);
        }

        var patchPixels = (2 * P + 1) * (2 * P + 1);
        var format = WorkingFormat.Rgba8;
        var maxCode = Pixels.MaxCode(format);
        var normalizer = 1.0 / (patchPixels * 4 * maxCode * maxCode);
        var hSquared = (double)HParam * HParam;
        var twoSigmaSquared = 2.0 * Sigma * Sigma;

        // Faithful transcription of Accumulate + SsdRgba8, threaded with the ablation mode. Single band
        // (0..H), the same ring seeding and modulo indexing as the shipped For(0, height) path.
        void Replica(int mode)
        {
            var computed = -1;
            for (var y = 0; y < H; y++)
            {
                var last = Math.Min(y + Support, H - 1);
                while (computed < last)
                {
                    computed++;
                    src.AsSpan(computed * Stride, Stride)
                       .CopyTo(ring.AsSpan((computed % RingRows) * Stride, Stride));
                }

                for (var x = 0; x < W; x++)
                {
                    double sumR = 0, sumG = 0, sumB = 0, sumA = 0, sumW = 0;
                    for (var dy = -S; dy <= S; dy++)
                    {
                        var sy = Math.Clamp(y + dy, 0, H - 1);
                        var candidateRow = ring.AsSpan((sy % RingRows) * Stride, Stride);
                        for (var dx = -S; dx <= S; dx++)
                        {
                            var sx = Math.Clamp(x + dx, 0, W - 1);
                            var distance = Ssd(mode, x, y, sx, sy);
                            var e = -Math.Max(distance * normalizer - twoSigmaSquared, 0.0) / hSquared;
                            var weight = (mode & StubExp) != 0 ? 1.0 - e : Math.Exp(e);
                            var off = sx * 4;
                            var r = candidateRow[off] * (1f / Pixels.MaxCode8);
                            var g = candidateRow[off + 1] * (1f / Pixels.MaxCode8);
                            var b = candidateRow[off + 2] * (1f / Pixels.MaxCode8);
                            var a = candidateRow[off + 3] * (1f / Pixels.MaxCode8);
                            sumR += r * weight;
                            sumG += g * weight;
                            sumB += b * weight;
                            sumA += a * weight;
                            sumW += weight;
                        }
                    }

                    var o = x * 4;
                    output[o] = (float)(sumR / sumW);
                    output[o + 1] = (float)(sumG / sumW);
                    output[o + 2] = (float)(sumB / sumW);
                    output[o + 3] = (float)(sumA / sumW);
                }

                Pixels.WriteRowRaw(output, W, format, dst.AsSpan(y * Stride, Stride));
            }
        }

        int Ssd(int mode, int ax, int ay, int bx, int by)
        {
            // Whole-scan stand-in: O(1), data-dependent (so Math.Exp is still called with a varying
            // argument 49x/pixel and cannot be CSE'd away), and non-negative. This is exactly the work
            // rolling SSD (an integral image of squared differences per offset) would delete.
            if ((mode & StubSsdScan) != 0)
                return (int)(((uint)(ax * 31 + ay * 17 + bx * 7 + by * 3) & 1023) * 100);

            var sum = 0;
            for (var py = -P; py <= P; py++)
            {
                ReadOnlySpan<byte> rowA, rowB;
                if ((mode & StubMem) != 0)
                {
                    rowA = hotA;
                    rowB = hotB;
                }
                else
                {
                    rowA = ring.AsSpan((Math.Clamp(ay + py, 0, H - 1) % RingRows) * Stride, Stride);
                    rowB = ring.AsSpan((Math.Clamp(by + py, 0, H - 1) % RingRows) * Stride, Stride);
                }

                for (var px = -P; px <= P; px++)
                {
                    var ia = Math.Clamp(ax + px, 0, W - 1) * 4;
                    var ib = Math.Clamp(bx + px, 0, W - 1) * 4;
                    var d0 = rowA[ia] - rowB[ib];
                    var d1 = rowA[ia + 1] - rowB[ib + 1];
                    var d2 = rowA[ia + 2] - rowB[ib + 2];
                    var d3 = rowA[ia + 3] - rowB[ib + 3];
                    sum += (mode & StubSsd) != 0
                        ? d0 + d1 + d2 + d3
                        : d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
                }
            }
            return sum;
        }

        // Correctness gate: the full replica must reproduce the product operator byte for byte. If it does
        // not, every timing below describes a different computation and must not be trusted.
        Replica(0);
        var identical = dst.AsSpan().SequenceEqual(shippedBytes);
        Console.WriteLine($"full replica byte-identical to shipped: {identical}  " +
                          $"(replica SHA {Metrics.Sha256(dst)[..16]}, shipped SHA {Metrics.Sha256(shippedBytes)[..16]})");
        if (!identical)
        {
            Console.WriteLine("ABORT: the replica is not faithful; no ablation number below is meaningful.");
            return;
        }

        var runners = new[]
        {
            new Runner("full (baseline)", () => Replica(0)),
            new Runner("-exp", () => Replica(StubExp)),
            new Runner("-ssd-scan", () => Replica(StubSsdScan)),
            new Runner("-ssd-sum", () => Replica(StubSsd)),
            new Runner("-mem", () => Replica(StubMem)),
            new Runner("-all (sum check)", () => Replica(StubAll)),
        };
        var times = Timing.Time(runners, rounds: 3);

        var full = times["full (baseline)"];
        var dExp = full - times["-exp"];
        var dSsdSum = full - times["-ssd-sum"];
        var dScan = full - times["-ssd-scan"];
        var dMem = full - times["-mem"];
        var dAll = full - times["-all (sum check)"];

        Console.WriteLine();
        Console.WriteLine("=== NLM inner-loop ablation | 1080p, S=3, P=1, h=0.1, sigma=0 | Rgba8 ===");
        Console.WriteLine($"{"variant",-22} {"ms",10} {"d vs full",12} {"share",8}");
        foreach (var r in runners)
        {
            var d = full - times[r.Name];
            Console.WriteLine($"{r.Name,-22} {Timing.F(times[r.Name]),10} " +
                              $"{Timing.F(d),12} {Pct(d, full),8}");
        }

        Console.WriteLine();
        Console.WriteLine("--- four requested cost centres (single-stand-in delta / full) ---");
        Console.WriteLine($"{"patch SSD 求和 = 整个 patch 扫描",-36} {Timing.F(dScan),10} ms   {Pct(dScan, full)}   [滚动 SSD 天花板]");
        Console.WriteLine($"{"  └ 其中平方乘加算术",-36} {Timing.F(dSsdSum),10} ms   {Pct(dSsdSum, full)}");
        Console.WriteLine($"{"平方根 sqrt",-36} {"0",10} ms   0.0%   — 本实现无 sqrt: weight=exp(-SSD*norm/h^2)");
        Console.WriteLine($"{"指数 expf (Math.Exp)",-36} {Timing.F(dExp),10} ms   {Pct(dExp, full)}   [权重表天花板]");
        Console.WriteLine($"{"内存访问 (patch 行读)",-36} {Timing.F(dMem),10} ms   {Pct(dMem, full)}   [行带分块天花板]");

        Console.WriteLine();
        Console.WriteLine("--- step-2 option ceilings (full minus a variant that makes that target free) ---");
        Console.WriteLine($"{"权重表 (去 exp)",-32} {Pct(dExp, full),7}  -> {Timing.F(times["-exp"])} ms");
        Console.WriteLine($"{"滚动 SSD (去整个 patch 扫描)",-32} {Pct(dScan, full),7}  -> {Timing.F(times["-ssd-scan"])} ms");
        Console.WriteLine($"{"行带分块 (去散访存)",-32} {Pct(dMem, full),7}  -> {Timing.F(times["-mem"])} ms");

        var sumExpScan = dExp + dScan;
        Console.WriteLine();
        Console.WriteLine($"Sigma(exp + whole-scan) = {Timing.F(sumExpScan)} ms ; -all = {Timing.F(dAll)} ms ; " +
                          $"gap = {(dAll > 0 ? (sumExpScan - dAll) / dAll * 100.0 : 0).ToString("F1", CultureInfo.InvariantCulture)}% ; " +
                          $"total removable = {Pct(dAll, full)} of full");
        var mainCause = dScan >= dExp && dScan >= dMem ? "patch SSD 求和 / 整个 patch 扫描 -> 滚动 SSD"
            : dExp >= dMem ? "指数 expf -> 权重表" : "内存访问 -> 行带分块";
        Console.WriteLine($"MAIN CAUSE: {mainCause}  " +
                          $"(scan={Timing.F(dScan)} / exp={Timing.F(dExp)} / mem={Timing.F(dMem)} ms)");
        Console.WriteLine();
        Console.WriteLine("Attribution note: the three single-stand-in deltas OVERLAP (the patch scan contains");
        Console.WriteLine("both the memory reads and the squaring), so they are ceilings, not a partition.");
        Console.WriteLine("The whole-scan stand-in still calls Math.Exp 49x/pixel and still reads the candidate");
        Console.WriteLine("centre, so it measures exactly what rolling SSD would remove, with no DCE of the rest.");
        Console.WriteLine("sqrt is reported, not timed: there is no Math.Sqrt in the shipped inner loop, so a");
        Console.WriteLine("sqrt stand-in would be byte-identical to full and its delta is 0 by construction.");
        Console.WriteLine("Run twice: a single cell that moves 2x between runs is not a number.");
    }

    private static string Pct(double part, double full) =>
        (full > 0 ? part / full * 100.0 : 0).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
