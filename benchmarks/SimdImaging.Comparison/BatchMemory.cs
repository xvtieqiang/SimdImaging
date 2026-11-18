using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Does a batch grow? One chain, N images, sampled after every image: a flat line means the pools and the scratch
/// caches are reused, a slope means each image leaves something behind.
/// </summary>
internal static class BatchMemory
{
    public static void Run(int width, int height, int count)
    {
        var plan = new ProcessingPlan()
            .Add(PlanOps.Sharpen(0.6f))
            .Add(PlanOps.Emboss(0.6f))
            .Add(PlanOps.Sobel());

        var data = Fixture.Make(width, height);
        using var process = System.Diagnostics.Process.GetCurrentProcess();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var baseline = process.WorkingSet64;
        Console.WriteLine($"{width}x{height} · 基线 {baseline / 1048576} MiB · 每张图处理后的工作集");

        for (var i = 1; i <= count; i++)
        {
            using (var image = Fixture.Ours(data, width, height))
            using (image.Apply(plan))
            {
            }

            if (Environment.GetEnvironmentVariable("SIMDIMAGING_TRIM_EACH") == "1")
                MemoryBudget.Trim();

            process.Refresh();
            var raw = (process.WorkingSet64 - baseline) / 1048576;

            // Without a collection the heap is allowed to grow to avoid one, which is not the same as retaining:
            // the second number is what is actually still held once the garbage has been taken out.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            var afterGc = (process.WorkingSet64 - baseline) / 1048576;

            Console.WriteLine($"  #{i,3}  未回收 {raw,5} MiB   强制回收后 {afterGc,5} MiB   " +
                $"托管堆 {GC.GetTotalMemory(false) / 1048576} MiB");
        }
    }
}
