using System.Diagnostics;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>Why a plan was not run over a rectangle instead of a whole image.</summary>
public enum RegionRefusal : byte
{
    /// <summary>Nothing refused it; the plan ran.</summary>
    None = 0,

    /// <summary>A <see cref="OpCategory.Global"/> step needs a whole-image statistic before it writes a pixel, and a
    /// rectangle cannot supply it.</summary>
    GlobalStep,

    /// <summary>A <see cref="OpCategory.Composite"/> step combines two inputs; a region source supplies one.</summary>
    CompositeStep,

    /// <summary>A step reads the whole source image and not just its window (the general convolution's Mirror and
    /// Wrap borders). A window would reflect about the window edge and seam.</summary>
    StepNeedsWholeSource,

    /// <summary>The plan ends with a size change; a region run produces the rectangle at its own size. Use
    /// <c>Image.ResizeRegion</c> for "decode a rectangle, then resize it".</summary>
    GeometricStep,

    /// <summary>A step changes the working format, which the region path's single destination format cannot carry.</summary>
    FormatChange,
}

/// <summary>What <see cref="ProcessingPlan.TryExecuteRegion"/> did.</summary>
/// <param name="Refusal">Why the plan did not run; <see cref="RegionRefusal.None"/> when it did.</param>
/// <param name="BandCount">Bands the region was processed in; 0 when refused.</param>
/// <param name="WorkerCount">Workers the last band's row loop used; 0 when refused.</param>
public readonly record struct RegionResult(RegionRefusal Refusal, int BandCount, int WorkerCount)
{
    /// <summary>True when the plan ran.</summary>
    public bool Succeeded => Refusal == RegionRefusal.None;

    /// <summary>The step that caused the refusal, when one did.</summary>
    public string? RefusingStep { get; init; }
}

/// <summary>
/// Runs a plan over one rectangle of a <see cref="IRegionSource"/>: the two-dimensional counterpart of the row
/// pump. A band's window is widened by every step's halo in both directions, filled from the source, and handed to
/// <c>FusedExecutor.RunWindow</c> exactly as the whole-image path hands it a slice — the inner rectangle is
/// then copied out. The halo recursion, the clamping and the rectangle arithmetic are therefore the same code in
/// all three paths, which is why the result is byte-identical rather than similar.
/// <para>
/// X halo is carried by the window being <em>wider</em>: every rectangle <c>FusedExecutor.RunWindow</c>
/// builds spans the window's full width, so a kernel's horizontal support only needs the pixels to be present.
/// </para>
/// <para>
/// The window is handed over as a standalone image (<c>band 0</c>, <c>bandRows = window height</c>,
/// <c>winTop 0</c>), so every step writes the whole window. Output rows outside the requested rectangle are
/// computed and discarded: that is the price of reusing the loop unchanged, and it is bounded by the halo, not by
/// the image.
/// </para>
/// </summary>
internal static class RegionExecutor
{
    /// <summary>The plan-level conditions, mapped from the single classifier the row pump uses.</summary>
    internal static RegionRefusal Classify(IReadOnlyList<PlanStep> steps, out string? refusingStep) =>
        Classify(steps, hasResize: false, out refusingStep);

    internal static RegionRefusal Classify(IReadOnlyList<PlanStep> steps, bool hasResize, out string? refusingStep)
    {
        if (hasResize)
        {
            refusingStep = "Resize";
            return RegionRefusal.GeometricStep;
        }

        var refusal = StreamingExecutor.Classify(steps, out refusingStep);
        return refusal switch
        {
            StreamRefusal.None => RegionRefusal.None,
            StreamRefusal.GlobalStep => RegionRefusal.GlobalStep,
            StreamRefusal.CompositeStep => RegionRefusal.CompositeStep,
            StreamRefusal.StepNeedsWholeSource => RegionRefusal.StepNeedsWholeSource,
            _ => throw new UnreachableException(
                $"StreamRefusal.{refusal} has no region equivalent; the classifier returned a value it never used to."),
        };
    }

    public static RegionResult Execute(ProcessingPlan plan, IRegionSource source, PixelRect region,
        PixelView destination, ExecuteOptions? options, IPixelPool? pool)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new ExecuteOptions();

        var width = source.Width;
        var height = source.Height;
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"A region source must have a positive size; got {width}x{height}.", nameof(source));
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"The region is empty: {region}.", nameof(region));
        if (!region.FitsIn(width, height))
            throw new ArgumentException($"The region {region} does not lie inside {width}x{height}.", nameof(region));
        if (destination.Width != region.Width || destination.Height != region.Height)
            throw new ArgumentException(
                $"The destination is {destination.Width}x{destination.Height}; the region is {region.Width}x{region.Height}.",
                nameof(destination));
        if (destination.Format != source.Format)
            throw new ArgumentException(
                $"The destination is {destination.Format}; the source writes {source.Format}.", nameof(destination));

        var steps = plan.Steps;
        var refusal = Classify(steps, plan.HasResize, out var refusingStep);
        if (refusal != RegionRefusal.None)
            return new RegionResult(refusal, 0, 0) { RefusingStep = refusingStep };

        var haloX = 0;
        var haloY = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            haloX += steps[i].HaloX;
            haloY += steps[i].HaloY;
        }

        var maxWindowCols = Math.Min(width, region.Width + (2 * haloX));

        // Bands run one at a time, so the band height has to keep its own row loop parallel — the whole-image band
        // rule gives 64 rows to a 512-wide region, which falls below the parallel threshold and runs every operator
        // serially (measured 3–5× slower than 192- and 256-row bands on the same pixels).
        var bandRows = SequentialBands.For(plan, maxWindowCols, region.Height, source.Format,
            options.MaxDegreeOfParallelism);
        var maxWindowRows = Math.Min(height, bandRows + (2 * haloY));
        var bytesPerPixel = source.Format.BytesPerPixel();

        WorkingSetBudget.Ensure(steps, options, maxWindowCols, height, maxWindowCols, maxWindowRows, source.Format,
            windows: steps.Count > 1 ? 4 : 2,
            extraBytes: (long)region.Width * region.Height * bytesPerPixel,
            pathName: "The region");

        var cancellation = options.CancellationToken;
        cancellation.ThrowIfCancellationRequested();

        // The band loop is sequential (a source is read one window at a time), so the parallelism has to come from
        // inside the band. Without this scope the plan adapters' unconditional EnterNested would force every
        // operator onto one thread and the whole region would run serially — correct, and several times slower.
        using var pump = RowParallel.EnterTopLevel(options.MaxDegreeOfParallelism);

        if (steps.Count == 0)
        {
            source.ReadRegion(region, destination);
            return new RegionResult(RegionRefusal.None, 1, 1);
        }

        using var sourceWindow = WorkingBuffer.Rent(maxWindowCols, maxWindowRows, source.Format, pool);
        using var outputWindow = WorkingBuffer.Rent(maxWindowCols, maxWindowRows, source.Format, pool);

        // A single step writes straight into the output window; only a chain needs the ping-pong pair. Renting them
        // anyway would make the estimate above a lie by two windows.
        using var scratchA = steps.Count > 1 ? WorkingBuffer.Rent(maxWindowCols, maxWindowRows, source.Format, pool) : null;
        using var scratchB = steps.Count > 1 ? WorkingBuffer.Rent(maxWindowCols, maxWindowRows, source.Format, pool) : null;

        var bands = 0;
        for (var bandTop = 0; bandTop < region.Height; bandTop += bandRows)
        {
            cancellation.ThrowIfCancellationRequested();
            var bandHeight = Math.Min(bandRows, region.Height - bandTop);

            var winX = Math.Max(0, region.X - haloX);
            var winY = Math.Max(0, (region.Y + bandTop) - haloY);
            var winRight = Math.Min(width, region.X + region.Width + haloX);
            var winBottom = Math.Min(height, region.Y + bandTop + bandHeight + haloY);
            var winWidth = winRight - winX;
            var winHeight = winBottom - winY;

            var sourceView = sourceWindow.View.Slice(new PixelRect(0, 0, winWidth, winHeight));
            source.ReadRegion(new PixelRect(winX, winY, winWidth, winHeight), sourceView);

            var outputView = outputWindow.View.Slice(new PixelRect(0, 0, winWidth, winHeight));
            var a = scratchA is null ? default : scratchA.View.Slice(new PixelRect(0, 0, winWidth, winHeight));
            var b = scratchB is null ? default : scratchB.View.Slice(new PixelRect(0, 0, winWidth, winHeight));

            // The window starts at winY and this call owns [bandTop, bandTop+bandHeight) in image rows, so every
            // step's rectangle shrinks by its remaining halo exactly as it does in the band pump.
            FusedExecutor.RunWindow(winWidth, height, sourceView, outputView, winY,
                region.Y + bandTop, region.Y + bandTop + bandHeight, steps, 0, steps.Count, haloY, a, b);

            var sourceX = region.X - winX;
            var sourceY = (region.Y + bandTop) - winY;
            var rowBytes = region.Width * bytesPerPixel;
            for (var y = 0; y < bandHeight; y++)
                outputView.Row(sourceY + y).Slice(sourceX * bytesPerPixel, rowBytes)
                    .CopyTo(destination.Row(bandTop + y));

            bands++;
        }

        return new RegionResult(RegionRefusal.None, bands, Math.Max(1, RowParallel.LastWorkerCount));
    }
}
