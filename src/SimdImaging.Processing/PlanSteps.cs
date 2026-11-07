using SimdImaging.Abstractions;
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
public static class PlanSteps
{
    /// <summary>Adapts a point operator — one whose output pixel depends only on the same input pixel.</summary>
    /// <param name="operator">Runs the operator over a view and returns a new buffer the adapter disposes.</param>
    public static OpKernel Point(Func<PixelView, PixelBuffer> @operator)
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

    /// <summary>Adapts a neighbourhood operator, keeping only the inner rectangle of its answer.</summary>
    /// <param name="operator">Runs the operator over a view and returns a new buffer the adapter disposes.</param>
    /// <param name="haloX">Columns of context the operator reads.</param>
    /// <param name="haloY">Rows of context the operator reads.</param>
    public static OpKernel Neighborhood(Func<PixelView, PixelBuffer> @operator, int haloX, int haloY)
    {
        ArgumentNullException.ThrowIfNull(@operator);
        ArgumentOutOfRangeException.ThrowIfNegative(haloX);
        ArgumentOutOfRangeException.ThrowIfNegative(haloY);

        return (source, destination, destinationRect, sourceRect) =>
        {
            var window = source.Slice(sourceRect);

            RowParallel.EnterNested();
            try
            {
                using var result = @operator(window);

                // The answer covers `sourceRect`; the part we want starts where the destination band starts.
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
    public static OpKernel Global(Func<PixelView, PixelBuffer> @operator)
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

    private static void CopyRows(PixelView from, int fromRow, PixelView to, int toRow, int rows)
    {
        for (var y = 0; y < rows; y++)
            from.Row(fromRow + y).CopyTo(to.Row(toRow + y));
    }
}
