using System.Diagnostics;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>
/// What the operators touched today actually cost in memory: peak working-set growth during the call, and
/// what is still resident after it returns and the result is disposed.
/// </summary>
/// <remarks>
/// <para>
/// In-process rather than a fresh child per cell, which is what <see cref="MemSteady"/> does. That trades a
/// little rigour for coverage: the interesting operators here are not in <see cref="Cases.Build"/>, and adding
/// them there would change the nineteen-case throughput table. The baseline is taken after a forced
/// collection, and every result is disposed before the post-call read, so what the live number reports is
/// what the pool and <c>FloatScratch</c> kept.
/// </para>
/// <para>
/// The question it exists to answer is not "who is smaller" but "did today's parallel band work leave a
/// per-worker buffer set behind". Five workers each holding eight planes is 88 MB that no longer shows up in
/// an allocation counter, because it was pooled rather than freed.
/// </para>
/// </remarks>
internal static class MemoryProbe
{
    private const int Repeats = 5;

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, $"内存探针（{width}×{height}）");
        report.Line("> 峰值 = 调用期间 `WorkingSet64` 相对基线的最大增量（采样 ~1 ms）；"
                  + "`live` = 结果 `Dispose()` 并强制 GC 后 `GC.GetTotalMemory(true)` 相对基线的增量。");
        report.Line("> `live` 只剩池与 `FloatScratch` 保留的缓冲——**如果它随尺寸线性涨到几百 MB，那就是真的高**。");
        report.Line();

        var data = Fixture.Make(width, height);
        using var source = Fixture.Ours(data, width, height);

        var ops = new (string Id, Action<OurImage> Run)[]
        {
            ("resize-bicubic", i => { using var r = i.Pipe().Resize(width / 2, height / 2, Resampler.Bicubic).ToImage(); }),
            ("gaussian-blur", i => { using var r = i.Pipe().GaussianBlur(3f).ToImage(); }),
            ("guided-filter", i => { using var r = i.Pipe().GuidedFilter(4, 1e-4f).ToImage(); }),
            ("guided.cross", i => { using var r = i.Pipe().GuidedFilter(i, 4, 1e-4f).ToImage(); }),
            ("oilpaint", i => { using var r = i.Pipe().OilPaint(2, 20).ToImage(); }),
            ("quantize-64", i => { using var r = i.Pipe().Quantize(64).ToImage(); }),
            ("seam-50", i => { using var r = i.Pipe().RemoveSeams(SeamAxis.Vertical, 50).ToImage(); }),
        };

        var rows = new List<string[]>();
        foreach (var (id, run) in ops)
        {
            run(source);   // warm the pools this operator uses, so the peak below is steady-state
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var liveBefore = GC.GetTotalMemory(true);
            var wsBefore = Process.GetCurrentProcess().WorkingSet64;

            long peak = 0;
            var sampler = new Thread(() =>
            {
                var process = Process.GetCurrentProcess();
                while (true)
                {
                    var current = process.WorkingSet64 - wsBefore;
                    if (current > peak)
                        peak = current;
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            sampler.Start();

            for (var i = 0; i < Repeats; i++)
                run(source);

            sampler.Join(20);

            var allocated = (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / Repeats;
            var liveAfter = GC.GetTotalMemory(true) - liveBefore;
            rows.Add([id, MiB(peak), MiB(liveAfter), MiB(allocated)]);
        }

        report.Table(["算子", "峰值增量", "调用后常驻", "每次分配"], rows);
    }

    private static string MiB(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MiB";
}
