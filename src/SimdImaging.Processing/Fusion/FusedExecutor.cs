using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Fusion;

/// <summary>
/// The fused executor: a band-able segment's steps run through band-sized windows instead of one
/// full-size buffer per step. The window contract is the halo recursion — step k writes
/// <c>band ⊕ Σ(halo of steps after k)</c> and reads that plus its own halo, which is exactly the
/// rectangle the step-by-step executor would hand it, so every output byte is identical.
/// <para>All views are slices with the SAME origin and height (<c>winTop</c>, <c>windowRows</c>), so a
/// rect is simply "global row − winTop" for both source and destination. Re-slicing an intermediate
/// to the produced rectangle would move its origin and silently shift every later row — that is the
/// bug the first draft of this file had.</para>
/// <para>Windows are clipped to the image at build time, so the clamping matches the step-by-step
/// executor's <c>MapSource</c> semantics; neighbouring bands recompute their overlap rows, which is
/// harmless because a kernel is deterministic in its input.</para>
/// </summary>
internal static class FusedExecutor
{
    /// <summary>
    /// Runs one fused segment (at least two steps) and returns the full-size output. The caller owns
    /// the returned buffer; a throw from a kernel disposes the output before propagating.
    /// </summary>
    public static WorkingBuffer ExecuteSegment(PixelView source, IReadOnlyList<PlanStep> steps, int first,
        int count, int bandRows, int degree, CancellationToken cancellation, IPixelPool? pool)
    {
        var width = source.Width;
        var height = source.Height;
        var haloTotal = 0;
        for (var i = 0; i < count; i++)
            haloTotal += steps[first + i].HaloY;

        // Every band's window is at most this tall; band 0 and the last band are shorter.
        var maxWindowRows = Math.Min(height, bandRows + 2 * haloTotal);
        var bandCount = (height + bandRows - 1) / bandRows;

        var output = WorkingBuffer.Rent(width, height, source.Format, pool);
        try
        {
            RowParallel.ForBands(bandCount, (start, end) =>
            {
                // One window pair per WORKER (not per band): a worker's bands reuse its buffers.
                var window = WorkingBuffer.Rent(width, maxWindowRows, source.Format, pool);
                WorkingBuffer? next = null;
                try
                {
                    // A halo-free segment ping-pongs between the output and ONE scratch window, so the second
                    // rent only exists for segments whose steps read rows outside their band. Halving the rent
                    // halves both the pooled buffers a fused point chain needs and the allocation a cold call
                    // pays for them: 16 windows at 1080p instead of 32, on a chain fusion exists to make cheap.
                    if (haloTotal > 0)
                    {
                        // The second rent can fail; keep the first rent inside the try so it is returned even when
                        // the pair cannot be completed.
                        next = WorkingBuffer.Rent(width, maxWindowRows, source.Format, pool);
                    }
                    for (var b = start; b < end; b++)
                        RunBand(source, output.View, steps, first, count, b, bandRows, haloTotal,
                            window.View, next?.View ?? window.View);
                }
                finally
                {
                    next?.Dispose();
                    window.Dispose();
                }
            }, degree, cancellation);

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>Estimate of the working set for one segment: output + two windows per worker. An
    /// estimate, not a measurement, and documented as such on <c>ExecuteOptions.MaxWorkingBytes</c>.</summary>
    public static long EstimateSegmentBytes(PixelView source, IReadOnlyList<PlanStep> steps, int first,
        int count, int bandRows, int degree) =>
        EstimateSegmentBytes(source.Width, source.Height, source.Format, steps, first, count, bandRows, degree);

    /// <summary>The same arithmetic from geometry alone, so <c>MemoryEstimator</c> can answer before an
    /// image exists. Keep the two in step: the PixelView overload is this one.</summary>
    internal static long EstimateSegmentBytes(int width, int height, WorkingFormat format,
        IReadOnlyList<PlanStep> steps, int first, int count, int bandRows, int degree)
    {
        var bytesPerPixel = format.BytesPerPixel();
        var haloTotal = 0;
        for (var i = 0; i < count; i++)
            haloTotal += steps[first + i].HaloY;

        var bandCount = (height + bandRows - 1) / bandRows;
        var workers = Math.Max(1, Math.Min(RowParallel.ResolveDegree(degree), bandCount));
        var windowRows = Math.Min(height, bandRows + 2 * haloTotal);
        var windowBytes = (long)width * windowRows * bytesPerPixel;
        // A halo-free segment ping-pongs between the output and one scratch window; a halo-carrying one needs
        // two scratch windows because the intermediate rectangles differ from the band.
        return (long)width * height * bytesPerPixel + (haloTotal == 0 ? 1 : 2) * workers * windowBytes;
    }

    private static void RunBand(PixelView source, PixelView output, IReadOnlyList<PlanStep> steps, int first,
        int count, int band, int bandRows, int haloTotal, PixelView window, PixelView next)
    {
        var width = source.Width;
        var height = source.Height;
        var top = band * bandRows;
        var bottom = top + Math.Min(bandRows, height - top);

        // The union window: the band widened by every halo, clamped to the image.
        var winTop = Math.Max(0, top - haloTotal);
        var windowRows = Math.Min(height, bottom + haloTotal) - winTop;

        // Source and destination views share this origin and height, so every rect below is
        // "global row minus winTop" and the two views address the same rows.
        // The scratch buffers are rented at maxWindowRows; hand the kernel only the rows this window owns.
        // A kernel clamps against source.Height, so a taller view would expose rows the last step never wrote
        // (they hold the previous band's data) instead of clamping at the image edge.
        var scratchRect = new PixelRect(0, 0, width, windowRows);
        var sourceBand = source.Slice(new PixelRect(0, winTop, width, windowRows));
        var outputBand = output.Slice(new PixelRect(0, winTop, width, windowRows));
        var scratch = window.Slice(scratchRect);
        if (haloTotal == 0)
        {
            // Without a halo every step reads and writes exactly this band, so the two scratch windows collapse
            // into one: alternate output and scratch, and never invoke a kernel with source and destination
            // aliased.
            RunZeroHaloBand(width, height, sourceBand, outputBand, winTop, steps, first, count, scratch);
            return;
        }

        RunWindow(width, height, sourceBand, outputBand, winTop, steps, first, count, band, bandRows, haloTotal,
            scratch, next.Slice(scratchRect));
    }

    /// <summary>
    /// The band loop over windows the caller already owns: <paramref name="sourceWindow"/> and
    /// <paramref name="outputWindow"/> start at absolute row <paramref name="winTop"/> and have the same height.
    /// The whole-image path slices them out of the full buffers; the streaming path fills them from a row source.
    /// <para>Keeping this one loop for both paths is the reason a streamed result is byte-identical: the halo
    /// recursion, the clamping and the rectangle arithmetic are not re-derived anywhere else.</para>
    /// </summary>
    /// <param name="height">Rows in the whole image, for the bottom clamp. The windows are shorter than this.</param>
    /// <param name="winTop">Absolute row that row 0 of both windows corresponds to.</param>
    internal static void RunWindow(int width, int height, PixelView sourceWindow, PixelView outputWindow,
        int winTop, IReadOnlyList<PlanStep> steps, int first, int count, int band, int bandRows, int haloTotal,
        PixelView window, PixelView next)
    {
        var top = band * bandRows;
        var bottom = top + Math.Min(bandRows, height - top);
        RunWindow(width, height, sourceWindow, outputWindow, winTop, top, bottom, steps, first, count, haloTotal,
            window, next);
    }

    /// <summary>
    /// The same loop for a caller that knows the band's absolute rows (the region executor, whose bands are not
    /// multiples of a height-splitting rule). <paramref name="height"/> is still the whole image, because that is
    /// what the kernels clamp to; <paramref name="top"/> and <paramref name="bottom"/> are the rows this call owns.
    /// </summary>
    internal static void RunWindow(int width, int height, PixelView sourceWindow, PixelView outputWindow,
        int winTop, int top, int bottom, IReadOnlyList<PlanStep> steps, int first, int count, int haloTotal,
        PixelView window, PixelView next)
    {
        var windowRows = sourceWindow.Height;
        EnsureWindow(width, height, sourceWindow, outputWindow, winTop, top, bottom, count, window, next);

        PixelView previous = sourceWindow;
        var previousRect = new PixelRect(0, 0, width, windowRows);

        // remaining starts at Σ all halos and loses step k's halo before step k's rect is computed:
        // after the subtraction it is R_k = Σ_{j>k} h_j, the distance step k must extend past the band.
        var remaining = haloTotal;
        for (var k = 0; k < count; k++)
        {
            var step = steps[first + k];
            var isLast = k == count - 1;
            remaining -= step.HaloY;

            var dstTop = Math.Max(0, top - remaining);
            var dstBottom = Math.Min(height, bottom + remaining);
            var destinationRect = new PixelRect(0, dstTop - winTop, width, dstBottom - dstTop);

            // The last step writes the band itself; the others ping-pong between the two windows.
            PixelView destination = isLast ? outputWindow : (k & 1) == 0 ? window : next;
            EnsureRect(previous, previousRect, "source", first + k);
            EnsureRect(destination, destinationRect, "destination", first + k);
            step.Invoke(previous, destination, destinationRect, previousRect);

            if (isLast)
                break;

            previous = destination;
            previousRect = destinationRect;
        }
    }

    /// <summary>
    /// The window contract every caller must satisfy. These are ordinary runtime checks, not <c>Debug.Assert</c>:
    /// the gates run Release, and a silent off-by-one in the halo recursion produces a plausible but wrong image,
    /// which no assertion compiled out of a Release build would catch.
    /// </summary>
    private static void EnsureWindow(int width, int height, PixelView sourceWindow, PixelView outputWindow,
        int winTop, int top, int bottom, int count, PixelView window, PixelView next)
    {
        var windowRows = sourceWindow.Height;
        if (count <= 0 || width <= 0 || windowRows <= 0)
            throw new InvalidOperationException(
                $"A fused window needs a positive size and at least one step (width={width}, rows={windowRows}, " +
                $"steps={count}).");
        if (top < 0 || bottom > height || bottom <= top)
            throw new InvalidOperationException(
                $"The fused band [{top},{bottom}) is outside the {height}-row image.");
        if (winTop < 0 || winTop > top || winTop + windowRows < bottom)
            throw new InvalidOperationException(
                $"The fused window starts at row {winTop} for {windowRows} rows but must cover the band " +
                $"[{top},{bottom}) of a {height}-row image.");
        if (sourceWindow.Width < width || outputWindow.Width < width || outputWindow.Height < windowRows)
            throw new InvalidOperationException(
                $"The fused source/output windows {sourceWindow.Width}x{sourceWindow.Height} and " +
                $"{outputWindow.Width}x{outputWindow.Height} do not cover {width}x{windowRows}.");
        if (count > 1 &&
            (window.Width < width || window.Height != windowRows ||
             next.Width < width || next.Height != windowRows))
            throw new InvalidOperationException(
                $"The fused scratch windows {window.Width}x{window.Height} and {next.Width}x{next.Height} must " +
                $"both be exactly {width}x{windowRows}. A taller view exposes rows the previous step never wrote, " +
                "so the kernel clamps against stale data instead of the image edge.");
    }

    /// <summary>
    /// The halo-free band loop. Every step's rectangle is the band itself, so the two scratch windows the halo
    /// recursion needs collapse to one: the destinations alternate between the output band and that scratch, and
    /// the parity is chosen so the last step lands on the output. Source and destination are never the same view,
    /// which is what the kernels require.
    /// </summary>
    private static void RunZeroHaloBand(int width, int height, PixelView sourceBand, PixelView outputBand,
        int winTop, IReadOnlyList<PlanStep> steps, int first, int count, PixelView scratch)
    {
        var rows = sourceBand.Height;
        EnsureWindow(width, height, sourceBand, outputBand, winTop, winTop, winTop + rows, count, scratch, scratch);

        var rect = new PixelRect(0, 0, width, rows);
        var previous = sourceBand;
        for (var k = 0; k < count; k++)
        {
            var isLast = k == count - 1;

            // Counted from the END of the chain, because that is the constraint: the last step must write the
            // output. An even number of steps remaining after this one means alternating reaches the output on
            // the last step. Writing the output early is harmless — a later step reads it back and the final
            // step overwrites it — so only the parity matters, never which of the two buffers comes first.
            var stepsAfter = count - 1 - k;
            var destination = isLast || (stepsAfter & 1) == 0 ? outputBand : scratch;
            EnsureRect(previous, rect, "source", first + k);
            EnsureRect(destination, rect, "destination", first + k);
            steps[first + k].Invoke(previous, destination, rect, rect);
            if (isLast)
                break;
            previous = destination;
        }
    }

    private static void EnsureRect(PixelView view, PixelRect rect, string name, int stepIndex)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Width < 0 || rect.Height < 0 ||
            rect.X + rect.Width > view.Width || rect.Y + rect.Height > view.Height)
            throw new InvalidOperationException(
                $"The fused {name} rect ({rect.X},{rect.Y} {rect.Width}x{rect.Height}) of step {stepIndex} " +
                $"escapes its {view.Width}x{view.Height} view.");
    }
}
