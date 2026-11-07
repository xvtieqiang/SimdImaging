using System.Globalization;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Internal;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// Ablation-style profiling (SimdCodecs' --ablate idea), NOT a correctness test and NOT shipped code.
/// Three questions, answered with numbers instead of guesses:
///   1. Does the slow operator scale with worker count, or is something serialising it?
///   2. Which part of the per-pixel loop costs the time: memory, LUT loads, float adds, or ToByte?
///   3. Is the separable blur's band height near its optimum?
/// </summary>
internal static class Profile
{
    private static readonly int[] Degrees = [1, 2, 4, 8, 16];
    private static readonly int[] BandRows = [64, 128, 256, 512, 1024];

    public static void Write(Report report, int width, int height)
    {
        var data = Fixture.Make(width, height);
        using var ours = Fixture.Ours(data, width, height);

        report.Heading(2, "瓶颈剖析（仅计时归因，不参与交付判定）");
        report.Line($"> 输入 {width}×{height} `Rgba8`；每个数字是 ≥300 ms 热身 + 5 轮交错采样的中位数。");
        report.Line();

        BlurProfile.Write(report, width, height);
        ParallelOverhead.Write(report, width, height);

        DegreeScan(report, ours, width, height);
        KernelBreakdown(report, data, width, height);
        MatrixSchemeBenchmark(report, data, width, height);
        BandScan(report, ours);
        GatherBenchmark(report, width, height);
        ColumnBlockBenchmark(report, width, height);
        WorkerGranularityScan(report, ours, width, height);
        MatrixPathCrossCheck(report, ours);
        CallOverheadBreakdown(report, ours, width, height);

        RowParallel.DegreeOverride = 0;
        RowParallel.RowsPerWorkerOverride = 0;
        FilterOps.BandRowsOverride = 0;
    }

    private static double TimeOncePerCall(Action action, int rounds = 5)
    {
        var runners = new[] { new Runner("only", action) };
        return Timing.Time(runners, rounds: rounds)["only"];
    }

    private static void DegreeScan(Report report, OurImage image, int width, int height)
    {
        report.Heading(3, "1. 并行度扫描（同一算子，1→16 workers）");
        var ops = new (string Name, Func<OurImage, OurImage> Run)[]
        {
            ("Invert", i => i.Invert()),
            ("Grayscale", i => i.Grayscale()),
            ("Sepia", i => i.Sepia()),
            ("GaussianBlur σ3", i => i.GaussianBlur(3f)),
            ("Resize bicubic", i => i.Resize(width / 2, height / 2, Resampler.Bicubic)),
        };

        var rows = new List<string[]>();
        foreach (var (name, run) in ops)
        {
            var samples = new List<double>();
            try
            {
                foreach (var degree in Degrees)
                {
                    RowParallel.DegreeOverride = degree;
                    samples.Add(TimeOncePerCall(() =>
                    {
                        using var result = run(image);
                        GC.KeepAlive(result);
                    }));
                }
            }
            finally
            {
                RowParallel.DegreeOverride = 0;
            }

            var speedup = samples[0] / samples[^1];
            rows.Add([name, .. samples.Select(Timing.F),
                speedup.ToString("F2", CultureInfo.InvariantCulture) + "×"]);
        }

        report.Table(["算子", .. Degrees.Select(d => d + " worker"), "1→16 加速"], rows);
        report.Line("> 若 1→16 加速远低于核数，瓶颈是并行扩展性/串行段；若接近线性，瓶颈是每像素指令量。");
        report.Line();
    }

    private static void KernelBreakdown(Report report, byte[] src, int width, int height)
    {
        report.Heading(3, "2. sepia 每像素循环分解（复刻内循环，逐段加回成本）");
        var dst = new byte[src.Length];
        var tables = new float[9 * 256];
        float[,] coefficients =
        {
            { 0.393f, 0.769f, 0.189f },
            { 0.349f, 0.686f, 0.168f },
            { 0.272f, 0.534f, 0.131f },
        };
        const float scale = 1f / 255f;
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        for (var v = 0; v < 256; v++)
            tables[(row * 3 + column) * 256 + v] = coefficients[row, column] * (v * scale);

        var stride = width * 4;

        void Copy(int start, int end)
        {
            for (var y = start; y < end; y++)
            {
                var o = y * stride;
                for (var i = 0; i < stride; i += 4)
                {
                    dst[o + i] = src[o + i];
                    dst[o + i + 1] = src[o + i + 1];
                    dst[o + i + 2] = src[o + i + 2];
                    dst[o + i + 3] = src[o + i + 3];
                }
            }
        }

        void Luma(int start, int end)
        {
            for (var y = start; y < end; y++)
            {
                var o = y * stride;
                for (var i = 0; i < stride; i += 4)
                {
                    var value = Pixels.ToByte(
                        ((tables[src[o + i]] + tables[256 + src[o + i + 1]]) + tables[512 + src[o + i + 2]]));
                    dst[o + i] = value;
                    dst[o + i + 1] = value;
                    dst[o + i + 2] = value;
                    dst[o + i + 3] = src[o + i + 3];
                }
            }
        }

        void MatrixNoToByte(int start, int end)
        {
            for (var y = start; y < end; y++)
            {
                var o = y * stride;
                for (var i = 0; i < stride; i += 4)
                {
                    var r = src[o + i];
                    var g = src[o + i + 1];
                    var b = src[o + i + 2];
                    var red = ((tables[r] + tables[256 + g]) + tables[512 + b]);
                    var green = ((tables[768 + r] + tables[1024 + g]) + tables[1280 + b]);
                    var blue = ((tables[1536 + r] + tables[1792 + g]) + tables[2048 + b]);
                    dst[o + i] = (byte)(red * 255f);
                    dst[o + i + 1] = (byte)(green * 255f);
                    dst[o + i + 2] = (byte)(blue * 255f);
                    dst[o + i + 3] = src[o + i + 3];
                }
            }
        }

        void FullMatrix(int start, int end)
        {
            for (var y = start; y < end; y++)
            {
                var o = y * stride;
                for (var i = 0; i < stride; i += 4)
                {
                    var r = src[o + i];
                    var g = src[o + i + 1];
                    var b = src[o + i + 2];
                    dst[o + i] = Pixels.ToByte((tables[r] + tables[256 + g]) + tables[512 + b]);
                    dst[o + i + 1] = Pixels.ToByte((tables[768 + r] + tables[1024 + g]) + tables[1280 + b]);
                    dst[o + i + 2] = Pixels.ToByte((tables[1536 + r] + tables[1792 + g]) + tables[2048 + b]);
                    dst[o + i + 3] = src[o + i + 3];
                }
            }
        }

        var kernels = new (string Name, Action<int, int> Body)[]
        {
            ("纯拷贝（带宽下限）", Copy),
            ("3 次查表 + 1 ToByte（灰度）", Luma),
            ("9 次查表 + 廉价写（无 ToByte）", MatrixNoToByte),
            ("9 次查表 + 3 ToByte（当前 sepia）", FullMatrix),
        };

        var rows = new List<string[]>();
        double? baseline = null;
        foreach (var (name, body) in kernels)
        {
            var ms = TimeOncePerCall(() => RowParallel.For(width, height, body));
            baseline ??= ms;
            rows.Add([name, Timing.F(ms),
                (ms / baseline.Value).ToString("F2", CultureInfo.InvariantCulture) + "×"]);
        }

        report.Table(["内核变体", "ms", "相对纯拷贝"], rows);
        report.Line("> 「3×查表」与「9×查表」之差定位表访问成本；「无 ToByte」与「3 ToByte」之差定位舍入/钳制成本。");
        report.Line();
    }

    /// <summary>
    /// Scheme-level question, not a micro-tune: the current matrix path lays its products out
    /// <b>coefficient-major</b> (9 separate 256-float tables), so one pixel costs nine 4-byte loads and
    /// the clamp has to be staged through a stack buffer. Laying the same products out
    /// <b>value-major</b> (one 16-byte record per input byte holding all three output-channel products)
    /// costs three 16-byte loads and keeps everything in registers. Identical arithmetic, identical
    /// order, therefore identical bytes — verified before any timing is trusted.
    /// </summary>
    private static void MatrixSchemeBenchmark(Report report, byte[] src, int width, int height)
    {
        report.Heading(3, "7. sepia 的算法形态对比：系数主序 9×4B 查表 vs 输入值主序 3×16B 记录");

        var stride = width * 4;
        var bytes = stride * height;

        float[,] coefficients =
        {
            { 0.393f, 0.769f, 0.189f },
            { 0.349f, 0.686f, 0.168f },
            { 0.272f, 0.534f, 0.131f },
        };
        const float scale = 1f / 255f;

        // Scheme A (what ships today): 9 tables of 256 floats, indexed by (output, input) then by byte.
        var tables = new float[9 * 256];
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        for (var v = 0; v < 256; v++)
            tables[(row * 3 + column) * 256 + v] = coefficients[row, column] * (v * scale);

        // Scheme B: 3 records of 256 × 4 floats; lane row of record[input][v] is that input byte's
        // contribution to output channel `row`. Padding lane stays zero.
        var records = new float[3 * 256 * 4];
        for (var input = 0; input < 3; input++)
        for (var v = 0; v < 256; v++)
        for (var row = 0; row < 3; row++)
            records[(input * 256 + v) * 4 + row] = coefficients[row, input] * (v * scale);

        var dstA = new byte[bytes];
        var dstB = new byte[bytes];

        var scaleVector = Vector128.Create(255f);
        var halfVector = Vector128.Create(0.5f);
        var maxVector = Vector128.Create(255f);
        var zeroVector = Vector128<float>.Zero;
        var offsets = Vector128.Create(0f, 0f, 0f, 0f);   // sepia has no per-row offset

        void Current(int start, int end)
        {
            Span<float> values = stackalloc float[16];
            Span<byte> packed = stackalloc byte[16];
            for (var y = start; y < end; y++)
            {
                var o = y * stride;
                var i = 0;
                for (; i + 15 < stride; i += 16)
                {
                    for (var t = 0; t < 4; t++)
                    {
                        var p = o + i + t * 4;
                        var r = src[p];
                        var g = src[p + 1];
                        var b = src[p + 2];
                        values[t * 4] = (tables[r] + tables[256 + g]) + tables[512 + b];
                        values[t * 4 + 1] = (tables[768 + r] + tables[1024 + g]) + tables[1280 + b];
                        values[t * 4 + 2] = (tables[1536 + r] + tables[1792 + g]) + tables[2048 + b];
                        values[t * 4 + 3] = 0f;
                    }

                    var q0 = Pack(values, 0, scaleVector, halfVector, maxVector, zeroVector);
                    var q1 = Pack(values, 4, scaleVector, halfVector, maxVector, zeroVector);
                    var q2 = Pack(values, 8, scaleVector, halfVector, maxVector, zeroVector);
                    var q3 = Pack(values, 12, scaleVector, halfVector, maxVector, zeroVector);
                    var low = Vector128.Narrow(q0.AsUInt32(), q1.AsUInt32());
                    var high = Vector128.Narrow(q2.AsUInt32(), q3.AsUInt32());
                    Vector128.Narrow(low, high).StoreUnsafe(ref packed[0]);
                    packed.CopyTo(dstA.AsSpan(o + i, 16));
                    for (var t = 0; t < 4; t++)
                        dstA[o + i + t * 4 + 3] = src[o + i + t * 4 + 3];
                }

                for (; i < stride; i += 4)
                {
                    var p = o + i;
                    dstA[p] = Pixels.ToByte((tables[src[p]] + tables[256 + src[p + 1]]) + tables[512 + src[p + 2]]);
                    dstA[p + 1] = Pixels.ToByte((tables[768 + src[p]] + tables[1024 + src[p + 1]]) + tables[1280 + src[p + 2]]);
                    dstA[p + 2] = Pixels.ToByte((tables[1536 + src[p]] + tables[1792 + src[p + 1]]) + tables[2048 + src[p + 2]]);
                    dstA[p + 3] = src[p + 3];
                }
            }
        }

        void Recorded(int start, int end)
        {
            Span<byte> packed = stackalloc byte[16];
            for (var y = start; y < end; y++)
            {
                var o = y * stride;
                var i = 0;
                for (; i + 15 < stride; i += 16)
                {
                    var p = o + i;
                    var m0 = Sum(records, src, p, offsets);
                    var m1 = Sum(records, src, p + 4, offsets);
                    var m2 = Sum(records, src, p + 8, offsets);
                    var m3 = Sum(records, src, p + 12, offsets);

                    var q0 = PackVector(m0, scaleVector, halfVector, maxVector, zeroVector);
                    var q1 = PackVector(m1, scaleVector, halfVector, maxVector, zeroVector);
                    var q2 = PackVector(m2, scaleVector, halfVector, maxVector, zeroVector);
                    var q3 = PackVector(m3, scaleVector, halfVector, maxVector, zeroVector);
                    var low = Vector128.Narrow(q0.AsUInt32(), q1.AsUInt32());
                    var high = Vector128.Narrow(q2.AsUInt32(), q3.AsUInt32());
                    Vector128.Narrow(low, high).StoreUnsafe(ref packed[0]);
                    packed.CopyTo(dstB.AsSpan(p, 16));
                    dstB[p + 3] = src[p + 3];
                    dstB[p + 7] = src[p + 7];
                    dstB[p + 11] = src[p + 11];
                    dstB[p + 15] = src[p + 15];
                }

                for (; i < stride; i += 4)
                {
                    var p = o + i;
                    var m = Sum(records, src, p, offsets);
                    dstB[p] = Pixels.ToByte(m.GetElement(0));
                    dstB[p + 1] = Pixels.ToByte(m.GetElement(1));
                    dstB[p + 2] = Pixels.ToByte(m.GetElement(2));
                    dstB[p + 3] = src[p + 3];
                }
            }
        }

        void Copy(int start, int end)
        {
            for (var y = start; y < end; y++)
                src.AsSpan(y * stride, stride).CopyTo(dstA.AsSpan(y * stride, stride));
        }

        RowParallel.For(width, height, Current);
        RowParallel.For(width, height, Recorded);
        var identical = dstA.AsSpan(0, bytes).SequenceEqual(dstB.AsSpan(0, bytes));

        var rows = new List<string[]>();
        try
        {
            foreach (var degree in new[] { 1, 0 })
            {
                RowParallel.DegreeOverride = degree;
                var label = degree == 1 ? "1 worker" : "默认并行";
                var copyMs = degree == 1 ? TimeOncePerCall(() => RowParallel.For(width, height, Copy)) : double.NaN;
                var currentMs = TimeOncePerCall(() => RowParallel.For(width, height, Current));
                var recordedMs = TimeOncePerCall(() => RowParallel.For(width, height, Recorded));
                rows.Add([label, degree == 1 ? Timing.F(copyMs) : "—", Timing.F(currentMs), Timing.F(recordedMs),
                    (recordedMs / currentMs).ToString("F2", CultureInfo.InvariantCulture) + "×"]);
            }
        }
        finally
        {
            RowParallel.DegreeOverride = 0;
        }

        report.Table(["并行度", "纯拷贝 ms", "系数主序（现状）ms", "输入值主序 ms", "新/旧"], rows);
        report.Line($"> 两种形态输出逐字节一致：**{identical}**。");
        report.Line("> 算术与加法顺序完全相同（`((p_r + p_g) + p_b) + offset`），变的只是表的排布和每次取多少字节。");
        report.Line();
    }

    private static Vector128<float> Sum(float[] records, byte[] src, int p, Vector128<float> offsets)
    {
        var r = Vector128.LoadUnsafe(ref records[src[p] * 4]);
        var g = Vector128.LoadUnsafe(ref records[(256 + src[p + 1]) * 4]);
        var b = Vector128.LoadUnsafe(ref records[(512 + src[p + 2]) * 4]);
        return ((r + g) + b) + offsets;
    }

    private static Vector128<int> PackVector(Vector128<float> value, Vector128<float> scale,
        Vector128<float> half, Vector128<float> max, Vector128<float> zero)
    {
        var scaled = value * scale + half;
        return Vector128.ConvertToInt32(Vector128.Max(Vector128.Min(scaled, max), zero));
    }

    private static Vector128<int> Pack(Span<float> values, int offset, Vector128<float> scale,
        Vector128<float> half, Vector128<float> max, Vector128<float> zero) =>
        PackVector(Vector128.LoadUnsafe(ref values[offset]), scale, half, max, zero);

    private static void BandScan(Report report, OurImage image)
    {
        report.Heading(3, "3. separable blur 的 band 高度扫描");
        var rows = new List<string[]>();
        try
        {
            foreach (var bandRows in BandRows)
            {
                FilterOps.BandRowsOverride = bandRows;
                var ms = TimeOncePerCall(() =>
                {
                    using var result = image.GaussianBlur(3f);
                    GC.KeepAlive(result);
                });
                rows.Add([bandRows.ToString(CultureInfo.InvariantCulture), Timing.F(ms),
                    RowParallel.LastWorkerCount.ToString(CultureInfo.InvariantCulture)]);
            }
        }
        finally
        {
            FilterOps.BandRowsOverride = 0;
        }

        report.Table(["bandRows", "gaussian ms", "workers"], rows);
    }

    private static void GatherBenchmark(Report report, int width, int height)
    {
        report.Heading(3, "4. gather 微基准：9 次标量 L1 查表 vs AVX2 gather");
        if (!Avx2.IsSupported)
        {
            report.Line("> 本机不支持 AVX2，跳过。");
            return;
        }

        var random = new Random(4242);
        var src = new byte[width * height * 4];
        random.NextBytes(src);
        var tables = new float[9 * 256];
        for (var i = 0; i < tables.Length; i++)
            tables[i] = (float)(random.NextDouble() * 0.5);

        var plane = width * height;
        var output = new float[3 * plane];
        var stride = width * 4;

        void Scalar(int start, int end)
        {
            for (var y = start; y < end; y++)
            {
                var o = y * stride;
                var d = y * width;
                for (var x = 0; x < width; x++)
                {
                    var r = src[o + x * 4];
                    var g = src[o + x * 4 + 1];
                    var b = src[o + x * 4 + 2];
                    output[d + x] = (tables[r] + tables[256 + g]) + tables[512 + b];
                    output[plane + d + x] = (tables[768 + r] + tables[1024 + g]) + tables[1280 + b];
                    output[2 * plane + d + x] = (tables[1536 + r] + tables[1792 + g]) + tables[2048 + b];
                }
            }
        }

        unsafe void Gather(int start, int end)
        {
            fixed (float* tablePtr = tables)
            fixed (float* outPtr = output)
            fixed (byte* srcPtr = src)
            {
                for (var y = start; y < end; y++)
                {
                    var o = y * stride;
                    var d = y * width;
                    var x = 0;
                    for (; x + 8 <= width; x += 8)
                    {
                        var p = o + x * 4;
                        var ri = Vector256.Create((int)srcPtr[p], (int)srcPtr[p + 4], (int)srcPtr[p + 8],
                            (int)srcPtr[p + 12], (int)srcPtr[p + 16], (int)srcPtr[p + 20],
                            (int)srcPtr[p + 24], (int)srcPtr[p + 28]);
                        var gi = Vector256.Create((int)srcPtr[p + 1], (int)srcPtr[p + 5], (int)srcPtr[p + 9],
                            (int)srcPtr[p + 13], (int)srcPtr[p + 17], (int)srcPtr[p + 21],
                            (int)srcPtr[p + 25], (int)srcPtr[p + 29]);
                        var bi = Vector256.Create((int)srcPtr[p + 2], (int)srcPtr[p + 6], (int)srcPtr[p + 10],
                            (int)srcPtr[p + 14], (int)srcPtr[p + 18], (int)srcPtr[p + 22],
                            (int)srcPtr[p + 26], (int)srcPtr[p + 30]);

                        var red = (Avx2.GatherVector256(tablePtr, ri, 4)
                                   + Avx2.GatherVector256(tablePtr + 256, gi, 4))
                                  + Avx2.GatherVector256(tablePtr + 512, bi, 4);
                        var green = (Avx2.GatherVector256(tablePtr + 768, ri, 4)
                                     + Avx2.GatherVector256(tablePtr + 1024, gi, 4))
                                    + Avx2.GatherVector256(tablePtr + 1280, bi, 4);
                        var blue = (Avx2.GatherVector256(tablePtr + 1536, ri, 4)
                                    + Avx2.GatherVector256(tablePtr + 1792, gi, 4))
                                   + Avx2.GatherVector256(tablePtr + 2048, bi, 4);

                        red.StoreUnsafe(ref outPtr[d + x]);
                        green.StoreUnsafe(ref outPtr[plane + d + x]);
                        blue.StoreUnsafe(ref outPtr[2 * plane + d + x]);
                    }

                    for (; x < width; x++)
                    {
                        var r = srcPtr[o + x * 4];
                        var g = srcPtr[o + x * 4 + 1];
                        var b = srcPtr[o + x * 4 + 2];
                        outPtr[d + x] = (tablePtr[r] + tablePtr[256 + g]) + tablePtr[512 + b];
                        outPtr[plane + d + x] = (tablePtr[768 + r] + tablePtr[1024 + g]) + tablePtr[1280 + b];
                        outPtr[2 * plane + d + x] = (tablePtr[1536 + r] + tablePtr[1792 + g]) + tablePtr[2048 + b];
                    }
                }
            }
        }

        // Byte-level (bit-level) equality of the two float paths before trusting any timing.
        Scalar(0, height);
        var expected = (float[])output.Clone();
        Gather(0, height);
        var identical = true;
        for (var i = 0; i < output.Length; i++)
            if (BitConverter.SingleToInt32Bits(output[i]) != BitConverter.SingleToInt32Bits(expected[i]))
            {
                identical = false;
                break;
            }

        var scalarMs = TimeOncePerCall(() => RowParallel.For(width, height, Scalar));
        var gatherMs = TimeOncePerCall(() => RowParallel.For(width, height, Gather));

        report.Table(["变体", "ms", "相对标量"], [
            ["9× 标量 L1 查表", Timing.F(scalarMs), "1.00×"],
            ["9× AVX2 gather (8 px/组)", Timing.F(gatherMs),
                (gatherMs / scalarMs).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "×"],
        ]);
        report.Line($"> 两变体输出逐位一致：**{identical}**；gather 更划算的条件是相对值 < 1.00×。");
        report.Line();
    }

    private static void ColumnBlockBenchmark(Report report, int width, int height)
    {
        report.Heading(3, "5. 垂直 pass 访存顺序：按 i 外层 vs 列块 × 按 tap 外层");
        var stride = width * 4;
        const int taps = 19;
        const int blockFloats = 64;
        var random = new Random(8181);
        var scratch = new float[taps * stride];
        var kernel = new float[taps];
        for (var i = 0; i < scratch.Length; i++)
            scratch[i] = (float)(random.NextDouble() - 0.5);
        for (var k = 0; k < taps; k++)
            kernel[k] = (float)(random.NextDouble() / taps);

        var outputRow = new float[stride];
        var acc = new float[blockFloats];
        const int repeats = 200;

        void Current()
        {
            for (var i = 0; i < stride; i += 8)
            {
                var sum = Vector256<float>.Zero;
                for (var k = 0; k < taps; k++)
                    sum += Vector256.LoadUnsafe(ref scratch[k * stride + i]) * Vector256.Create(kernel[k]);
                sum.StoreUnsafe(ref outputRow[i]);
            }
        }

        void ColumnBlock()
        {
            for (var block = 0; block < stride; block += blockFloats)
            {
                var length = Math.Min(blockFloats, stride - block);
                for (var j = 0; j < length; j += 8)
                    Vector256<float>.Zero.StoreUnsafe(ref acc[j]);

                for (var k = 0; k < taps; k++)
                {
                    var weight = Vector256.Create(kernel[k]);
                    var sourceBase = k * stride + block;
                    for (var j = 0; j < length; j += 8)
                    {
                        var a = Vector256.LoadUnsafe(ref acc[j]);
                        var s = Vector256.LoadUnsafe(ref scratch[sourceBase + j]);
                        (a + s * weight).StoreUnsafe(ref acc[j]);
                    }
                }

                for (var j = 0; j < length; j += 8)
                    Vector256.LoadUnsafe(ref acc[j]).StoreUnsafe(ref outputRow[block + j]);
            }
        }

        Current();
        var expected = (float[])outputRow.Clone();
        ColumnBlock();
        var identical = true;
        for (var i = 0; i < stride; i++)
            if (BitConverter.SingleToInt32Bits(outputRow[i]) != BitConverter.SingleToInt32Bits(expected[i]))
            {
                identical = false;
                break;
            }

        var currentMs = TimeOncePerCall(() => { for (var r = 0; r < repeats; r++) Current(); });
        var blockMs = TimeOncePerCall(() => { for (var r = 0; r < repeats; r++) ColumnBlock(); });

        report.Table(["变体", $"{repeats} 行 ms", "相对当前"], [
            ["按 i 外层（当前）", Timing.F(currentMs), "1.00×"],
            ["列块 × 按 tap 外层", Timing.F(blockMs),
                (blockMs / currentMs).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "×"],
        ]);
        report.Line($"> 两变体浮点结果逐位一致：**{identical}**（加法顺序未变）；列块更划算的条件是相对值 < 1.00×。");
        report.Line("> 输入尺寸参数只用于构造与库相同宽度的工作集：当前为 " + width + "×" + height + "。");
        report.Line();
    }

    /// <summary>
    /// Same code path, four different coefficient sets. Grayscale, Sepia and a raw ColorMatrix all land in
    /// <c>PointLut.ApplyMatrixRgba8</c>, so if their timings differ by more than noise the difference is in
    /// the harness or in the data — not in the kernel. Interleaved so machine drift hits all four equally.
    /// </summary>
    private static void MatrixPathCrossCheck(Report report, OurImage image)
    {
        report.Heading(3, "8. 同一段矩阵代码、不同系数：互相对照（交错采样，排除漂移）");

        var sepia = new float[]
        {
            0.393f, 0.769f, 0.189f, 0f,
            0.349f, 0.686f, 0.168f, 0f,
            0.272f, 0.534f, 0.131f, 0f,
        };
        var asymmetric = new float[]
        {
            0.512f, 0.221f, 0.113f, 0f,
            0.077f, 0.634f, 0.291f, 0f,
            0.318f, 0.149f, 0.402f, 0f,
        };

        var ops = new (string Name, Func<OurImage> Run)[]
        {
            ("Grayscale (BT.601)", () => image.Grayscale()),
            ("Sepia()", () => image.Sepia()),
            ("ColorMatrix(sepia 系数)", () => image.ColorMatrix(sepia)),
            ("ColorMatrix(非对称系数)", () => image.ColorMatrix(asymmetric)),
            ("Invert（字节 LUT 路径）", () => image.Invert()),
        };

        var coefficientRows = new List<string[]>();
        try
        {
            foreach (var degree in new[] { 1, 0 })
            {
                RowParallel.DegreeOverride = degree;
                var runners = ops.Select(o => new Runner(o.Name, () =>
                {
                    using var result = o.Run();
                    GC.KeepAlive(result);
                })).ToArray();
                var times = Timing.Time(runners, rounds: 5);
                coefficientRows.Add([degree == 1 ? "1 worker" : "默认并行",
                    .. ops.Select(o => Timing.F(times[o.Name]))]);
            }
        }
        finally
        {
            RowParallel.DegreeOverride = 0;
        }

        report.Table(["并行度", .. ops.Select(o => o.Name)], coefficientRows);
        report.Line("> 前四个场景走的是**同一个** `ApplyMatrixRgba8`，只有系数不同；差异若超过噪声，说明差异不在内核指令而在数据或测量。");
        report.Line("> 这张表是当初发现「sepia 比灰度慢 2.2×」全是 DegreeScan 测量假象的证据，保留以便复查。");
        report.Line();
    }

    /// <summary>
    /// How much of a call is the operator and how much is getting hold of the output buffer? Rent/return
    /// of one 8 MB buffer is timed on its own, next to the public API and the bare kernel, and the managed
    /// allocation per call is recorded for each — the three numbers have to add up or something is hiding.
    /// </summary>
    private static void CallOverheadBreakdown(Report report, OurImage image, int width, int height)
    {
        report.Heading(3, "9. 每次调用的固定开销拆解：租还 8 MB 缓冲 vs 内核 vs 公开 API");

        var length = width * height * 4;
        var matrix = new float[]
        {
            0.393f, 0.769f, 0.189f, 0f,
            0.349f, 0.686f, 0.168f, 0f,
            0.272f, 0.534f, 0.131f, 0f,
        };
        var view = image[0].Pixels.View;

        var runners = new[]
        {
            new Runner("只租还缓冲", () =>
            {
                var owner = ArrayPixelPool.Shared.Rent(length);
                owner.Dispose();
            }),
            new Runner("公开 API Sepia()", () => { using var r = image.Sepia(); GC.KeepAlive(r); }),
            new Runner("裸内核 ApplyMatrixRgba8", () =>
            {
                using var r = PointLut.ApplyMatrixRgba8(view, null, matrix);
            }),
        };

        var allocated = new Dictionary<string, long>();
        var times = Timing.Time(runners, rounds: 7, allocated: allocated);

        var rows = runners.Select(r => new[]
        {
            r.Name,
            Timing.F(times[r.Name]),
            (allocated.TryGetValue(r.Name, out var bytes) ? bytes / 1024 : 0).ToString(CultureInfo.InvariantCulture) + " KiB",
        }).ToList();

        report.Table(["变体", "ms", "分配/次"], rows);
        report.Line($"> 输出缓冲 {length / 1024} KiB。若「只租还缓冲」已经吃掉可观时间，瓶颈就不在内核，而在每调用的缓冲路径。");
        report.Line();
    }

    private static void WorkerGranularityScan(Report report, OurImage image, int width, int height)
    {
        report.Heading(3, "6. worker 粒度扫描（每 worker 最少行数）");
        var granularities = new[] { 16, 32, 64, 128, 256, 512 };
        var ops = new (string Name, Func<OurImage, OurImage> Run)[]
        {
            ("Invert", i => i.Invert()),
            ("Grayscale", i => i.Grayscale()),
            ("Sepia", i => i.Sepia()),
            ("Rotate90", i => i.Rotate90()),
        };

        var rows = new List<string[]>();
        try
        {
            foreach (var granularity in granularities)
            {
                RowParallel.RowsPerWorkerOverride = granularity;
                var samples = new List<string>();
                int workers = 0;
                foreach (var (_, run) in ops)
                {
                    var ms = TimeOncePerCall(() =>
                    {
                        using var result = run(image);
                        GC.KeepAlive(result);
                    });
                    samples.Add(Timing.F(ms));
                    workers = RowParallel.LastWorkerCount;
                }

                rows.Add([granularity.ToString(CultureInfo.InvariantCulture),
                    workers.ToString(CultureInfo.InvariantCulture), .. samples]);
            }
        }
        finally
        {
            RowParallel.RowsPerWorkerOverride = 0;
        }

        report.Table(["行/worker", "实际 workers", .. ops.Select(o => o.Name)], rows);
        report.Line("> 只影响 `RowParallel.For`（点算子、rotate90）；滤波/resize 走 `ForBands`，不受此参数影响。");
        report.Line();
    }
}
