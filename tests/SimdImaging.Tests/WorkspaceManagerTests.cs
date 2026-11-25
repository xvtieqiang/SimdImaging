using SimdImaging.Abstractions;
using SimdImaging.Processing;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>The reservation account: peak, release, limit, and the plan/transcode integration.</summary>
public sealed class WorkspaceManagerTests
{
    [Fact]
    public void Reserve_RecordsThePeak_AndReleases()
    {
        var manager = new WorkspaceManager();
        using (manager.Reserve(100, "a"))
        {
            manager.OutstandingBytes.ShouldBe(100);
            using (manager.Reserve(50, "b"))
            {
                manager.OutstandingBytes.ShouldBe(150);
                manager.PeakBytes.ShouldBe(150);
            }

            manager.OutstandingBytes.ShouldBe(100);
        }

        manager.OutstandingBytes.ShouldBe(0);
        manager.PeakBytes.ShouldBe(150);
    }

    [Fact]
    public void Reserve_AboveTheLimit_ThrowsAndDoesNotLeak()
    {
        var manager = new WorkspaceManager(100);
        Should.Throw<InvalidOperationException>(() => manager.Reserve(101, "too large"));
        manager.OutstandingBytes.ShouldBe(0);
    }

    [Fact]
    public void APlanReservation_IsVisibleToTheManager()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var manager = new WorkspaceManager();
        using var result = new ProcessingPlan().Add(PlanOps.GaussianBlur(1f))
            .ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions { Workspace = manager });

        manager.PeakBytes.ShouldBeGreaterThan(0);
        manager.OutstandingBytes.ShouldBe(0);
    }

    [Fact]
    public void APlanAboveTheManagerLimit_IsRefused()
    {
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);
        var manager = new WorkspaceManager(16);
        Should.Throw<InvalidOperationException>(() => new ProcessingPlan().Add(PlanOps.GaussianBlur(1f))
            .ExecuteWithOptions(image[0].Pixels.View, new ExecuteOptions { Workspace = manager }));
        manager.OutstandingBytes.ShouldBe(0);
    }

    [Fact]
    public void ATranscodeReport_CarriesThePeak()
    {
        var source = Path.Combine(Path.GetTempPath(), $"simd-ws-{Guid.NewGuid():N}.png");
        var destination = Path.Combine(Path.GetTempPath(), $"simd-ws-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8))
                Image.Save(image, source);

            var report = ImageTranscode.From(source).Invert().Run(destination);
            report.PeakWorkingBytes.ShouldBeGreaterThan(0);
            report.PredictedWorkingBytes.ShouldBeGreaterThan(0);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }
}
