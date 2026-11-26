using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Geometry;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Adapters that turn an existing whole-image operator into a plan step. The executor hands a kernel one
/// rectangle at a time; these run the operator on exactly that rectangle and copy the answer back.
/// <para>
/// A neighbourhood step is run on the rectangle widened by its halo and only the inner part is kept. At the
/// image border the halo is clamped, so the operator sees the real edge and its own clamping matches; inside
/// the image the extra rows absorb the operator's edge handling and are discarded.
/// </para>
/// </summary>
internal static class PlanSteps
{
    /// <summary>Adapts a point operator — one whose output pixel depends only on the same input pixel.</summary>
    /// <param name="operator">Runs the operator over a view and returns a new buffer the adapter disposes.</param>
    public static OpKernel Point(Func<PixelView, WorkingBuffer> @operator)
    {
        ArgumentNullException.ThrowIfNull(@operator);

        return (source, destination, destinationRect, _) =>
        {
            var band = source.Slice(destinationRect);

            // The plan already parallelises over bands; letting the operator start its own Parallel.For
            // from inside a band worker oversubscribes the pool. Measured: a three-step point chain cost
            // 14.5 ms with the nesting and 5.9 ms without, against 5.1 ms for the same three operators
            // called directly.
            RowParallel.EnterNested();
            try
            {
                using var result = @operator(band);
                CopyRows(result.View, 0, destination, destinationRect.Y, destinationRect.Height);
            }
            finally
            {
                RowParallel.ExitNested();
            }
        };
    }

    /// <summary>
    /// Adapts a point operator that has a byte-domain table. On Rgba8 the table is applied straight into the
    /// destination, which removes the band buffer and the copy <see cref="Point"/> needs. Every other format takes
    /// the general path unchanged.
    /// </summary>
    /// <param name="table">The 4 x 256 table, built on first use from the same operator by <c>PlanOps</c>.</param>
    /// <param name="fallback">The general operator, used whatever the table does not cover.</param>
    /// <param name="pixelFloor">Pixels below which building the table costs more than the copy it saves.</param>
    public static OpKernel PointTable(Lazy<byte[]> table, Func<PixelView, WorkingBuffer> fallback, long pixelFloor)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentOutOfRangeException.ThrowIfNegative(pixelFloor);

        return (source, destination, rect, _) =>
        {
            if (source.Format != WorkingFormat.Rgba8 || (long)source.Width * source.Height < pixelFloor)
            {
                RowParallel.EnterNested();
                try
                {
                    using var result = fallback(source.Slice(rect));
                    CopyRows(result.View, 0, destination, rect.Y, rect.Height);
                }
                finally
                {
                    RowParallel.ExitNested();
                }

                return;
            }

            var lut = table.Value;

            // The executor has already split the image into bands and runs them in parallel, so this uses a bare loop:
            // a nested RowParallel.For would run inline anyway and would overwrite LastWorkerCount with 1.
            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            {
                var from = source.Row(y);
                var to = destination.Row(y);
                var start = rect.X * 4;
                var end = start + (rect.Width * 4);

                for (var i = start; i < end; i += 4)
                {
                    to[i] = lut[from[i]];
                    to[i + 1] = lut[256 + from[i + 1]];
                    to[i + 2] = lut[512 + from[i + 2]];
                    to[i + 3] = lut[768 + from[i + 3]];
                }
            }
        };
    }

    /// <summary>Adapts a neighbourhood operator, keeping only the inner rectangle of its answer.</summary>
    /// <param name="operator">Runs the operator over a view and returns a new buffer the adapter disposes.</param>
    /// <param name="haloX">Columns of context the operator reads.</param>
    /// <param name="haloY">Rows of context the operator reads.</param>
    public static OpKernel Neighborhood(Func<PixelView, WorkingBuffer> @operator, int haloX, int haloY,
        IntoKernel? into = null)
    {
        ArgumentNullException.ThrowIfNull(@operator);
        ArgumentOutOfRangeException.ThrowIfNegative(haloX);
        ArgumentOutOfRangeException.ThrowIfNegative(haloY);

        return (source, destination, destinationRect, sourceRect) =>
        {
            var window = source.Slice(sourceRect);

            // The answer covers `sourceRect`; the part we want starts where the destination band starts.
            var offsetY = destinationRect.Y - sourceRect.Y;

            if (into is not null)
            {
                // No intermediate and no copy: the operator writes the band it owns straight into the output.
                RowParallel.EnterNested();
                try
                {
                    into(window, destination, destinationRect.Y, destinationRect.Height, offsetY);
                }
                finally
                {
                    RowParallel.ExitNested();
                }

                return;
            }

            RowParallel.EnterNested();
            try
            {
                using var result = @operator(window);
                CopyRows(result.View, offsetY, destination, destinationRect.Y, destinationRect.Height);
            }
            finally
            {
                RowParallel.ExitNested();
            }
        };
    }

    /// <summary>Adapts a neighbourhood operator that needs to know where its window sits in the image.</summary>
    /// <remarks>
    /// A bordered convolution cannot be banded with <see cref="Neighborhood(Func{PixelView,WorkingBuffer},int,int,IntoKernel?)"/>:
    /// Mirror and Wrap have to reflect or wrap about the <b>image</b> edge, and the operator only sees its window, so
    /// it would reflect about the band edge and produce a seam. The window's origin and the image size are exactly
    /// what tells the two apart, so they are handed over.
    /// </remarks>
    /// <param name="operator">Runs over a window and gets the context that locates it.</param>
    /// <param name="haloX">Columns of context the operator reads.</param>
    /// <param name="haloY">Rows of context the operator reads.</param>
    public static OpKernel Neighborhood(Func<PixelView, BandContext, PixelView, WorkingBuffer> @operator,
        int haloX, int haloY)
    {
        ArgumentNullException.ThrowIfNull(@operator);
        ArgumentOutOfRangeException.ThrowIfNegative(haloX);
        ArgumentOutOfRangeException.ThrowIfNegative(haloY);

        return (source, destination, destinationRect, sourceRect) =>
        {
            var window = source.Slice(sourceRect);
            var context = new BandContext(source.Width, source.Height, sourceRect.X, sourceRect.Y);

            RowParallel.EnterNested();
            try
            {
                // The whole image goes along with the window: a Wrap border reads the opposite edge, which the window
                // only holds when the band happens to cover the image. Handing over the full view costs nothing and
                // means a banded plan can serve every border mode instead of three.
                using var result = @operator(window, context, source);
                var offsetY = destinationRect.Y - sourceRect.Y;
                CopyRows(result.View, offsetY, destination, destinationRect.Y, destinationRect.Height);
            }
            finally
            {
                RowParallel.ExitNested();
            }
        };
    }

    /// <summary>Adapts an operator that needs the whole image (histogram, quantise, CLAHE).</summary>
    /// <param name="operator">Runs the operator over the full view.</param>
    public static OpKernel Global(Func<PixelView, WorkingBuffer> @operator)
    {
        ArgumentNullException.ThrowIfNull(@operator);

        return (source, destination, _, _) =>
        {
            // No EnterNested here, unlike Point and Neighborhood. Global steps are invoked from the caller's
            // thread, not from inside a band worker, so there is no outer band loop to nest inside — and
            // suppressing would force the operator's own row parallelism (Equalize, CLAHE) onto one thread.
            // The audit caught this: correct output, needless slow-down.
            using var result = @operator(source);
            CopyRows(result.View, 0, destination, 0, source.Height);
        };
    }

    /// <summary>The injected form of the composite step: the overlay arrives as the kernel's second input.</summary>
    public static OpKernel2 CompositeInto(int x, int y, PorterDuff mode) =>
        (source, second, destination, _, _) =>
        {
            CopyRows(source, 0, destination, 0, source.Height);
            AlphaOps.CompositeInto(destination, second, x, y, mode, destination);
        };

    /// <summary>The injected form of the blend step.</summary>
    public static OpKernel2 BlendInto(int x, int y, BlendMode mode) =>
        (source, second, destination, _, _) =>
        {
            CopyRows(source, 0, destination, 0, source.Height);
            Blend.ApplyInto(destination, second, x, y, mode, destination);
        };

    /// <summary>The injected form of the draw-image step.</summary>
    public static OpKernel2 DrawImageInto(PixelRect placement, float opacity) =>
        (source, second, destination, _, _) =>
        {
            CopyRows(source, 0, destination, 0, source.Height);
            using var scaled = Canvas.Resize(second, placement.Width, placement.Height, Resampler.Bilinear,
                ResizeMode.Stretch, Anchor.Centre, default);
            if (opacity >= 1f)
            {
                AlphaOps.CompositeInto(destination, scaled.View, placement.X, placement.Y, PorterDuff.SourceOver,
                    destination);
                return;
            }

            using var faded = PointOps.Opacity(scaled.View, opacity);
            AlphaOps.CompositeInto(destination, faded.View, placement.X, placement.Y, PorterDuff.SourceOver,
                destination);
        };

    /// <summary>The injected form of the guided filter: the guide arrives as the kernel's second input.</summary>
    public static OpKernel2 GuidedBy(int radius, float epsilon, int bandRows) =>
        (source, guide, destination, destinationRect, sourceRect) =>
        {
            var window = source.Slice(sourceRect);
            var offsetY = destinationRect.Y - sourceRect.Y;

            RowParallel.EnterNested();
            try
            {
                using var result = GuidedFilterOps.GuidedFilter(window, guide, radius, epsilon, bandRows);
                CopyRows(result.View, offsetY, destination, destinationRect.Y, destinationRect.Height);
            }
            finally
            {
                RowParallel.ExitNested();
            }
        };

    private static void CopyRows(PixelView from, int fromRow, PixelView to, int toRow, int rows)
    {
        for (var y = 0; y < rows; y++)
            from.Row(fromRow + y).CopyTo(to.Row(toRow + y));
    }
}
