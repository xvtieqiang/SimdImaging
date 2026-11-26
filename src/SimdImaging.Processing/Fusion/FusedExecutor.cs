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
                    // The second rent can fail; keep the first rent inside the try so it is returned even when
                    // the pair cannot be completed.
                    next = WorkingBuffer.Rent(width, maxWindowRows, source.Format, pool);
                    for (var b = start; b < end; b++)
                        RunBand(source, output.View, steps, first, count, b, bandRows, haloTotal,
                            window.View, next.View);
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
        var workers = Math.Max(1, Math.Min(degree > 0 ? degree : Environment.ProcessorCount, bandCount));
        var windowRows = Math.Min(height, bandRows + 2 * haloTotal);
        var windowBytes = (long)width * windowRows * bytesPerPixel;
        return (long)width * height * bytesPerPixel + 2 * workers * windowBytes;
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
        RunWindow(width, height,
            source.Slice(new PixelRect(0, winTop, width, windowRows)),
            output.Slice(new PixelRect(0, winTop, width, windowRows)),
            winTop, steps, first, count, band, bandRows, haloTotal, window, next);
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
            step.Invoke(previous, destination, destinationRect, previousRect);

            if (isLast)
                break;

            previous = destination;
            previousRect = destinationRect;
        }
    }
}
