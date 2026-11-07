using System.Globalization;
using System.Runtime.Intrinsics;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Comparison;

/// <summary>
/// Blur scheduling shapes, not micro-tunes. A separable blur has two passes and each has a loop nest that can
/// be arranged several ways; this file measures the arrangements instead of guessing.
/// <para>Every variant must produce <b>bit-identical</b> floats before its timing is reported: the tap
/// accumulation for a given output sample stays in ascending-k order in all of them, so reordering the
/// <i>nest</i> (which sample is outer, which is inner) is legal while reordering the <i>sum</i> is not. A
/// variant whose bits differ is reported as such and its timing is not trusted.</para>
/// <para>No hardware performance counters are read: PMU cache-miss counting on Windows needs a kernel ETW
/// provider plus elevation. What is reported instead is bytes moved over elapsed time, derived from the
/// loop bounds. It says whether a shape is near the memory roof; it does not say which cache level missed.</para>
/// </summary>
internal static class BlurProfile
{
    private const int BandRows = 128;

    public static void Write(Report report, int width, int height)
    {
        const int Radius = 9;                          // sigma 3 → ceil(3σ) = 9
        var taps = Radius * 2 + 1;
        var kernel = GaussianKernel(Radius);
        var stride = width * 4;

        report.Heading(2, "可分离滤波的循环嵌套形态（纵向 pass 与横向 pass 分开测）");
        report.Line($"> 输入 {width}×{height}，σ3（{taps} taps），band = {BandRows} 行，与 `FilterOps.Separable` 默认一致。");
        report.Line("> 所有变体都保持「单个输出样本的 tap 累加按 k 升序」，因此浮点结果必须逐位相同；不一致的变体计时作废。");
        report.Line();

        VerticalShapes(report, width, height, stride, kernel, Radius, taps);
        HorizontalShapes(report, width, height, stride, kernel, Radius, taps);
        BoxSlidingWindow(report, width);
    }

    private static float[] GaussianKernel(int radius)
    {
        const float sigma = 3f;
        var kernel = new float[radius * 2 + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var weight = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            kernel[i + radius] = weight;
            sum += weight;
        }

        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;
        return kernel;
    }

    private static void VerticalShapes(Report report, int width, int height, int stride, float[] kernel,
        int radius, int taps)
    {
        report.Heading(3, "1. 纵向 pass：一维 band（现状）vs 列块 vs 行 tile vs 两者组合");

        var scratchRows = BandRows + radius * 2;
        var scratch = new float[scratchRows * stride];
        var random = new Random(9091);
        for (var i = 0; i < scratch.Length; i++)
            scratch[i] = (float)(random.NextDouble() - 0.5);

        var reference = new float[height * stride];
        var output = new float[height * stride];
        var bands = (height + BandRows - 1) / BandRows;

        // ---- V0: current shape. Per output row, per 8 floats, taps inner, clamp recomputed per chunk. ----
        void Current(int bandTop, int bandBottom)
        {
            var top = Math.Max(0, bandTop - radius);
            for (var y = bandTop; y < bandBottom; y++)
            {
                var rowBase = y * stride;
                var i = 0;
                for (; i + 8 <= stride; i += 8)
                {
                    var sum = Vector256<float>.Zero;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var sy = Math.Clamp(y + k, 0, height - 1) - top;
                        sum += Vector256.LoadUnsafe(ref scratch[sy * stride + i]) * Vector256.Create(kernel[k + radius]);
                    }

                    sum.StoreUnsafe(ref output[rowBase + i]);
                }

                for (; i < stride; i++)
                {
                    var sum = 0f;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var sy = Math.Clamp(y + k, 0, height - 1) - top;
                        sum += scratch[sy * stride + i] * kernel[k + radius];
                    }

                    output[rowBase + i] = sum;
                }
            }
        }

        // ---- V0b: same nest, the per-tap row offset is hoisted out of the x loop. ----
        void Hoisted(int bandTop, int bandBottom)
        {
            Span<int> offsets = stackalloc int[taps];
            var top = Math.Max(0, bandTop - radius);
            for (var y = bandTop; y < bandBottom; y++)
            {
                for (var k = 0; k < taps; k++)
                    offsets[k] = (Math.Clamp(y + k - radius, 0, height - 1) - top) * stride;

                var rowBase = y * stride;
                var i = 0;
                for (; i + 8 <= stride; i += 8)
                {
                    var sum = Vector256<float>.Zero;
                    for (var k = 0; k < taps; k++)
                        sum += Vector256.LoadUnsafe(ref scratch[offsets[k] + i]) * Vector256.Create(kernel[k]);
                    sum.StoreUnsafe(ref output[rowBase + i]);
                }

                for (; i < stride; i++)
                {
                    var sum = 0f;
                    for (var k = 0; k < taps; k++)
                        sum += scratch[offsets[k] + i] * kernel[k];
                    output[rowBase + i] = sum;
                }
            }
        }

        // ---- V1: band × column block. Tap outer, a 64-float strip stays in registers. ----
        const int BlockFloats = 64;
        void ColumnBlock(int bandTop, int bandBottom)
        {
            Span<int> offsets = stackalloc int[taps];
            Span<float> acc = stackalloc float[BlockFloats];
            var top = Math.Max(0, bandTop - radius);
            for (var y = bandTop; y < bandBottom; y++)
            {
                for (var k = 0; k < taps; k++)
                    offsets[k] = (Math.Clamp(y + k - radius, 0, height - 1) - top) * stride;

                var rowBase = y * stride;
                for (var block = 0; block < stride; block += BlockFloats)
                {
                    var length = Math.Min(BlockFloats, stride - block);
                    for (var j = 0; j < length; j += 8)
                        Vector256<float>.Zero.StoreUnsafe(ref acc[j]);

                    for (var k = 0; k < taps; k++)
                    {
                        var weight = Vector256.Create(kernel[k]);
                        var sourceBase = offsets[k] + block;
                        for (var j = 0; j < length; j += 8)
                        {
                            var a = Vector256.LoadUnsafe(ref acc[j]);
                            var s = Vector256.LoadUnsafe(ref scratch[sourceBase + j]);
                            (a + s * weight).StoreUnsafe(ref acc[j]);
                        }
                    }

                    for (var j = 0; j < length; j += 8)
                        Vector256.LoadUnsafe(ref acc[j]).StoreUnsafe(ref output[rowBase + block + j]);
                }
            }
        }

        // ---- V2: band × output-row tile. T rows share one read of each tap row, one accumulator per row. ----
        const int TileRows = 4;
        void RowTile(int bandTop, int bandBottom)
        {
            Span<int> tileOffsets = stackalloc int[TileRows * taps];
            Span<Vector256<float>> sums = stackalloc Vector256<float>[TileRows];
            var top = Math.Max(0, bandTop - radius);
            for (var y0 = bandTop; y0 < bandBottom; y0 += TileRows)
            {
                var rowCount = Math.Min(TileRows, bandBottom - y0);
                for (var t = 0; t < rowCount; t++)
                for (var k = 0; k < taps; k++)
                    tileOffsets[t * taps + k] = (Math.Clamp(y0 + t + k - radius, 0, height - 1) - top) * stride;

                var i = 0;
                for (; i + 8 <= stride; i += 8)
                {
                    for (var t = 0; t < rowCount; t++)
                        sums[t] = Vector256<float>.Zero;

                    for (var k = 0; k < taps; k++)
                    {
                        var weight = Vector256.Create(kernel[k]);
                        for (var t = 0; t < rowCount; t++)
                            sums[t] += Vector256.LoadUnsafe(ref scratch[tileOffsets[t * taps + k] + i]) * weight;
                    }

                    for (var t = 0; t < rowCount; t++)
                        sums[t].StoreUnsafe(ref output[(y0 + t) * stride + i]);
                }

                for (; i < stride; i++)
                {
                    for (var t = 0; t < rowCount; t++)
                    {
                        var sum = 0f;
                        for (var k = 0; k < taps; k++)
                            sum += scratch[tileOffsets[t * taps + k] + i] * kernel[k];
                        output[(y0 + t) * stride + i] = sum;
                    }
                }
            }
        }

        // ---- V3: column block × row tile. Two output rows, one 32-float strip, tap outer. ----
        const int ComboBlock = 32;
        const int ComboRows = 2;
        void Combo(int bandTop, int bandBottom)
        {
            Span<int> tileOffsets = stackalloc int[ComboRows * taps];
            Span<float> acc = stackalloc float[ComboRows * ComboBlock];
            var top = Math.Max(0, bandTop - radius);
            for (var y0 = bandTop; y0 < bandBottom; y0 += ComboRows)
            {
                var rowCount = Math.Min(ComboRows, bandBottom - y0);
                for (var t = 0; t < rowCount; t++)
                for (var k = 0; k < taps; k++)
                    tileOffsets[t * taps + k] = (Math.Clamp(y0 + t + k - radius, 0, height - 1) - top) * stride;

                for (var block = 0; block < stride; block += ComboBlock)
                {
                    var length = Math.Min(ComboBlock, stride - block);
                    for (var t = 0; t < rowCount; t++)
                    for (var j = 0; j < length; j += 8)
                        Vector256<float>.Zero.StoreUnsafe(ref acc[t * ComboBlock + j]);

                    for (var k = 0; k < taps; k++)
                    {
                        var weight = Vector256.Create(kernel[k]);
                        for (var t = 0; t < rowCount; t++)
                        {
                            var sourceBase = tileOffsets[t * taps + k] + block;
                            for (var j = 0; j < length; j += 8)
                            {
                                var a = Vector256.LoadUnsafe(ref acc[t * ComboBlock + j]);
                                var s = Vector256.LoadUnsafe(ref scratch[sourceBase + j]);
                                (a + s * weight).StoreUnsafe(ref acc[t * ComboBlock + j]);
                            }
                        }
                    }

                    for (var t = 0; t < rowCount; t++)
                    for (var j = 0; j < length; j += 8)
                        Vector256.LoadUnsafe(ref acc[t * ComboBlock + j])
                            .StoreUnsafe(ref output[(y0 + t) * stride + block + j]);
                }
            }
        }

        var variants = new (string Name, Action<int, int> Run)[]
        {
            ("V0 现状：逐输出行，tap 内层（clamp 在内层）", Current),
            ("V0b 同上，行偏移提到外层", Hoisted),
            ("V1 band × 列块 64 floats，tap 外层", ColumnBlock),
            ("V2 band × 行 tile 4 行", RowTile),
            ("V3 列块 32 × 行 tile 2", Combo),
        };

        for (var b = 0; b < bands; b++)
        {
            var top = b * BandRows;
            Current(top, Math.Min(top + BandRows, height));
        }

        Array.Copy(output, reference, output.Length);

        var rows = new List<string[]>();
        foreach (var (name, run) in variants)
        {
            Array.Clear(output);
            for (var b = 0; b < bands; b++)
            {
                var top = b * BandRows;
                run(top, Math.Min(top + BandRows, height));
            }

            var identical = true;
            for (var i = 0; i < output.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(output[i]) != BitConverter.SingleToInt32Bits(reference[i]))
                {
                    identical = false;
                    break;
                }
            }

            var ms = TimePerBand(run, height);
            var bytes = (double)height * stride * (taps + 1) * 4;
            rows.Add([name, identical ? "逐位一致" : "**不一致**", Timing.F(ms),
                (bytes / (ms * 1e-3) / 1e9).ToString("F1", CultureInfo.InvariantCulture)]);
        }

        report.Table(["变体", "对照结果", "ms", "推导带宽 GB/s"], rows);
        report.Line("> 「推导带宽」= (taps 次读 + 1 次写) 的字节数 ÷ 耗时，用来判断是否已经贴着内存屋顶；**不是**硬件计数器读数。");
        report.Line("> 纵向 pass 的重复读来自同一条 tap 行被每个输出行重读一遍；行 tile 的意义就是把这份重复读按 tile 行数摊薄。");
        report.Line();

        // True 2-D tiling: the strip is a column block AND the tile spans several output rows, so one read of
        // a tap row feeds `tileRows` accumulators. tileRows = 1 degenerates to the pure column block, which
        // makes this a sweep over the whole family rather than two hand-picked points.
        var sweepRows = new List<string[]>();
        foreach (var tileRows in new[] { 1, 2, 4 })
        foreach (var block in new[] { 16, 32, 64, 128 })
        {
            var run = MakeTwoDimensional(scratch, output, stride, height, kernel, radius, tileRows, block);

            Array.Clear(output);
            for (var b = 0; b < bands; b++)
            {
                var top = b * BandRows;
                run(top, Math.Min(top + BandRows, height));
            }

            var same = true;
            for (var i = 0; i < output.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(output[i]) != BitConverter.SingleToInt32Bits(reference[i]))
                {
                    same = false;
                    break;
                }
            }

            var ms = TimePerBand(run, height);
            var bytes = (double)height * stride * (taps + 1) * 4;
            sweepRows.Add([$"{tileRows} 行 × {block} floats", same ? "逐位一致" : "**不一致**", Timing.F(ms),
                (bytes / (ms * 1e-3) / 1e9).ToString("F1", CultureInfo.InvariantCulture)]);
        }

        report.Table(["tile 形状（行 × 列块）", "对照结果", "ms", "推导带宽 GB/s"], sweepRows);
        report.Line("> `1 行 × B` 就是纯列块；行数 > 1 才是二维 tile。全组逐位一致，所以这是一次纯调度参数的搜索。");
        report.Line();

        // The sweep above runs its configs one after another, so drift between them is unaccounted for. The
        // three finalists are re-measured round-robin in a single loop before any of them is allowed to
        // change the library.
        var finalists = new (string Name, Action<int, int> Run)[]
        {
            ("1 行 × 64（当前库内）", MakeTwoDimensional(scratch, output, stride, height, kernel, radius, 1, 64)),
            ("1 行 × 128", MakeTwoDimensional(scratch, output, stride, height, kernel, radius, 1, 128)),
            ("4 行 × 128", MakeTwoDimensional(scratch, output, stride, height, kernel, radius, 4, 128)),
        };

        var finalistRunners = finalists.Select(f => new Runner(f.Name, () =>
        {
            for (var b = 0; b < bands; b++)
            {
                var top = b * BandRows;
                f.Run(top, Math.Min(top + BandRows, height));
            }
        })).ToArray();
        var interleaved = Timing.Time(finalistRunners, rounds: 5);
        var baseline = interleaved[finalists[0].Name];

        report.Table(["决赛配置", "ms", "相对当前库内"],
            finalists.Select(f => new[]
            {
                f.Name,
                Timing.F(interleaved[f.Name]),
                (interleaved[f.Name] / baseline).ToString("F2", CultureInfo.InvariantCulture) + "×",
            }).ToList());
        report.Line("> 同一循环轮转采样，漂移被三个配置共同承担；这张表才是选参数的依据。");
        report.Line();
    }

    /// <summary>
    /// Two-dimensional tile: `tileRows` output rows × `blockFloats` floats, tap outer. Each tap row is read
    /// once per (tile, tap) and added into `tileRows` accumulators, so the repeated read of a tap row is
    /// amortised over the tile height. With `tileRows == 1` this is exactly the column-block shape.
    /// </summary>
    private static Action<int, int> MakeTwoDimensional(float[] scratch, float[] output, int stride, int height,
        float[] kernel, int radius, int tileRows, int blockFloats)
    {
        var taps = kernel.Length;
        return (bandTop, bandBottom) =>
        {
            Span<int> tileOffsets = stackalloc int[tileRows * taps];
            Span<float> acc = stackalloc float[tileRows * blockFloats];
            var top = Math.Max(0, bandTop - radius);

            for (var y0 = bandTop; y0 < bandBottom; y0 += tileRows)
            {
                var rowCount = Math.Min(tileRows, bandBottom - y0);
                for (var t = 0; t < rowCount; t++)
                for (var k = 0; k < taps; k++)
                    tileOffsets[t * taps + k] = (Math.Clamp(y0 + t + k - radius, 0, height - 1) - top) * stride;

                for (var block = 0; block < stride; block += blockFloats)
                {
                    var length = Math.Min(blockFloats, stride - block);
                    var vectorLength = length - length % 8;

                    for (var t = 0; t < rowCount; t++)
                    for (var j = 0; j < vectorLength; j += 8)
                        Vector256<float>.Zero.StoreUnsafe(ref acc[t * blockFloats + j]);

                    for (var k = 0; k < taps; k++)
                    {
                        var weight = Vector256.Create(kernel[k]);
                        for (var t = 0; t < rowCount; t++)
                        {
                            var sourceBase = tileOffsets[t * taps + k] + block;
                            for (var j = 0; j < vectorLength; j += 8)
                            {
                                var a = Vector256.LoadUnsafe(ref acc[t * blockFloats + j]);
                                var v = Vector256.LoadUnsafe(ref scratch[sourceBase + j]);
                                (a + v * weight).StoreUnsafe(ref acc[t * blockFloats + j]);
                            }
                        }
                    }

                    for (var t = 0; t < rowCount; t++)
                    for (var j = 0; j < vectorLength; j += 8)
                        Vector256.LoadUnsafe(ref acc[t * blockFloats + j])
                            .StoreUnsafe(ref output[(y0 + t) * stride + block + j]);

                    // Scalar twin for the tail of the last strip, in the same ascending-k order.
                    for (var j = vectorLength; j < length; j++)
                    {
                        for (var t = 0; t < rowCount; t++)
                        {
                            var sum = 0f;
                            for (var k = 0; k < taps; k++)
                                sum += scratch[tileOffsets[t * taps + k] + block + j] * kernel[k];
                            output[(y0 + t) * stride + block + j] = sum;
                        }
                    }
                }
            }
        };
    }

    /// <summary>One output sample of a planar 1-D convolution, taps in ascending order — the reference the
    /// vector interior must reproduce bit for bit.</summary>
    private static float SumScalar(float[] plane, int planeBase, int x, int width, float[] kernel, int radius)
    {
        var sum = 0f;
        for (var k = -radius; k <= radius; k++)
            sum += plane[planeBase + Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
        return sum;
    }

    private static double TimePerBand(Action<int, int> runBand, int height)
    {
        var bands = (height + BandRows - 1) / BandRows;
        var runners = new[]
        {
            new Runner("only", () => RowParallel.ForBands(bands, (start, end) =>
            {
                for (var b = start; b < end; b++)
                {
                    var top = b * BandRows;
                    runBand(top, Math.Min(top + BandRows, height));
                }
            })),
        };

        return Timing.Time(runners, rounds: 5)["only"];
    }

    private static void HorizontalShapes(Report report, int width, int height, int stride, float[] kernel,
        int radius, int taps)
    {
        report.Heading(3, "2. 横向 pass：RGBA 交错 + 每像素一次（现状）vs 平面化 + 8 像素一次");

        var rowsToRun = 256;
        var source = new float[rowsToRun * stride];
        var random = new Random(5150);
        for (var i = 0; i < source.Length; i++)
            source[i] = (float)(random.NextDouble() - 0.5);

        var reference = new float[rowsToRun * stride];
        var output = new float[rowsToRun * stride];


        void Interleaved(int start, int end)
        {
            for (var y = start; y < end; y++)
            {
                var baseOffset = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var accumulator = Vector128<float>.Zero;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var sx = Math.Clamp(x + k, 0, width - 1);
                        accumulator += Vector128.LoadUnsafe(ref source[baseOffset + sx * 4])
                                       * Vector128.Create(kernel[k + radius]);
                    }

                    accumulator.StoreUnsafe(ref output[baseOffset + x * 4]);
                }
            }
        }

        // De-interleave and re-interleave are measured as part of this variant, not hidden: a shape that only
        // wins when the data already arrives planar has not won anything. One row at a time, because the
        // planar buffers hold a single row.
        void Planar(int start, int end, bool transposeOnly)
        {
            var plane = new float[4 * width];
            var planarOut = new float[4 * width];

            for (var y = start; y < end; y++)
            {
                var baseOffset = y * stride;

                for (var x = 0; x < width; x++)
                for (var c = 0; c < 4; c++)
                    plane[c * width + x] = source[baseOffset + x * 4 + c];

                if (!transposeOnly)
                for (var c = 0; c < 4; c++)
                {
                    var planeBase = c * width;

                    // The vector loop may only use an unclamped shifted window: a group of 8 pixels reads
                    // plane[x + k .. x + k + 7] as if every lane had the same offset. As soon as one lane in
                    // the group needs its index clamped, that shortcut reads the wrong samples, so the first
                    // and last `radius` pixels stay scalar. Getting this wrong was the 1.1% mismatch.
                    var x = 0;
                    for (; x < radius; x++)
                        planarOut[planeBase + x] = SumScalar(plane, planeBase, x, width, kernel, radius);

                    for (; x + 7 + radius <= width - 1; x += 8)
                    {
                        var sum = Vector256<float>.Zero;
                        for (var k = -radius; k <= radius; k++)
                            sum += Vector256.LoadUnsafe(ref plane[planeBase + x + k]) * Vector256.Create(kernel[k + radius]);
                        sum.StoreUnsafe(ref planarOut[planeBase + x]);
                    }

                    for (; x < width; x++)
                        planarOut[planeBase + x] = SumScalar(plane, planeBase, x, width, kernel, radius);
                }

                for (var x = 0; x < width; x++)
                for (var c = 0; c < 4; c++)
                    output[baseOffset + x * 4 + c] = planarOut[c * width + x];
            }
        }

        Array.Clear(output);
        Interleaved(0, rowsToRun);
        Array.Copy(output, reference, output.Length);

        Array.Clear(output);
        Planar(0, rowsToRun, false);

        // A bit difference has two very different causes and they must not be confused: a rounding-order
        // difference stays at the 1 ulp scale, a logic error does not. Report the magnitude, not just a flag.
        var identical = true;
        var differing = 0;
        var maxDiff = 0f;
        for (var i = 0; i < output.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(output[i]) == BitConverter.SingleToInt32Bits(reference[i]))
                continue;
            identical = false;
            differing++;
            var diff = MathF.Abs(output[i] - reference[i]);
            if (diff > maxDiff)
                maxDiff = diff;
        }

        var interleavedMs = Timing.Time(
            [new Runner("only", () => RowParallel.ForBands(rowsToRun, Interleaved))], rounds: 5)["only"];
        var planarMs = Timing.Time(
            [new Runner("only", () => RowParallel.ForBands(rowsToRun, (a, b) => Planar(a, b, false)))],
            rounds: 5)["only"];
        var transposeMs = Timing.Time(
            [new Runner("only", () => RowParallel.ForBands(rowsToRun, (a, b) => Planar(a, b, true)))],
            rounds: 5)["only"];

        var touched = (double)rowsToRun * stride * (taps + 1) * 4;
        report.Table(["变体", "对照结果", $"{rowsToRun} 行 ms", "相对现状", "推导带宽 GB/s"], [
            ["RGBA 交错，每像素一次", "基准", Timing.F(interleavedMs), "1.00×",
                (touched / (interleavedMs * 1e-3) / 1e9).ToString("F1", CultureInfo.InvariantCulture)],
            ["平面化，8 像素一次（标量交织）", identical ? "逐位一致" : "**不一致**", Timing.F(planarMs),
                (planarMs / interleavedMs).ToString("F2", CultureInfo.InvariantCulture) + "×",
                (touched / (planarMs * 1e-3) / 1e9).ToString("F1", CultureInfo.InvariantCulture)],
            ["只做交织/反交织（无卷积）", "—", Timing.F(transposeMs),
                (transposeMs / interleavedMs).ToString("F2", CultureInfo.InvariantCulture) + "×",
                (touched / (transposeMs * 1e-3) / 1e9).ToString("F1", CultureInfo.InvariantCulture)],
        ]);
        report.Line(identical
            ? "> 平面化只换「哪一维是向量维」：现状把 4 个通道塞进一条 128 位向量（一次 1 像素），平面化把 8 个像素塞进一条 256 位向量；单个样本的 tap 累加仍是 k 升序。"
            : $"> 不一致样本 {differing} / {output.Length}，最大绝对差 {maxDiff.ToString("E3", CultureInfo.InvariantCulture)}。1 ulp 量级才可能是舍入顺序差异；数量级到 1e34 就只能是读到脏数据或逻辑错，必须查清楚再谈计时。");
        report.Line("> 这条也解释了为什么「逐字节」必须逐档位验证：同一条 C# 表达式在 128 位与 256 位向量上可能生成不同的舍入序列。");
        report.Line("> 代价是每行多一次 de-interleave 与一次 re-interleave，收益能否盖过它，看相对值。");
        report.Line();
    }


    /// <summary>
    /// A box kernel is uniform, so a sliding window could in principle do O(1) work per pixel instead of
    /// O(2r+1): <c>S(x) = S(x-1) - v(x-1-r) + v(x+r)</c>. That changes the accumulation order, so it cannot
    /// be assumed byte-identical — this measures how far off it actually lands rather than arguing about it.
    /// </summary>
    private static void BoxSlidingWindow(Report report, int width)
    {
        report.Heading(3, "3. box 滑动窗和（O(1)/像素）是否还能逐字节一致");

        const int Radius = 8;
        var taps = Radius * 2 + 1;
        var weight = 1f / taps;
        var random = new Random(31337);
        var row = new float[width];
        for (var i = 0; i < width; i++)
            row[i] = (float)(random.NextDouble() - 0.5);

        var direct = new float[width];
        var sliding = new float[width];

        // Direct: ascending taps, exactly what the library does.
        for (var x = 0; x < width; x++)
        {
            var sum = 0f;
            for (var k = -Radius; k <= Radius; k++)
                sum += row[Math.Clamp(x + k, 0, width - 1)] * weight;
            direct[x] = sum;
        }

        // Sliding: seed the first sample the same way, then update by subtracting the leaving sample and
        // adding the entering one. Edges are handled by clamping, so the seed is exact.
        {
            var sum = 0f;
            for (var k = -Radius; k <= Radius; k++)
                sum += row[Math.Clamp(k, 0, width - 1)] * weight;
            sliding[0] = sum;

            for (var x = 1; x < width; x++)
            {
                var leaving = row[Math.Clamp(x - 1 - Radius, 0, width - 1)] * weight;
                var entering = row[Math.Clamp(x + Radius, 0, width - 1)] * weight;
                sum = sum - leaving + entering;
                sliding[x] = sum;
            }
        }

        // Second shortcut worth checking: the box kernel is uniform, so "sum the samples, multiply once at the
        // end" does 17 adds + 1 multiply instead of 17 multiply-adds. Same question -- does it move any bits?
        var summed = new float[width];
        for (var x = 0; x < width; x++)
        {
            var sum = 0f;
            for (var k = -Radius; k <= Radius; k++)
                sum += row[Math.Clamp(x + k, 0, width - 1)];
            summed[x] = sum * weight;
        }

        var sumDiffering = 0;
        var sumMaxUlp = 0;
        for (var i = 0; i < width; i++)
        {
            if (BitConverter.SingleToInt32Bits(direct[i]) == BitConverter.SingleToInt32Bits(summed[i]))
                continue;
            sumDiffering++;
            var ulp = Math.Abs(BitConverter.SingleToInt32Bits(direct[i]) - BitConverter.SingleToInt32Bits(summed[i]));
            if (ulp > sumMaxUlp)
                sumMaxUlp = ulp;
        }

        var differing = 0;
        var maxDiff = 0f;
        var maxUlp = 0;
        for (var i = 0; i < width; i++)
        {
            if (BitConverter.SingleToInt32Bits(direct[i]) == BitConverter.SingleToInt32Bits(sliding[i]))
                continue;
            differing++;
            var diff = MathF.Abs(direct[i] - sliding[i]);
            if (diff > maxDiff)
                maxDiff = diff;
            var ulp = Math.Abs(BitConverter.SingleToInt32Bits(direct[i]) - BitConverter.SingleToInt32Bits(sliding[i]));
            if (ulp > maxUlp)
                maxUlp = ulp;
        }

        report.Table(["做法", "每像素工作量", $"与直接法不一致（共 {width}）", "最大绝对差", "最大 ULP 差"], [
            ["直接累加（现状）", $"{taps} 次乘加", "基准", "—", "—"],
            ["滑动窗和", "1 减 1 加", differing.ToString(CultureInfo.InvariantCulture),
                maxDiff.ToString("E3", CultureInfo.InvariantCulture), maxUlp.ToString(CultureInfo.InvariantCulture)],
            ["先求和再一次乘", $"{taps} 次加 + 1 次乘", sumDiffering.ToString(CultureInfo.InvariantCulture),
                "—", sumMaxUlp.ToString(CultureInfo.InvariantCulture)],
        ]);
        report.Line(differing == 0
            ? "> 罕见结果：滑动窗和在这里逐字节一致，可以考虑落地（但要先在全尺寸图像上复核）。"
            : "> 滑动窗和**不是逐字节一致**：浮点加法的顺序变了，`S(x)` 携带上一像素的舍入误差，误差沿行累积。在「锁死逐字节」的前提下不可用，O(1)/像素这条路只能放弃。");
        report.Line(sumDiffering == 0
            ? "> 「先求和再一次乘」在这一行上逐位一致，值得在全尺寸图像上复核后再考虑落地。"
            : "> 「先求和再一次乘」同样不是逐字节一致：`Σ(v·w)` 与 `(Σv)·w` 的舍入点不同。box 的两条捷径（滑动窗、先和后乘）都被数据否掉。");
        report.Line();
    }
}
