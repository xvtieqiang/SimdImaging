using System.Diagnostics;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Runs the newer whole-image operators over real photographs rather than synthetic patterns: dehaze on each, and
/// exposure fusion on three exposures derived from each.
/// </summary>
/// <remarks>
/// <para>
/// Synthetic tests prove an operator does what it says on the cases they were written for. This is the other half:
/// real files, real sizes, real bit depths, and a check that the output is still a picture — not all black, not all
/// white, not NaN — with the time it took.
/// </para>
/// <para>
/// The three exposures for fusion are manufactured by scaling and clipping one photograph, which is exactly what a
/// camera bracket produces: the scene is unknown to the operator either way.
/// </para>
/// </remarks>
internal static class RealWorld
{
    public static void Write(Report report)
    {
        var files = Find();
        report.Heading(2, $"J 组 · 真实图片跑通（{files.Count} 张）");
        if (files.Count == 0)
        {
            report.Line("> 没找到真实图片；`tests/Fixtures/download-corpus.sh` 下载 CC0 语料后再跑。");
            return;
        }

        report.Line("> 每张图跑三件事：解码、去雾（`r=7, refine=20`）、三档曝光融合。判据是**输出还是不是一张图**——");
        report.Line("> 均值落在 0.02–0.98 之间、标准差 > 0.01、没有 NaN；不是拿它和谁比谁好看。");
        report.Line();

        var rows = new List<string[]>();
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            try
            {
                rows.Add(Run(path, name));
            }
            catch (Exception e)
            {
                rows.Add([name, "—", "—", "—", "—", "—", "**失败**: " + e.GetType().Name + ": " + e.Message]);
            }
        }

        report.Table(["文件", "尺寸", "格式", "去雾 ms", "融合 ms", "输出均值/标准差", "判定"], rows);
        report.Line("> 「判定」看的是**输出可用性**：均值与标准差说明画面既没有全黑也没有全白，`NaN` 计数说明数值没有崩。");
    }

    private static string[] Run(string path, string name)
    {
        using var image = Image.Load(path);
        var width = image.Width;
        var height = image.Height;

        var dehazeWatch = Stopwatch.StartNew();
        using var dehazed = image.Dehaze(7, 0.95f, 0.1f, 20);
        dehazeWatch.Stop();

        // Three exposures of the same scene: 0.45×, 1× and 2.2×, each clipped the way a sensor clips.
        using var dark = Expose(image, 0.45f);
        using var bright = Expose(image, 2.2f);

        var fuseWatch = Stopwatch.StartNew();
        using var fused = Image.FuseExposures([dark, image, bright]);
        fuseWatch.Stop();

        // Optional: write the outputs next to the report so a human can look at them. A number saying the frame is
        // "usable" is not the same as the frame looking like a photograph.
        if (Environment.GetEnvironmentVariable("SIMDIMAGING_DUMP_DIR") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            var stem = Path.GetFileNameWithoutExtension(name);
            Image.Save(dehazed, Path.Combine(dump, stem + ".dehaze.png"));
            Image.Save(fused, Path.Combine(dump, stem + ".fusion.png"));

            // A copy small enough to open in a viewer: a 3069-wide PNG is not something a human inspects casually.
            var scale = Math.Min(1f, 800f / Math.Max(width, height));
            if (scale < 1f)
            {
                var small = (int)(width * scale);
                var smallH = (int)(height * scale);
                using var thumbSource = image.Resize(small, smallH);
                using var thumbDehaze = dehazed.Resize(small, smallH);
                using var thumbFused = fused.Resize(small, smallH);
                Image.Save(thumbSource, Path.Combine(dump, stem + ".source.small.png"));
                Image.Save(thumbDehaze, Path.Combine(dump, stem + ".dehaze.small.png"));
                Image.Save(thumbFused, Path.Combine(dump, stem + ".fusion.small.png"));
            }
        }

        // The established operators, on the same real photograph: not for a number, but because a synthetic
        // pattern cannot reach a code path that a real file does (a 16-bit ramp, a fully saturated sky, an alpha
        // channel). Any throw here is a defect the comparison suite would not have shown.
        var failures = new List<string>();
        // `sparse` marks the operators whose correct output is mostly dark with thin bright edges: a mean near zero
        // is the answer, not a fault. Everything else must stay in the displayable band.
        void Run(string name2, Func<Image, IDisposable> apply, bool sparse = false)
        {
            try
            {
                using var result = apply(image);
                var (m, d, n) = Statistics((Image)result);
                if (n > 0 || d < 0.005f || (!sparse && m is <= 0.01f or >= 0.99f))
                    failures.Add($"{name2}(mean {m:F2} sd {d:F2} nan {n})");
            }
            catch (Exception e)
            {
                failures.Add($"{name2}({e.GetType().Name})");
            }
        }

        Run("blur", i => i.GaussianBlur(2f));
        Run("sharpen", i => i.Sharpen());
        Run("median", i => i.Median3x3());
        Run("sobel", i => i.Sobel(), sparse: true);
        Run("bilateral", i => i.Bilateral(2));
        Run("oilpaint", i => i.OilPaint(2));
        Run("guided", i => i.GuidedFilter(4));
        Run("nlm", i => i.NonLocalMeans(2));
        Run("clahe", i => i.Clahe());
        Run("equalize", i => i.EqualizeHistogram());
        Run("vignette", i => i.Vignette());
        Run("local-laplacian", i => i.LocalLaplacian(3, 8));
        Run("resize", i => i.Resize(Math.Max(8, i.Width / 2), Math.Max(8, i.Height / 2)));
        Run("rotate", i => i.Rotate90());

        var (mean, deviation, nan) = Statistics(fused);
        var usable = mean is > 0.02f and < 0.98f && deviation > 0.01f && nan == 0;

        return
        [
            name,
            $"{width}×{height}",
            image.Format.ToString(),
            Ms(dehazeWatch),
            Ms(fuseWatch),
            $"{mean.ToString("F3", CultureInfo.InvariantCulture)} / {deviation.ToString("F3", CultureInfo.InvariantCulture)}",
            usable
                ? (failures.Count == 0 ? "可用" : "**算子异常**: " + string.Join(", ", failures))
                : $"**异常**（NaN {nan}）",
        ];
    }

    /// <summary>A copy scaled by <paramref name="exposure"/>, clipped to the displayable range.</summary>
    private static Image Expose(Image source, float exposure)
    {
        var copy = source.Clone();
        var span = copy.WritablePixels;
        var bytes = copy.Format.BytesPerPixel();
        for (var i = 0; i < span.Length; i += bytes)
        {
            for (var c = 0; c < 3; c++)
            {
                var offset = i + (c * (bytes / 4));
                switch (copy.Format)
                {
                    case WorkingFormat.Rgba8:
                        span[offset] = (byte)Math.Clamp(span[offset] * exposure, 0f, 255f);
                        break;

                    case WorkingFormat.Rgba16:
                        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                        var scaled = Math.Clamp(value * exposure, 0f, 65535f);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), (ushort)scaled);
                        break;

                    default:
                        var f = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset, 4));
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset, 4),
                            Math.Clamp(f * exposure, 0f, 1f));
                        break;
                }
            }
        }

        return copy;
    }

    private static (float Mean, float Deviation, int Nan) Statistics(Image image)
    {
        var span = image.WritablePixels;
        var bytes = image.Format.BytesPerPixel();
        double sum = 0;
        double squares = 0;
        var count = 0;
        var nan = 0;
        for (var i = 0; i < span.Length; i += bytes)
        {
            for (var c = 0; c < 3; c++)
            {
                var offset = i + (c * (bytes / 4));
                float value = image.Format switch
                {
                    WorkingFormat.Rgba8 => span[offset] / 255f,
                    WorkingFormat.Rgba16 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2)) / 65535f,
                    _ => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset, 4)),
                };

                if (float.IsNaN(value) || float.IsInfinity(value))
                    nan++;

                sum += value;
                squares += (double)value * value;
                count++;
            }
        }

        if (count == 0)
            return (0, 0, nan);

        var mean = sum / count;
        var variance = Math.Max(0, (squares / count) - (mean * mean));
        return ((float)mean, (float)Math.Sqrt(variance), nan);
    }

    private static string Ms(Stopwatch watch) =>
        watch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);

    private static List<string> Find()
    {
        var files = new List<string>();
        foreach (var directory in new[]
                 {
                     "samples/SimdImaging.Lab/wwwroot/samples",
                     "tests/Fixtures/.cache",
                 })
        {
            if (!Directory.Exists(directory))
                continue;

            files.AddRange(Directory.EnumerateFiles(directory)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal));
        }

        return files;
    }
}
