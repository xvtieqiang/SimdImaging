using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// Content-aware resizing by seam carving: instead of resampling, it removes (or repeats) connected paths of
/// pixels that carry the least gradient energy, one at a time, and the image loses exactly those columns or
/// rows.
/// <para><b>This is not <see cref="EntropyCrop"/>.</b> EntropyCrop trims uniform <i>borders</i> and always keeps a
/// rectangle; a seam is a per-row path through the middle of the image, so the result is not a sub-rectangle of
/// the input. Use EntropyCrop for scan margins, seam carving to change the aspect ratio while keeping the
/// subject.</para>
/// <para><b>Determinism.</b> For 8- and 16-bit input the luma is an exact integer
/// (<c>(54·R + 183·G + 19·B) &gt;&gt; 8</c>, BT.709 with weights summing to 256), so the energy map, the dynamic
/// programme and every comparison are exact integers — the output is bit-identical on any machine (D0). Float
/// input uses float luma and a double programme in a fixed order (D1). Pixels are only ever <i>moved</i>, never
/// resampled, so the kept pixels keep their exact bytes.</para>
/// <para>Cost is one full energy + programme pass per seam: <c>O(W·H)</c> each, so removing 100 columns from
/// 1080p is a hundred passes. The energy is recomputed from pixels every time rather than updated
/// incrementally; see the note on <see cref="RemoveSeams"/> for why.</para>
/// </summary>
public static class SeamCarving
{
    /// <summary>Test hook: forces the scalar DP row so a test can compare the two paths on real data.</summary>
    internal static bool ForceScalarVector;

    /// <summary>
    /// Phase timing, off by default: six counters written once per phase per call, and four per seam.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape as <c>PlanarConvolve.ForceScalar</c>: internal, no <c>#if</c>, read in the
    /// shipped path as a branch that is never taken. What it costs when off is the branch — it never touches a
    /// value that takes part in the arithmetic, so it cannot change a byte.
    /// </para>
    /// <para>
    /// The brackets deliberately sit outside the per-pixel loops: a timing check inside <c>WriteRow4</c> would
    /// stop the four lanes from being one block. <see cref="Interlocked"/> rather than <c>+=</c> because the
    /// concurrency tests run the operator on several threads at once.
    /// </para>
    /// </remarks>
    internal static bool ProfilePhases;

    internal static long TicksCopy;
    internal static long TicksPrime;
    internal static long TicksFind;
    internal static long TicksCut;
    internal static long TicksShift;
    internal static long TicksCrop;
    internal static long TicksTranspose;

    /// <summary>Zeroes every phase counter; call before the run being measured.</summary>
    internal static void ResetProfile()
    {
        TicksCopy = 0;
        TicksPrime = 0;
        TicksFind = 0;
        TicksCut = 0;
        TicksShift = 0;
        TicksCrop = 0;
        TicksTranspose = 0;
    }

    private static void Close(long startTicks, ref long counter) =>
        Interlocked.Add(ref counter, Stopwatch.GetTimestamp() - startTicks);

    /// <summary>
    /// Removes <paramref name="count"/> connected seams, shrinking the image by one column or row each.
    /// <para><b>Why the energy is recomputed rather than updated.</b> Removing a seam moves every pixel to its
    /// right (or below) one position, so the columns whose gradient changes are not a fixed-width band around the
    /// seam: the set is bounded only by how far the path wanders, which is unbounded in general. The programme has
    /// the same problem in a worse form — a change at one cell spreads one column per row, so for a full-height
    /// seam the affected region is a triangle that reaches the far edge by the bottom row. An "incremental"
    /// update therefore still touches about half the image, and the measured saving did not justify a second
    /// code path that could disagree with the full one.</para>
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="axis">Which way the seams run.</param>
    /// <param name="count">How many seams to remove; must leave at least one column or row.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative, or would remove the
    /// whole image.</exception>
    public static WorkingBuffer RemoveSeams(PixelView source, SeamAxis axis, int count, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return Transforms.Copy(source, pool);

        return axis == SeamAxis.Vertical
            ? RemoveVertical(source, count, pool)
            : RemoveHorizontal(source, count, pool);
    }

    /// <summary>
    /// Repeats <paramref name="count"/> connected seams, growing the image by one column or row each. Each
    /// insertion is chosen on the already-enlarged image with the previously inserted paths blocked, so the
    /// duplicates spread out instead of piling onto the same path.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="axis">Which way the seams run.</param>
    /// <param name="count">How many seams to insert.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static WorkingBuffer InsertSeams(PixelView source, SeamAxis axis, int count, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return Transforms.Copy(source, pool);

        return axis == SeamAxis.Vertical
            ? InsertVertical(source, count, pool)
            : InsertHorizontal(source, count, pool);
    }

    /// <summary>Resizes to an exact size by removing or inserting seams on each axis: vertical first, then
    /// horizontal.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="width">Target width, at least 1.</param>
    /// <param name="height">Target height, at least 1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">A target dimension is not positive.</exception>
    public static WorkingBuffer CarveTo(PixelView source, int width, int height, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var current = source;
        WorkingBuffer? owned = null;
        try
        {
            var deltaWidth = width - current.Width;
            if (deltaWidth != 0)
            {
                owned = deltaWidth < 0 ? RemoveSeams(current, SeamAxis.Vertical, -deltaWidth, pool)
                    : InsertSeams(current, SeamAxis.Vertical, deltaWidth, pool);
                current = owned.View;
            }

            var deltaHeight = height - current.Height;
            if (deltaHeight != 0)
            {
                var next = deltaHeight < 0 ? RemoveSeams(current, SeamAxis.Horizontal, -deltaHeight, pool)
                    : InsertSeams(current, SeamAxis.Horizontal, deltaHeight, pool);
                owned?.Dispose();
                owned = next;
                current = owned.View;
            }

            return owned ?? Transforms.Copy(source, pool);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    private static WorkingBuffer RemoveVertical(PixelView source, int count, IPixelPool? pool)
    {
        var width = source.Width;
        var height = source.Height;
        if (count >= width)
            throw new ArgumentOutOfRangeException(nameof(count), count,
                $"Cannot remove {count} of {width} columns; at least one must remain.");

        var start = Stopwatch.GetTimestamp();
        var work = Transforms.Copy(source, pool);
        if (ProfilePhases)
            Close(start, ref TicksCopy);

        try
        {
            start = Stopwatch.GetTimestamp();
            var workspace = new Workspace(width, height);
            workspace.PrimeLuma(work.View);
            if (ProfilePhases)
                Close(start, ref TicksPrime);

            var logical = width;
            for (var i = 0; i < count; i++)
            {
                start = Stopwatch.GetTimestamp();
                var seam = workspace.FindSeam(work.View, logical);
                if (ProfilePhases)
                    Close(start, ref TicksFind);

                start = Stopwatch.GetTimestamp();
                Workspace.CutSeam(work.View, seam);
                if (ProfilePhases)
                    Close(start, ref TicksCut);

                start = Stopwatch.GetTimestamp();
                workspace.ShiftLuma(seam, logical);
                if (ProfilePhases)
                    Close(start, ref TicksShift);

                logical--;
            }

            start = Stopwatch.GetTimestamp();
            var cropped = Transforms.Crop(work.View, new PixelRect(0, 0, logical, height), pool);
            if (ProfilePhases)
                Close(start, ref TicksCrop);

            return cropped;
        }
        finally
        {
            work.Dispose();
        }
    }

    private static WorkingBuffer InsertVertical(PixelView source, int count, IPixelPool? pool)
    {
        var width = source.Width;
        var height = source.Height;
        var bytesPerPixel = source.Format.BytesPerPixel();
        var work = Transforms.Copy(source, pool);
        try
        {
            var workspace = new Workspace(width + count, height);
            var logical = width;
            for (var i = 0; i < count; i++)
            {
                var seam = workspace.FindSeam(work.View, logical, blocked: true);
                var next = WorkingBuffer.Rent(logical + 1, height, source.Format, pool);
                try
                {
                    for (var y = 0; y < height; y++)
                    {
                        var from = work.View.Row(y);
                        var to = next.View.Row(y);
                        var at = seam[y] * bytesPerPixel;

                        from[..at].CopyTo(to);
                        from.Slice(at, bytesPerPixel).CopyTo(to[at..]);
                        from[at..].CopyTo(to[(at + bytesPerPixel)..]);
                    }
                }
                catch
                {
                    next.Dispose();
                    throw;
                }

                workspace.BlockSeam(seam, logical);
                work.Dispose();
                work = next;
                logical++;
            }

            return work;
        }
        catch
        {
            work.Dispose();
            throw;
        }
    }

    private static WorkingBuffer RemoveHorizontal(PixelView source, int count, IPixelPool? pool)
    {
        var start = Stopwatch.GetTimestamp();
        var transposed = Orientation.Transpose(source, false, pool);
        if (ProfilePhases)
            Close(start, ref TicksTranspose);
        try
        {
            using var carved = RemoveVertical(transposed.View, count, pool);
            start = Stopwatch.GetTimestamp();
            var result = Orientation.Transpose(carved.View, false, pool);
            if (ProfilePhases)
                Close(start, ref TicksTranspose);
            return result;
        }
        finally
        {
            transposed.Dispose();
        }
    }

    private static WorkingBuffer InsertHorizontal(PixelView source, int count, IPixelPool? pool)
    {
        var start = Stopwatch.GetTimestamp();
        var transposed = Orientation.Transpose(source, false, pool);
        if (ProfilePhases)
            Close(start, ref TicksTranspose);
        try
        {
            using var carved = InsertVertical(transposed.View, count, pool);
            return Orientation.Transpose(carved.View, false, pool);
        }
        finally
        {
            transposed.Dispose();
        }
    }

    /// <summary>
    /// Per-call scratch: because the width changes with every seam, every buffer is sized for the widest image
    /// the call can reach, and the programme's back-pointers are one byte per pixel.
    /// </summary>
    private sealed class Workspace
    {
        private readonly int _capacity;
        private readonly int _height;
        private readonly byte[] _back;
        private readonly bool[] _blocked;
        private readonly double[] _rowA;
        private readonly double[] _rowB;
        private readonly float[] _above;
        private readonly float[] _centre;
        private readonly float[] _below;
        private readonly float[] _straight;
        private readonly int[] _seam;

        /// <summary>
        /// Luma of the whole image, kept across seams when <see cref="PrimeLuma"/> was called.
        /// <para>
        /// Removing a column shifts every row right of the seam one place left, so the luma array shifts with
        /// it and stays exactly equal to what recomputing from the pixels would give — the conversion is a
        /// per-pixel function of that pixel's own bytes. Without this, a 50-seam carve converted the same
        /// 2.07 MPix fifty times.
        /// </para>
        /// </summary>
        private float[]? _luma;

        /// <summary>Four lanes of double, and the caller did not ask for scalar.</summary>
        private static bool Vectorized => Vector256.IsHardwareAccelerated && !ForceScalarVector;

        public Workspace(int width, int height)
        {
            _capacity = width;
            _height = height;
            _back = new byte[(long)width * height];
            _blocked = new bool[(long)width * height];
            _rowA = new double[width];
            _rowB = new double[width];
            _above = new float[width];
            _centre = new float[width];
            _below = new float[width];
            _straight = new float[width * 4];
            _seam = new int[height];
        }

        /// <summary>
        /// Finds the minimum-energy connected path. Ties resolve to the top neighbour first, then left, then
        /// right (the comparison is strict), and the starting column is the leftmost minimum of the last row —
        /// so a flat image always loses column 0, which is what makes the tie-break testable.
        /// </summary>
        public int[] FindSeam(PixelView view, int width, bool blocked = false)
        {
            var previous = _rowA;
            var current = _rowB;
            var above = _above;
            var centre = _centre;
            var below = _below;

            ReadLuma(view, 0, width, above);
            ReadLuma(view, 0, width, centre);
            ReadLuma(view, Math.Min(1, _height - 1), width, below);

            for (var x = 0; x < width; x++)
            {
                previous[x] = Energy(width, x, above, centre, below, blocked, 0);
                _back[x] = 1;
            }

            for (var y = 1; y < _height; y++)
            {
                var spare = above;
                above = centre;
                centre = below;
                below = spare;
                ReadLuma(view, Math.Min(y + 1, _height - 1), width, below);

                var row = y * _capacity;
                var x = 0;

                if (Vectorized && !blocked)
                {
                    // Column 0 reads centre[-1] and previous[-1] through clamps, so it stays scalar.
                    WriteCell(width, 0, row, previous, current, above, centre, below, blocked, y);
                    x = 1;

                    for (; x + 4 < width; x += 4)
                        WriteRow4(x, row, previous, current, above, centre, below);

                    if (x > width)
                        x = width;
                }

                for (; x < width; x++)
                    WriteCell(width, x, row, previous, current, above, centre, below, blocked, y);

                (previous, current) = (current, previous);
            }

            var column = 0;
            var minimum = previous[0];
            for (var x = 1; x < width; x++)
            {
                if (previous[x] < minimum)
                {
                    minimum = previous[x];
                    column = x;
                }
            }

            for (var y = _height - 1; y >= 0; y--)
            {
                _seam[y] = column;
                column = Math.Clamp(column + _back[y * _capacity + column] - 1, 0, width - 1);
            }

            return _seam;
        }

        /// <summary>Marks the newly duplicated path as used, shifting the mask that later insertions read.</summary>
        public void BlockSeam(int[] seam, int width)
        {
            for (var y = 0; y < _height; y++)
            {
                var row = y * _capacity;
                var x = seam[y];
                for (var c = width; c > x + 1; c--)
                    _blocked[row + c] = _blocked[row + c - 1];
                _blocked[row + x] = true;
                _blocked[row + x + 1] = true;
            }
        }

        /// <summary>Shifts one row left over the seam; the copy runs forward, which is safe for a left shift.</summary>
        public static void CutSeam(PixelView view, int[] seam)
        {
            var bytesPerPixel = view.Format.BytesPerPixel();
            for (var y = 0; y < view.Height; y++)
            {
                var row = view.Row(y);
                var at = seam[y] * bytesPerPixel;
                row.Slice(at + bytesPerPixel).CopyTo(row[at..]);
            }
        }

        /// <summary>Four DP cells at once: the three-way minimum and the gradient energy.</summary>
        /// <remarks>
        /// <para>
        /// The vector axis is <i>pixels</i> and the arithmetic mirrors <see cref="WriteCell"/> operand for
        /// operand, which is what keeps the bytes identical rather than merely close. In particular the
        /// horizontal term is a <c>float</c> subtraction converted to double afterwards, because that is the
        /// order the scalar twin evaluates <c>(double)Math.Abs(right - left)</c> in; converting first would
        /// round differently. The vertical term subtracts in double, as the scalar does.
        /// </para>
        /// <para>
        /// Ties follow the scalar rule by construction: the comparisons are strict, a tie on the left keeps
        /// the centre, and a tie on the right keeps whatever the left comparison chose.
        /// </para>
        /// </remarks>
        private void WriteRow4(int x, int row, double[] previous, double[] current,
            float[] above, float[] centre, float[] below)
        {
            var prevLeft = Vector256.LoadUnsafe(ref previous[x - 1]);
            var prevMid = Vector256.LoadUnsafe(ref previous[x]);
            var prevRight = Vector256.LoadUnsafe(ref previous[x + 1]);

            var takeLeft = Vector256.LessThan(prevLeft, prevMid);
            var bestLeft = Vector256.ConditionalSelect(takeLeft, prevLeft, prevMid);
            var direction = Vector256.ConditionalSelect(takeLeft, Vector256<double>.Zero, Vector256.Create(1.0));

            var takeRight = Vector256.LessThan(prevRight, bestLeft);
            var best = Vector256.ConditionalSelect(takeRight, prevRight, bestLeft);
            direction = Vector256.ConditionalSelect(takeRight, Vector256.Create(2.0), direction);

            var left = Vector128.LoadUnsafe(ref centre[x - 1]);
            var right = Vector128.LoadUnsafe(ref centre[x + 1]);
            var (horizontalLow, horizontalHigh) = Vector128.Widen(Vector128.Abs(right - left));
            var horizontal = Vector256.Create(horizontalLow, horizontalHigh);

            var (downLow, downHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref below[x]));
            var (upLow, upHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref above[x]));
            var vertical = Vector256.Abs(
                Vector256.Create(downLow, downHigh) - Vector256.Create(upLow, upHigh));

            (horizontal + vertical + best).StoreUnsafe(ref current[x]);

            Span<double> directions = stackalloc double[4];
            direction.StoreUnsafe(ref MemoryMarshal.GetReference(directions));
            for (var i = 0; i < 4; i++)
                _back[row + x + i] = (byte)directions[i];
        }

        /// <summary>One DP cell, the scalar twin both paths have to agree with.</summary>
        private void WriteCell(int width, int x, int row, double[] previous, double[] current,
            float[] above, float[] centre, float[] below, bool blocked, int y)
        {
            var best = previous[x];
            var direction = (byte)1;
            if (x > 0 && previous[x - 1] < best)
            {
                best = previous[x - 1];
                direction = 0;
            }

            if (x + 1 < width && previous[x + 1] < best)
            {
                best = previous[x + 1];
                direction = 2;
            }

            current[x] = Energy(width, x, above, centre, below, blocked, y) + best;
            _back[row + x] = direction;
        }

        private double Energy(int width, int x, float[] above, float[] centre, float[] below, bool blocked, int y)
        {
            var left = centre[x > 0 ? x - 1 : 0];
            var right = centre[x + 1 < width ? x + 1 : width - 1];
            var residual = (double)Math.Abs(right - left) + Math.Abs((double)below[x] - above[x]);
            return blocked && _blocked[y * _capacity + x] ? double.PositiveInfinity : residual;
        }

        /// <summary>Luma of one row: exact integers for 8/16-bit, BT.709 floats for Rgba32F.</summary>
        /// <summary>Converts every row once so <see cref="FindSeam"/> can read luma instead of recomputing it.</summary>
        public void PrimeLuma(PixelView view)
        {
            _luma = new float[(long)_capacity * _height];
            for (var y = 0; y < _height; y++)
                Luma(view, y, view.Width, _luma.AsSpan(y * _capacity, view.Width));
        }

        /// <summary>Mirrors <see cref="CutSeam"/> on the cached luma: one column disappears from column <paramref name="seam"/>.</summary>
        public void ShiftLuma(int[] seam, int width)
        {
            if (_luma is null)
                return;

            for (var y = 0; y < _height; y++)
            {
                var at = seam[y];
                var row = y * _capacity;
                Array.Copy(_luma, row + at + 1, _luma, row + at, width - at - 1);
            }
        }

        /// <summary>Cached row when primed, a fresh conversion otherwise.</summary>
        private void ReadLuma(PixelView view, int y, int width, float[] destination)
        {
            if (_luma is null)
            {
                Luma(view, y, width, destination);
                return;
            }

            Array.Copy(_luma, (long)y * _capacity, destination, 0, width);
        }

        private void Luma(PixelView view, int y, int width, Span<float> destination)
        {
            var row = view.Row(y);

            if (view.Format == WorkingFormat.Rgba16)
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                for (var x = 0; x < width; x++)
                {
                    var o = x * 4;
                    destination[x] = (54 * samples[o] + 183 * samples[o + 1] + 19 * samples[o + 2]) >> 8;
                }

                return;
            }

            if (view.Format == WorkingFormat.Rgba8)
            {
                for (var x = 0; x < width; x++)
                {
                    var o = x * 4;
                    destination[x] = (54 * row[o] + 183 * row[o + 1] + 19 * row[o + 2]) >> 8;
                }

                return;
            }

            Pixels.ReadRowRaw(row, width, view.Format, _straight);
            for (var x = 0; x < width; x++)
            {
                var o = x * 4;
                destination[x] = 0.2126f * _straight[o] + 0.7152f * _straight[o + 1] + 0.0722f * _straight[o + 2];
            }
        }
    }
}
