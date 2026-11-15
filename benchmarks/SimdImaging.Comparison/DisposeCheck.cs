using System.Diagnostics;
using System.Globalization;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// What actually happens to someone who never writes <c>using</c>.
/// </summary>
/// <remarks>
/// <para>
/// The library has no finalizers: <c>Image</c> owns frames, a frame owns a <c>WorkingBuffer</c>, and the buffer
/// holds a lease on pooled memory. Nothing but <c>Dispose</c> hands that lease back, so an image that is dropped
/// is still collected by the GC — this is not a leak — but the pool never sees the buffer again and the next call
/// rents a fresh one.
/// </para>
/// <para>
/// The two columns below are the same work with only that difference. Allocation climbing while the resident set
/// climbs more slowly is the pool failing to do its job; a resident set that keeps climbing after a forced
/// collection would mean something is holding on, which is a different and worse story.
/// </para>
/// </remarks>
internal static class DisposeCheck
{
    private const int Rounds = 12;

    public static void Write(Report report, int width, int height)
    {
        report.Heading(2, $"I 组 · 忘了 using 会怎样（{width}×{height}，{Rounds} 次）");
        report.Line("> 同一份工作，只差有没有 `Dispose`。**分配**是这段时间内申请的总字节；**峰值增量**是采样到的");
        report.Line("> `WorkingSet64` 最大上升；**强制回收后常驻**是把垃圾收掉之后还在的部分——它涨才说明有人抓着不放。");
        report.Line();

        var data = Fixture.Make(width, height);
        using var source = Fixture.Ours(data, width, height);

        var rows = new List<string[]>();
        foreach (var dispose in new[] { true, false })
        {
            rows.Add(Measure(source, dispose, width, height));
        }

        report.Table(["做法", "分配总量", "峰值增量", "强制回收后常驻", "每轮分配"], rows);
        report.Line("> 读法：不写 `using` 时**分配总量**会明显更高（池不再复用），常驻在强制回收后会回落——");
        report.Line("> 那就不是泄漏，是**池失效 + GC 压力**。如果常驻收不回去，说明有引用链把它钉住了，那是另一回事。");
    }

    private static string[] Measure(Image source, bool dispose, int width, int height)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var liveBefore = GC.GetTotalMemory(true);
        var wsBefore = Process.GetCurrentProcess().WorkingSet64;

        long peak = 0;
        var gate = new object();
        using var cancel = new CancellationTokenSource();
        var sampler = Task.Run(() =>
        {
            var process = Process.GetCurrentProcess();
            while (!cancel.IsCancellationRequested)
            {
                process.Refresh();
                var rise = process.WorkingSet64 - wsBefore;
                lock (gate)
                {
                    if (rise > peak)
                        peak = rise;
                }

                Thread.Sleep(1);
            }
        });

        for (var i = 0; i < Rounds; i++)
        {
            var result = source.Pipe().GaussianBlur(1f).ToImage();
            if (dispose)
                result.Dispose();
        }

        cancel.Cancel();
        sampler.Wait();

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var live = GC.GetTotalMemory(true) - liveBefore;

        return
        [
            dispose ? "Dispose（using）" : "**不 Dispose**",
            MiB(allocated),
            MiB(peak),
            MiB(live),
            MiB(allocated / (double)Rounds),
        ];
    }

    private static string MiB(long bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    private static string MiB(double bytes) => MiB((long)bytes);
}
