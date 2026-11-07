using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// One step of a plan: what it is called, how much context it needs, and the code that fills a destination
/// rectangle. The kernel receives absolute coordinates on both views, so it never has to reason about which
/// band it is in.
/// </summary>
/// <param name="source">The previous step's image, in full.</param>
/// <param name="destination">The buffer being filled. Its contents are undefined; the kernel must write every
/// byte of <paramref name="destinationRect"/>, alpha included, or the result depends on what the pool
/// happened to hand out.</param>
/// <param name="destinationRect">The rectangle this call must fill, in destination coordinates.</param>
/// <param name="sourceRect">The part of <paramref name="source"/> that rectangle may read, halo included and
/// clamped to the image.</param>
public delegate void OpKernel(PixelView source, PixelView destination, PixelRect destinationRect,
    PixelRect sourceRect);

/// <summary>One named operator in a <see cref="ProcessingPlan"/>.</summary>
/// <param name="Name">Stable name, used in diagnostics.</param>
/// <param name="Category">How the step reads its source; <see cref="OpCategory.Global"/> and
/// <see cref="OpCategory.Geometric"/> are run whole.</param>
/// <param name="HaloX">Columns read on each side of a destination pixel.</param>
/// <param name="HaloY">Rows read on each side.</param>
/// <param name="Kernel">The code that fills a rectangle.</param>
public readonly record struct PlanStep(string Name, OpCategory Category, int HaloX, int HaloY, OpKernel Kernel);

/// <summary>
/// Called after a step finished, while that step's output is still alive.
/// <para>
/// <b>The view is only valid for the duration of the call.</b> The buffer behind it is the plan's
/// ping-pong slot: it becomes the next step's source and is disposed on the following iteration.
/// Copy whatever you need — and expect the copy to cost more than the step did, so this is a
/// verification hook, not something to leave switched on in production.
/// </para>
/// </summary>
/// <param name="index">Zero-based step index.</param>
/// <param name="name">The step's name, as passed to <see cref="ProcessingPlan.Add"/>.</param>
/// <param name="result">The step's full output.</param>
public delegate void PlanObserver(int index, string name, PixelView result);

/// <summary>
/// A sequence of operators executed over one source with a bounded working set: same-size steps run in row
/// bands, so a neighbourhood kernel only ever sees its own band plus its halo.
/// <para>
/// The destination of each step is one buffer the size of the image — that part is unavoidable — but the
/// scratch a kernel allocates inside a call is band-sized, which is what keeps a large-radius filter from
/// materialising several full planes.
/// </para>
/// <para>
/// <see cref="OpCategory.Global"/> steps (histogram, quantise, CLAHE) still run whole: their answer depends on
/// every pixel, so splitting them would change the result. <see cref="OpCategory.Geometric"/> steps are
/// rejected — they change the image size, which a same-size chain cannot represent.
/// </para>
/// </summary>
public sealed class ProcessingPlan
{
    /// <summary>Rows per band when a step does not pick its own.</summary>
    public const int DefaultBandRows = 128;

    private readonly List<PlanStep> _steps = [];

    /// <summary>The steps, in execution order.</summary>
    public IReadOnlyList<PlanStep> Steps => _steps;

    /// <summary>Rows per band for this plan.</summary>
    public int BandRows { get; init; } = DefaultBandRows;

    /// <summary>Appends a step.</summary>
    /// <param name="name">Stable name.</param>
    /// <param name="category">How the step reads its source.</param>
    /// <param name="kernel">The code that fills a rectangle.</param>
    /// <param name="haloX">Columns of context, for neighbourhood kernels.</param>
    /// <param name="haloY">Rows of context, for neighbourhood kernels.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or <paramref name="kernel"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A halo is negative.</exception>
    /// <exception cref="NotSupportedException">The category changes the image size.</exception>
    public ProcessingPlan Add(string name, OpCategory category, OpKernel kernel, int haloX = 0, int haloY = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentOutOfRangeException.ThrowIfNegative(haloX);
        ArgumentOutOfRangeException.ThrowIfNegative(haloY);
        if (category == OpCategory.Geometric)
            throw new NotSupportedException(
                "A geometric step changes the image size, which a same-size plan cannot represent; run it outside the plan.");

        _steps.Add(new PlanStep(name, category, haloX, haloY, kernel));
        return this;
    }

    /// <summary>
    /// Runs every step over <paramref name="source"/>, band by band where the category allows it, and returns
    /// the last step's buffer. An empty plan returns a copy.
    /// </summary>
    /// <param name="source">The input pixels.</param>
    /// <param name="pool">Where the intermediate and output buffers are rented.</param>
    /// <param name="observe">Optional per-step hook, called while the step's output is still alive. Off by
    /// default: the callback is invoked on the calling thread, once per step, after that step finished.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="BandRows"/> is not positive.</exception>
    /// <summary>
    /// Runs every step and returns a new buffer.
    /// <para><b>Kernels may be invoked concurrently.</b> Bands execute in parallel, so a kernel must be
    /// thread-safe and must only touch the rows inside <c>destinationRect</c>. This changed on 2026-11-06:
    /// the band loop used to be a plain sequential <c>for</c>, which left the whole plan single-threaded and
    /// depended on each operator's own <c>Parallel.For</c> re-entering the pool once per band. Parallel bands
    /// took a three-step point chain from 14.5 ms to 10.1 ms and a blur chain from 67.3 ms to 25.9 ms.</para>
    /// <para><b>There is still one full-size buffer per step.</b> The plan fuses the <i>traversal</i>, not the
    /// allocation: each step rents one image-sized destination and the previous one is disposed right after,
    /// so the live set is two full images regardless of step count. That is deliberate — it is what makes
    /// <paramref name="observe"/> able to see every intermediate. A fused executor would drop that ability.</para>
    /// </summary>
    public PixelBuffer Execute(PixelView source, IPixelPool? pool = null, PlanObserver? observe = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BandRows);
        if (_steps.Count == 0)
            return Geometry.Transforms.Copy(source, pool);

        var width = source.Width;
        var height = source.Height;
        var current = source;
        PixelBuffer? owned = null;

        try
        {
            for (var i = 0; i < _steps.Count; i++)
            {
                var step = _steps[i];
                var destination = PixelBuffer.Rent(width, height, current.Format, pool);
                var whole = new PixelRect(0, 0, width, height);

                if (step.Category == OpCategory.Global)
                {
                    step.Kernel(current, destination.View, whole, whole);
                }
                else
                {
                    // Bands run in PARALLEL. The old loop was a plain `for`, so a plan was effectively
                    // single-threaded and leaned entirely on each operator's own Parallel.For — which meant
                    // re-entering the pool once per band and rebuilding the operator's per-call setup
                    // (a 256-entry LUT, for one) 24 times per 1080p step. Measured: three point steps cost
                    // 14.5 ms that way against 5.1 ms for the same three operators called directly.
                    // Bands write disjoint rows, so the worker count cannot change a byte.
                    var bandCount = (height + BandRows - 1) / BandRows;
                    RowParallel.ForBands(bandCount, (start, end) =>
                    {
                        for (var b = start; b < end; b++)
                        {
                            var top = b * BandRows;
                            var band = new PixelRect(0, top, width, Math.Min(BandRows, height - top));
                            step.Kernel(current, destination.View, band,
                                MapSource(band, step.HaloX, step.HaloY, width, height));
                        }
                    });
                }

                // Before the dispose below, not after: `destination` is what `owned` is about to become, and
                // the *previous* owned buffer is still the one being released. Calling the observer after the
                // swap would hand out a view of a buffer the next iteration is about to dispose.
                observe?.Invoke(i, step.Name, destination.View);

                owned?.Dispose();
                owned = destination;
                current = destination.View;
            }
        }
        catch
        {
            owned?.Dispose();
            throw;
        }

        return owned!;
    }

    /// <summary>The destination band widened by the halo and clamped to the image.</summary>
    private static PixelRect MapSource(PixelRect band, int haloX, int haloY, int width, int height)
    {
        var left = Math.Max(0, band.X - haloX);
        var top = Math.Max(0, band.Y - haloY);
        var right = Math.Min(width, band.X + band.Width + haloX);
        var bottom = Math.Min(height, band.Y + band.Height + haloY);

        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
