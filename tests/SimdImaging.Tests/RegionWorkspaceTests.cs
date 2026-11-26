using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// A region run allocates windows and declares scratch, so its peak must reach the same WorkspaceManager as the
/// frame path. Before this, RegionExecutor only checked WorkingSetBudget and reported a peak of the materialised
/// image, which made two runs with the same limit disagree about what they had held.
/// </summary>
public sealed class RegionWorkspaceTests
{
    [Fact]
    public void ARegionRun_ReservesItsWindowsInTheWorkspace()
    {
        var source = Path.Combine(Path.GetTempPath(), $"simd-region-ws-{Guid.NewGuid():N}.tif");
        var destination = Path.Combine(Path.GetTempPath(), $"simd-region-ws-{Guid.NewGuid():N}.png");
        try
        {
            var pixels = new byte[64 * 48 * 3];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(i * 37);
            File.WriteAllBytes(source, TiffFixture.Strips(64, 48, PixelFormat.Rgb24, pixels));

            var workspace = new WorkspaceManager();
            var options = TranscodeOptions.Default with
            {
                Execute = new ExecuteOptions { Workspace = workspace, MaxDegreeOfParallelism = 1 },
            };

            var report = ImageTranscode.From(source)
                .WithOptions(options)
                .Region(new PixelRect(8, 6, 32, 24))
                .Invert()
                .Run(destination);

            report.PeakWorkingBytes.ShouldBe(workspace.PeakBytes);
            report.PeakWorkingBytes.ShouldBeGreaterThan(0);
            report.PredictedWorkingBytes.ShouldBeGreaterThan(0);
            report.PeakWorkingBytes.ShouldBeLessThanOrEqualTo(report.PredictedWorkingBytes);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    /// <summary>
    /// The rectangle is a buffer the facade rents, so the facade must reserve it before the rent. Before this the
    /// buffer was rented first and the workspace refusal arrived afterwards, naming the windows: a limit that could
    /// not be met still paid for the full allocation. The assertion on the pool — not only on the exception — is what
    /// pins the ordering.
    /// </summary>
    [Fact]
    public void ARegionAboveTheWorkspaceLimit_IsRefusedBeforeItsTargetIsRented()
    {
        var source = Path.Combine(Path.GetTempPath(), $"simd-region-ws-{Guid.NewGuid():N}.tif");
        var destination = Path.Combine(Path.GetTempPath(), $"simd-region-ws-{Guid.NewGuid():N}.png");
        try
        {
            var pixels = new byte[64 * 48 * 3];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(i * 37);
            File.WriteAllBytes(source, TiffFixture.Strips(64, 48, PixelFormat.Rgb24, pixels));

            var pool = new CountingPool();
            var workspace = new WorkspaceManager(limitBytes: 1);
            var options = TranscodeOptions.Default with
            {
                Execute = new ExecuteOptions { Workspace = workspace, MaxDegreeOfParallelism = 1 },
                Region = new RegionOptions { Pool = pool },
            };

            var error = Should.Throw<InvalidOperationException>(() => ImageTranscode.From(source)
                .WithOptions(options)
                .Region(new PixelRect(8, 6, 32, 24))
                .Invert()
                .Run(destination));

            error.Message.ShouldContain("region destination");
            pool.Rented.ShouldBe(0, "the target was refused before it was rented");
            workspace.OutstandingBytes.ShouldBe(0);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }
}
