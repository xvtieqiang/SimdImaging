using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Spatial filters. The linear ones (box, Gaussian) blur in premultiplied float with a normalized symmetric
/// kernel, so transparent neighbours cannot bleed colour. The order-statistic and gradient filters work on
/// straight alpha: a median is not a linear combination, and premultiplying first would let alpha distort the
/// ordering.
/// </summary>
public static class FilterOps
{
    /// <summary>Diagnostic hook: override the separable band height (0 = default). Used only by the
    /// comparison harness to attribute cost; changing it must never change the output bytes.</summary>
    internal static int BandRowsOverride;

    /// <summary>Output rows per vertical tile: one read of a tap row feeds this many accumulators.</summary>
    private const int VerticalTileRows = 4;

    /// <summary>Floats per column strip in the vertical pass.</summary>
    private const int VerticalBlockFloats = 128;

    /// <summary>
    /// 4×4 lane transpose: four pixels of RGBA become four channels of four pixels. Built from the SSE
    /// unpck/mov pairs rather than a two-source shuffle (this runtime's <c>Vector128.Shuffle</c> has no
    /// cross-vector overload). A matrix transpose is its own inverse, so the same call turns four channel
    /// vectors back into four pixels. shufps carries the second halving: 0x44 selects lanes 0,1 of each
    /// operand, 0xEE selects lanes 2,3.
    /// </summary>
    private static (Vector128<float> A, Vector128<float> B, Vector128<float> C, Vector128<float> D) Transpose4x4(
        Vector128<float> p0, Vector128<float> p1, Vector128<float> p2, Vector128<float> p3)
    {
        var low0 = Sse.UnpackLow(p0, p1);       // r0 r1 g0 g1
        var high0 = Sse.UnpackHigh(p0, p1);     // b0 b1 a0 a1
        var low1 = Sse.UnpackLow(p2, p3);       // r2 r3 g2 g3
        var high1 = Sse.UnpackHigh(p2, p3);     // b2 b3 a2 a3

        // shufps with 0x44 takes lanes 0,1 of each operand; 0xEE takes lanes 2,3.
        return (Sse.Shuffle(low0, low1, 0x44), Sse.Shuffle(low0, low1, 0xEE),
            Sse.Shuffle(high0, high1, 0x44), Sse.Shuffle(high0, high1, 0xEE));
    }

    /// <summary>One output sample of a planar 1-D convolution, taps ascending — the reference the vector
    /// interior must reproduce bit for bit.</summary>
    private static float HorizontalSample(float[] planes, int from, int x, int width, float[] kernel, int radius)
    {
        var sum = 0f;
        for (var k = -radius; k <= radius; k++)
            sum += planes[from + Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
        return sum;
    }

    /// <summary>A box blur of the given radius; the kernel is <c>1/(2r+1)</c> over 2r+1 taps.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative.</exception>
    public static PixelBuffer BoxBlur(PixelView source, int radius, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        if (radius == 0)
            return Geometry.Transforms.Copy(source, pool);

        var kernel = new float[radius * 2 + 1];
        Array.Fill(kernel, 1f / kernel.Length);
        return Separable(source, kernel, pool);
    }

    /// <summary>
    /// A Gaussian blur: <c>exp(-x² / 2σ²)</c> over <c>±ceil(3σ)</c> taps, normalized. Unlike a three-box
    /// approximation this is the real kernel at any sigma.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is not finite and positive.</exception>
    public static PixelBuffer GaussianBlur(PixelView source, float sigma, IPixelPool? pool = null)
    {
        if (!(sigma > 0f) || float.IsInfinity(sigma))
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be finite and greater than 0.");

        var radius = Math.Max(1, (int)MathF.Ceiling(3f * sigma));
        var kernel = new float[radius * 2 + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var weight = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            kernel[i + radius] = weight;
            sum += weight;
        }

        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;

        return Separable(source, kernel, pool);
    }

    /// <summary>
    /// Runs a separable kernel as a horizontal pass followed by a vertical one. Work is done in row bands: a
    /// band keeps only <c>bandRows + 2·radius</c> horizontally filtered rows, so 4K needs about 9 MB of scratch
    /// instead of a full float plane (162 MB at σ 3). The recomputed rows are the band's context, and the
    /// vertical pass reads a row only after clamping it against the image — so the output is identical to the
    /// whole-image pass, edge replication included.
    /// </summary>
    private static PixelBuffer Separable(PixelView source, float[] kernel, IPixelPool? pool)
    {
        var radius = kernel.Length / 2;
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;

        var bandRows = BandRowsOverride > 0 ? BandRowsOverride : Math.Max(128, radius * 8);
        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;
        var bandCount = (height + bandRows - 1) / bandRows;

        void RunBand(int bandTop, int bandBottom, float[] scratch, float[] row, float[] outputRows)
        {
            var top = Math.Max(0, bandTop - radius);
            var bottom = Math.Min(height, bandBottom + radius);
            var rows = bottom - top;

            // Eight planes: four de-interleaved inputs, four convolved outputs. Per worker, because bands run
            // concurrently and a shared plane is a data race.
            var planes = FloatScratch.Rent(width * 8);
            try
            {

            for (var i = 0; i < rows; i++)
            {
                Pixels.ReadRowPremultiplied(source.Row(top + i), width, source.Format, row);
                var output = scratch.AsSpan(i * stride, stride);

                if (Vector256.IsHardwareAccelerated)
                {
                    // Planar: the vector axis is pixels, not channels, so one 256-bit op covers 8 pixels of a
                    // single channel instead of 1 pixel of all four. Measured 1.32x over the interleaved shape
                    // with bit-identical output (BlurProfile section 2). The de-interleave and re-interleave
                    // are counted in that number, not hidden behind it.
                    // The de-interleave and re-interleave are half the cost of this pass (BlurProfile section 2:
                    // 1.015 ms of 2.003 ms), so they go through a 4×4 lane transpose instead of four scalar
                    // moves per pixel. A transpose is a pure permutation, so the bytes cannot change.
                    var x4 = 0;
                    if (Sse.IsSupported)
                    {
                        for (; x4 + 4 <= width; x4 += 4)
                        {
                            var (r, g, b, a) = Transpose4x4(
                                Vector128.LoadUnsafe(ref row[x4 * 4]),
                                Vector128.LoadUnsafe(ref row[x4 * 4 + 4]),
                                Vector128.LoadUnsafe(ref row[x4 * 4 + 8]),
                                Vector128.LoadUnsafe(ref row[x4 * 4 + 12]));
                            r.StoreUnsafe(ref planes[x4]);
                            g.StoreUnsafe(ref planes[width + x4]);
                            b.StoreUnsafe(ref planes[2 * width + x4]);
                            a.StoreUnsafe(ref planes[3 * width + x4]);
                        }
                    }

                    for (var x = x4; x < width; x++)
                    for (var c = 0; c < 4; c++)
                        planes[c * width + x] = row[x * 4 + c];

                    for (var c = 0; c < 4; c++)
                    {
                        var from = c * width;
                        var to = (4 + c) * width;

                        // A group of 8 pixels loads plane[x + k .. x + k + 7] as if all lanes shared one
                        // offset. The moment a lane in the group needs its index clamped that shortcut reads
                        // the wrong samples, so the first and last `radius` pixels stay scalar. Missing this
                        // is what made an earlier cut differ on 1.1% of samples.
                        // The prefix is bounded by BOTH the radius and the width. Bounding it by the radius
                        // alone runs past the plane whenever radius >= width (an 11-px wide image with
                        // sigma 5 gives radius 15), and because FloatScratch may hand back a buffer larger
                        // than requested, the overrun either threw or silently wrote into the next plane
                        // depending on pool state. That was the intermittent failure.
                        var x = 0;
                        var prefix = Math.Min(radius, width);
                        for (; x < prefix; x++)
                            planes[to + x] = HorizontalSample(planes, from, x, width, kernel, radius);

                        for (; x + 7 + radius <= width - 1; x += 8)
                        {
                            var sum = Vector256<float>.Zero;
                            for (var k = -radius; k <= radius; k++)
                                sum += Vector256.LoadUnsafe(ref planes[from + x + k]) * Vector256.Create(kernel[k + radius]);
                            sum.StoreUnsafe(ref planes[to + x]);
                        }

                        for (; x < width; x++)
                            planes[to + x] = HorizontalSample(planes, from, x, width, kernel, radius);
                    }

                    x4 = 0;
                    if (Sse.IsSupported)
                    {
                        for (; x4 + 4 <= width; x4 += 4)
                        {
                            var (p0, p1, p2, p3) = Transpose4x4(
                                Vector128.LoadUnsafe(ref planes[4 * width + x4]),
                                Vector128.LoadUnsafe(ref planes[5 * width + x4]),
                                Vector128.LoadUnsafe(ref planes[6 * width + x4]),
                                Vector128.LoadUnsafe(ref planes[7 * width + x4]));
                            p0.StoreUnsafe(ref output[x4 * 4]);
                            p1.StoreUnsafe(ref output[x4 * 4 + 4]);
                            p2.StoreUnsafe(ref output[x4 * 4 + 8]);
                            p3.StoreUnsafe(ref output[x4 * 4 + 12]);
                        }
                    }

                    for (var x = x4; x < width; x++)
                    for (var c = 0; c < 4; c++)
                        output[x * 4 + c] = planes[(4 + c) * width + x];
                }
                else
                {
                    for (var x = 0; x < width; x++)
                    {
                        var accumulator = Vector128<float>.Zero;
                        for (var k = -radius; k <= radius; k++)
                        {
                            var sx = Math.Clamp(x + k, 0, width - 1);
                            accumulator += Vector128.LoadUnsafe(ref row[sx * 4]) * Vector128.Create(kernel[k + radius]);
                        }

                        accumulator.StoreUnsafe(ref output[x * 4]);
                    }
                }
            }

            }
            finally
            {
                FloatScratch.Return(planes);
            }

            // Vertical pass, two-dimensional tile. `VerticalTileRows` output rows share one read of every tap
            // row, and the accumulation for each row lives in a `VerticalBlockFloats`-wide strip instead of
            // being written and re-read once per tap. Each output sample still sums its taps in ascending k
            // order, so the output is bit-identical (BlurProfile section 1 measured the family; the 4x128
            // point beat the 1x64 point by 15% in an interleaved final).
            Span<int> tileOffsets = stackalloc int[VerticalTileRows * kernel.Length];
            Span<float> strip = stackalloc float[VerticalTileRows * VerticalBlockFloats];

            for (var y0 = bandTop; y0 < bandBottom; y0 += VerticalTileRows)
            {
                var rowCount = Math.Min(VerticalTileRows, bandBottom - y0);
                for (var t = 0; t < rowCount; t++)
                for (var k = 0; k < kernel.Length; k++)
                    tileOffsets[t * kernel.Length + k] =
                        (Math.Clamp(y0 + t + k - radius, 0, height - 1) - top) * stride;

                if (Vector256.IsHardwareAccelerated)
                {
                    for (var block = 0; block < stride; block += VerticalBlockFloats)
                    {
                        var length = Math.Min(VerticalBlockFloats, stride - block);
                        var vectorLength = length - length % 8;

                        for (var t = 0; t < rowCount; t++)
                        for (var j = 0; j < vectorLength; j += 8)
                            Vector256<float>.Zero.StoreUnsafe(ref strip[t * VerticalBlockFloats + j]);

                        for (var k = 0; k < kernel.Length; k++)
                        {
                            var weight = Vector256.Create(kernel[k]);
                            for (var t = 0; t < rowCount; t++)
                            {
                                var sourceBase = tileOffsets[t * kernel.Length + k] + block;
                                for (var j = 0; j < vectorLength; j += 8)
                                {
                                    var partial = Vector256.LoadUnsafe(ref strip[t * VerticalBlockFloats + j]);
                                    var sample = Vector256.LoadUnsafe(ref scratch[sourceBase + j]);
                                    (partial + sample * weight).StoreUnsafe(ref strip[t * VerticalBlockFloats + j]);
                                }
                            }
                        }

                        for (var t = 0; t < rowCount; t++)
                        for (var j = 0; j < vectorLength; j += 8)
                            Vector256.LoadUnsafe(ref strip[t * VerticalBlockFloats + j])
                                .StoreUnsafe(ref outputRows[t * stride + block + j]);

                        // Scalar twin for the tail of the last strip; same ascending-k order.
                        for (var j = vectorLength; j < length; j++)
                        for (var t = 0; t < rowCount; t++)
                        {
                            var sum = 0f;
                            for (var k = 0; k < kernel.Length; k++)
                                sum += scratch[tileOffsets[t * kernel.Length + k] + block + j] * kernel[k];
                            outputRows[t * stride + block + j] = sum;
                        }
                    }
                }
                else
                {
                    for (var t = 0; t < rowCount; t++)
                    {
                        var target = outputRows.AsSpan(t * stride, stride);
                        for (var i = 0; i < stride; i++)
                        {
                            var sum = 0f;
                            for (var k = 0; k < kernel.Length; k++)
                                sum += scratch[tileOffsets[t * kernel.Length + k] + i] * kernel[k];
                            target[i] = sum;
                        }
                    }
                }

                for (var t = 0; t < rowCount; t++)
                    Pixels.WriteRowUnpremultiplied(outputRows.AsSpan(t * stride, stride), width,
                        source.Format, view.Row(y0 + t));
            }
        }

        // One scratch set per worker, reused across the bands that worker owns. Bands write disjoint
        // output rows and only read shared source rows, so the worker count cannot change the bytes.
        RowParallel.ForBands(bandCount, (start, end) =>
        {
            var capacity = Math.Min(height, bandRows + radius * 2);
            var scratch = FloatScratch.Rent(capacity * stride);
            var row = FloatScratch.Rent(stride);
            var outputRows = FloatScratch.Rent(stride * VerticalTileRows);
            try
            {
                for (var b = start; b < end; b++)
                {
                    var bandTop = b * bandRows;
                    RunBand(bandTop, Math.Min(bandTop + bandRows, height), scratch, row, outputRows);
                }
            }
            finally
            {
                FloatScratch.Return(scratch);
                FloatScratch.Return(row);
                FloatScratch.Return(outputRows);
            }
        });

        return destination;
    }


    /// <summary>
    /// A 3×3 median per channel (alpha included). Edges replicate. The median is an order statistic, so this
    /// works on straight alpha: premultiplying would change the ordering the median is defined on.
    /// </summary>
    public static PixelBuffer Median3x3(PixelView source, IPixelPool? pool = null)
    {
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;

        // Rgba8 gets a byte-domain kernel: four pixels (16 samples) per compare-exchange instead of one
        // channel. Legal only because the float round trip is the identity on bytes — proved exhaustively by
        // Median3x3Tests.ByteToFloatRoundTrip_IsIdentity_ForEveryByte, not assumed.
        var byteDomain = source.Format == WorkingFormat.Rgba8 && Vector128.IsHardwareAccelerated;

        RowParallel.For(width, height, (start, end) =>
        {
            if (byteDomain)
            {
                for (var y = start; y < end; y++)
                    MedianRowBytes(source, view, width, height, y);

                return;
            }

            var block = new float[3 * stride];
            var outputRow = new float[stride];

            for (var y = start; y < end; y++)
            {
                for (var dy = -1; dy <= 1; dy++)
                    Pixels.ReadRowRaw(source.Row(Math.Clamp(y + dy, 0, height - 1)), width, source.Format,
                        block.AsSpan((dy + 1) * stride, stride));

                for (var x = 0; x < width; x++)
                {
                    var left = Math.Clamp(x - 1, 0, width - 1) * 4;
                    var centre = x * 4;
                    var right = Math.Clamp(x + 1, 0, width - 1) * 4;

                    for (var c = 0; c < 4; c++)
                    {
                        var a0 = block[left + c];
                        var a1 = block[centre + c];
                        var a2 = block[right + c];
                        var a3 = block[stride + left + c];
                        var a4 = block[stride + centre + c];
                        var a5 = block[stride + right + c];
                        var a6 = block[2 * stride + left + c];
                        var a7 = block[2 * stride + centre + c];
                        var a8 = block[2 * stride + right + c];

                        Median9(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7, ref a8);
                        outputRow[centre + c] = a4;
                    }
                }

                Pixels.WriteRowRaw(outputRow, width, source.Format, view.Row(y));
            }
        });

        return destination;
    }

    /// <summary>
    /// One destination row of the byte-domain 3×3 median. Blocks of four pixels read three 16-byte windows per
    /// source row; the first pixel and the last few fall back to a plain nine-element sort, because a window
    /// load at <c>x-1</c> would run off the row and clamping has to be per pixel there.
    /// </summary>
    private static void MedianRowBytes(PixelView source, PixelView destination, int width, int height, int y)
    {
        var up = source.Row(Math.Clamp(y - 1, 0, height - 1));
        var mid = source.Row(y);
        var down = source.Row(Math.Clamp(y + 1, 0, height - 1));
        var to = destination.Row(y);

        MedianEdgePixelBytes(up, mid, down, to, 0, width);

        var x = 1;
        // A block covers pixels x..x+3 and reads bytes 4x-4 .. 4x+19, so x+5 must stay inside the row.
        for (; x <= width - 5; x += 4)
        {
            var centre = x * 4;
            var median = Median9Bytes(
                Vector128.LoadUnsafe(ref Unsafe.AsRef(in up[centre - 4])),
                Vector128.LoadUnsafe(ref Unsafe.AsRef(in up[centre])),
                Vector128.LoadUnsafe(ref Unsafe.AsRef(in up[centre + 4])),
                Vector128.LoadUnsafe(ref Unsafe.AsRef(in mid[centre - 4])),
                Vector128.LoadUnsafe(ref Unsafe.AsRef(in mid[centre])),
                Vector128.LoadUnsafe(ref Unsafe.AsRef(in mid[centre + 4])),
                Vector128.LoadUnsafe(ref Unsafe.AsRef(in down[centre - 4])),
                Vector128.LoadUnsafe(ref Unsafe.AsRef(in down[centre])),
                Vector128.LoadUnsafe(ref Unsafe.AsRef(in down[centre + 4])));

            median.StoreUnsafe(ref to[centre]);
        }

        for (; x < width; x++)
            MedianEdgePixelBytes(up, mid, down, to, x, width);
    }

    /// <summary>One pixel, nine-element sort. Used for the edges and the row tail.</summary>
    private static void MedianEdgePixelBytes(ReadOnlySpan<byte> up, ReadOnlySpan<byte> mid, ReadOnlySpan<byte> down,
        Span<byte> to, int x, int width)
    {
        var left = Math.Clamp(x - 1, 0, width - 1) * 4;
        var centre = x * 4;
        var right = Math.Clamp(x + 1, 0, width - 1) * 4;

        Span<byte> window = stackalloc byte[9];   // hoisted: CA2014, a stackalloc inside a loop grows nothing
        for (var c = 0; c < 4; c++)
        {
            window[0] = up[left + c];
            window[1] = up[centre + c];
            window[2] = up[right + c];
            window[3] = mid[left + c];
            window[4] = mid[centre + c];
            window[5] = mid[right + c];
            window[6] = down[left + c];
            window[7] = down[centre + c];
            window[8] = down[right + c];

            window.Sort();
            to[centre + c] = window[4];
        }
    }

    private static void CeBytes(ref Vector128<byte> low, ref Vector128<byte> high)
    {
        var a = low;
        var b = high;
        low = Vector128.Min(a, b);
        high = Vector128.Max(a, b);
    }

    /// <summary>The same 19-compare-exchange network as <see cref="Median9"/>, on sixteen byte lanes at once.</summary>
    private static Vector128<byte> Median9Bytes(Vector128<byte> a0, Vector128<byte> a1, Vector128<byte> a2,
        Vector128<byte> a3, Vector128<byte> a4, Vector128<byte> a5, Vector128<byte> a6, Vector128<byte> a7,
        Vector128<byte> a8)
    {
        CeBytes(ref a1, ref a2);
        CeBytes(ref a4, ref a5);
        CeBytes(ref a7, ref a8);

        CeBytes(ref a0, ref a1);
        CeBytes(ref a3, ref a4);
        CeBytes(ref a6, ref a7);

        CeBytes(ref a1, ref a2);
        CeBytes(ref a4, ref a5);
        CeBytes(ref a7, ref a8);

        CeBytes(ref a0, ref a3);
        CeBytes(ref a5, ref a8);
        CeBytes(ref a4, ref a7);

        CeBytes(ref a3, ref a6);
        CeBytes(ref a1, ref a4);
        CeBytes(ref a2, ref a5);

        CeBytes(ref a4, ref a7);
        CeBytes(ref a4, ref a2);
        CeBytes(ref a6, ref a4);

        CeBytes(ref a4, ref a2);
        return a4;
    }

    /// <summary>
    /// Compare-exchange: smaller into <paramref name="low"/>, larger into <paramref name="high"/>.
    /// <para>Unconditional on purpose. An <c>if (low &gt; high) swap</c> version is still faster on already-sorted
    /// data because the branch predicts perfectly — measured 21 ms on a flat colour chart against 45 ms on a
    /// real photograph for the same size. <c>Min</c>/<c>Max</c> compile to <c>minss</c>/<c>maxss</c>, so the cost
    /// no longer depends on the image.</para>
    /// </summary>
    private static void Ce(ref float low, ref float high)
    {
        var a = low;
        var b = high;
        low = MathF.Min(a, b);
        high = MathF.Max(a, b);
    }

    /// <summary>
    /// Paeth's median-of-nine selection network: 19 compare-exchanges, after which <c>a4</c> holds the median.
    /// Verified against a full sort by <c>Median3x3Tests</c> — a wrong network would silently return the wrong
    /// pixel, so it is not taken on faith.
    /// </summary>
    private static void Median9(ref float a0, ref float a1, ref float a2, ref float a3, ref float a4,
        ref float a5, ref float a6, ref float a7, ref float a8)
    {
        Ce(ref a1, ref a2);
        Ce(ref a4, ref a5);
        Ce(ref a7, ref a8);

        Ce(ref a0, ref a1);
        Ce(ref a3, ref a4);
        Ce(ref a6, ref a7);

        Ce(ref a1, ref a2);
        Ce(ref a4, ref a5);
        Ce(ref a7, ref a8);

        Ce(ref a0, ref a3);
        Ce(ref a5, ref a8);
        Ce(ref a4, ref a7);

        Ce(ref a3, ref a6);
        Ce(ref a1, ref a4);
        Ce(ref a2, ref a5);

        Ce(ref a4, ref a7);
        Ce(ref a4, ref a2);
        Ce(ref a6, ref a4);

        Ce(ref a4, ref a2);
    }

    /// <summary>
    /// Sobel gradient magnitude of the luma, written to RGB with the source alpha. Magnitude is
    /// <c>min(1, √(gx² + gy²) / 4)</c>: the Sobel kernel sums to 4 per axis, so dividing by 4 keeps a full
    /// black/white edge inside [0,1] instead of clipping every strong edge to white.
    /// </summary>
    public static PixelBuffer Sobel(PixelView source, bool bt709 = false, IPixelPool? pool = null)
    {
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var (lr, lg, lb) = bt709 ? (0.2126f, 0.7152f, 0.0722f) : (0.299f, 0.587f, 0.114f);

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var luma = new float[3 * width];
        var view = destination.View;

        // Three things were wrong here, none of them arithmetic:
        //   * the luma of every row was recomputed three times (once per dy), never cached;
        //   * `outputRow` was allocated inside the row loop — 1080 allocations of 7.7 KB per call, ~8 MB of
        //     garbage for one 1080p image;
        //   * the three luma spans were rebuilt inside the per-pixel loop.
        // A per-band ring of three luma rows fixes the first (the value is computed by the same expression on
        // the same input, so it is the same float), and hoisting fixes the other two.
        RowParallel.For(width, height, (start, end) =>
        {
            var row = new float[stride];
            var outputRow = new float[stride];
            var luma = new float[3 * width];
            var computed = start - 2;

            void EnsureLuma(int y)
            {
                for (var current = computed + 1; current <= y; current++)
                {
                    var clamped = Math.Clamp(current, 0, height - 1);
                    Pixels.ReadRowRaw(source.Row(clamped), width, source.Format, row);
                    var target = luma.AsSpan((current % 3 + 3) % 3 * width, width);
                    for (var x = 0; x < width; x++)
                    {
                        var o = x * 4;
                        target[x] = row[o] * lr + row[o + 1] * lg + row[o + 2] * lb;
                    }
                }

                computed = Math.Max(computed, y);
            }

            for (var y = start; y < end; y++)
            {
                EnsureLuma(y + 1);

                var top = luma.AsSpan(((y - 1) % 3 + 3) % 3 * width, width);
                var middle = luma.AsSpan((y % 3) * width, width);
                var bottom = luma.AsSpan(((y + 1) % 3) * width, width);

                Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

                for (var x = 0; x < width; x++)
                {
                    var left = Math.Clamp(x - 1, 0, width - 1);
                    var right = Math.Clamp(x + 1, 0, width - 1);

                    var gx = (top[right] + 2f * middle[right] + bottom[right])
                             - (top[left] + 2f * middle[left] + bottom[left]);
                    var gy = (bottom[left] + 2f * bottom[x] + bottom[right])
                             - (top[left] + 2f * top[x] + top[right]);

                    var magnitude = MathF.Sqrt(gx * gx + gy * gy) * 0.25f;
                    if (magnitude > 1f)
                        magnitude = 1f;

                    var o = x * 4;
                    outputRow[o] = magnitude;
                    outputRow[o + 1] = magnitude;
                    outputRow[o + 2] = magnitude;
                    outputRow[o + 3] = row[o + 3];
                }

                Pixels.WriteRowRaw(outputRow, width, source.Format, view.Row(y));
            }
        });

        return destination;
    }

    /// <summary>3×3 erosion (per-channel minimum). Shrinks bright regions and opaque coverage.</summary>
    public static PixelBuffer Erode3x3(PixelView source, IPixelPool? pool = null) => Morph3x3(source, minimum: true, pool);

    /// <summary>3×3 dilation (per-channel maximum).</summary>
    public static PixelBuffer Dilate3x3(PixelView source, IPixelPool? pool = null) => Morph3x3(source, minimum: false, pool);

    /// <summary>Erosion then dilation: removes small bright specks.</summary>
    public static PixelBuffer Open3x3(PixelView source, IPixelPool? pool = null)
    {
        using var eroded = Morph3x3(source, minimum: true, null);
        return Morph3x3(eroded.View, minimum: false, pool);
    }

    /// <summary>Dilation then erosion: fills small dark holes.</summary>
    public static PixelBuffer Close3x3(PixelView source, IPixelPool? pool = null)
    {
        using var dilated = Morph3x3(source, minimum: false, null);
        return Morph3x3(dilated.View, minimum: true, pool);
    }

    /// <summary>
    /// Separable 3×3 minimum/maximum. Vertical neighbours are produced by a rolling buffer of three
    /// horizontal rows, so memory is <c>3 × row</c> instead of a full float image.
    /// </summary>
    private static PixelBuffer Morph3x3(PixelView source, bool minimum, IPixelPool? pool)
    {
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        // One contiguous block, three row offsets into it: a jagged float[3][] would add a pointer chase
        // per tap and three allocations per call for no benefit.
        var rows = new float[3 * stride];
        var sourceRow = new float[stride];
        var output = new float[stride];
        var computed = -1;

        void EnsureRow(int y)
        {
            for (var current = computed + 1; current <= y; current++)
            {
                Pixels.ReadRowRaw(source.Row(current), width, source.Format, sourceRow);
                var targetOffset = (current % 3) * stride;
                for (var x = 0; x < width; x++)
                {
                    var left = Math.Clamp(x - 1, 0, width - 1) * 4;
                    var centre = x * 4;
                    var right = Math.Clamp(x + 1, 0, width - 1) * 4;
                    for (var c = 0; c < 4; c++)
                    {
                        var a = sourceRow[left + c];
                        var b = sourceRow[centre + c];
                        var d = sourceRow[right + c];
                        rows[targetOffset + centre + c] = minimum
                            ? Math.Min(a, Math.Min(b, d))
                            : Math.Max(a, Math.Max(b, d));
                    }
                }

                computed = current;
            }
        }

        EnsureRow(0);
        for (var y = 0; y < height; y++)
        {
            EnsureRow(Math.Min(y + 1, height - 1));
            var topOffset = (Math.Max(y - 1, 0) % 3) * stride;
            var middleOffset = (y % 3) * stride;
            var bottomOffset = (Math.Min(y + 1, height - 1) % 3) * stride;

            for (var i = 0; i < stride; i++)
            {
                var a = rows[topOffset + i];
                var b = rows[middleOffset + i];
                var d = rows[bottomOffset + i];
                output[i] = minimum ? Math.Min(a, Math.Min(b, d)) : Math.Max(a, Math.Max(b, d));
            }

            Pixels.WriteRowRaw(output, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>
    /// Bilateral filter: a spatial Gaussian multiplied by a range Gaussian, so pixels only average with
    /// neighbours of similar colour and edges survive. The range kernel is a 512-entry lookup over squared
    /// RGBA distance — the exponential is evaluated once per table slot, not once per tap.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="radius">Window radius in pixels (&gt; 0).</param>
    /// <param name="sigmaSpace">Spatial sigma; 0 derives <c>radius / 2</c>.</param>
    /// <param name="sigmaRange">Colour sigma, in [0,1] sample units.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The radius or a sigma is not positive.</exception>
    public static PixelBuffer Bilateral(PixelView source, int radius = 2, float sigmaSpace = 0f,
        float sigmaRange = 0.1f, IPixelPool? pool = null)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Radius must be positive.");
        if (!(sigmaRange > 0f) || float.IsInfinity(sigmaRange))
            throw new ArgumentOutOfRangeException(nameof(sigmaRange), sigmaRange, "sigmaRange must be finite and positive.");

        if (sigmaSpace <= 0f)
            sigmaSpace = radius / 2f;
        if (!float.IsFinite(sigmaSpace))
            throw new ArgumentOutOfRangeException(nameof(sigmaSpace), sigmaSpace, "sigmaSpace must be finite.");

        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var window = radius * 2 + 1;

        var spatial = new float[window * window];
        var spatialScale = 1f / (2f * sigmaSpace * sigmaSpace);
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
                spatial[(dy + radius) * window + dx + radius] = MathF.Exp(-(dx * dx + dy * dy) * spatialScale);
        }

        const int RangeLutSize = 512;
        var range = new float[RangeLutSize];
        var rangeScale = (RangeLutSize - 1) / 4f;       // squared RGBA distance lies in [0, 4]
        for (var i = 0; i < RangeLutSize; i++)
        {
            var distanceSquared = i / rangeScale;
            range[i] = MathF.Exp(-distanceSquared / (2f * sigmaRange * sigmaRange));
        }

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;

        // Same shape as OilPaint: the ring of source rows is a cache, so each band keeps its own and the
        // per-pixel arithmetic is untouched. Both were fully serial before — 412 ms on a real 1080p photo.
        RowParallel.For(width, height, (start, end) =>
        {
        var rows = new float[window][];
        for (var i = 0; i < window; i++)
            rows[i] = new float[stride];

        var computed = start - 1;
        var output = new float[stride];

        void EnsureRow(int y)
        {
            for (var current = computed + 1; current <= y; current++)
                Pixels.ReadRowRaw(source.Row(current), width, source.Format, rows[current % window]);
            computed = Math.Max(computed, y);
        }

        for (var y = start; y < end; y++)
        {
            EnsureRow(Math.Min(y + radius, height - 1));

            for (var x = 0; x < width; x++)
            {
                var centre = x * 4;
                var centreRow = rows[y % window];
                var centreR = centreRow[centre];
                var centreG = centreRow[centre + 1];
                var centreB = centreRow[centre + 2];
                var centreA = centreRow[centre + 3];

                float sumR = 0, sumG = 0, sumB = 0, sumA = 0, sumW = 0;

                for (var dy = -radius; dy <= radius; dy++)
                {
                    var sy = Math.Clamp(y + dy, 0, height - 1);
                    var row = rows[sy % window];
                    var spatialRow = (dy + radius) * window;

                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var sx = Math.Clamp(x + dx, 0, width - 1) * 4;
                        var r = row[sx];
                        var g = row[sx + 1];
                        var b = row[sx + 2];
                        var a = row[sx + 3];

                        var dr = r - centreR;
                        var dg = g - centreG;
                        var db = b - centreB;
                        var da = a - centreA;
                        var distanceSquared = dr * dr + dg * dg + db * db + da * da;
                        var index = (int)(distanceSquared * rangeScale);
                        if (index >= RangeLutSize)
                            index = RangeLutSize - 1;

                        var weight = spatial[spatialRow + dx + radius] * range[index];
                        sumR += r * weight;
                        sumG += g * weight;
                        sumB += b * weight;
                        sumA += a * weight;
                        sumW += weight;
                    }
                }

                var inverse = 1f / sumW;
                output[centre] = sumR * inverse;
                output[centre + 1] = sumG * inverse;
                output[centre + 2] = sumB * inverse;
                output[centre + 3] = sumA * inverse;
            }

            Pixels.WriteRowRaw(output, width, source.Format, view.Row(y));
        }
        });

        return destination;
    }

    /// <summary>
    /// 3×3 convolution on the colour channels; alpha is preserved. Edges replicate. The kernel is applied to
    /// straight colour (a sharpen or emboss kernel is defined on values, not on premultiplied coverage).
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="kernel">Nine weights, row-major.</param>
    /// <param name="bias">Added after the convolution, in sample units (0.5 = half range).</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentException">The kernel is not nine values.</exception>
    public static PixelBuffer Convolve3x3(PixelView source, ReadOnlySpan<float> kernel, float bias = 0f,
        IPixelPool? pool = null)
    {
        if (kernel.Length != 9)
            throw new ArgumentException("A 3×3 kernel needs nine weights.", nameof(kernel));

        var weights = kernel.ToArray();
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var rows = new float[3 * stride];       // three rows in one block; see Morph3x3
        var output = new float[stride];
        var computed = -1;

        void EnsureRow(int y)
        {
            for (var current = computed + 1; current <= y; current++)
                Pixels.ReadRowRaw(source.Row(current), width, source.Format,
                    rows.AsSpan((current % 3) * stride, stride));
            computed = Math.Max(computed, y);
        }

        EnsureRow(0);
        for (var y = 0; y < height; y++)
        {
            EnsureRow(Math.Min(y + 1, height - 1));
            var top = (Math.Max(y - 1, 0) % 3) * stride;
            var middle = (y % 3) * stride;
            var bottom = (Math.Min(y + 1, height - 1) % 3) * stride;

            for (var x = 0; x < width; x++)
            {
                var left = Math.Clamp(x - 1, 0, width - 1) * 4;
                var centre = x * 4;
                var right = Math.Clamp(x + 1, 0, width - 1) * 4;

                for (var c = 0; c < 3; c++)
                {
                    var sum = weights[0] * rows[top + left + c] + weights[1] * rows[top + centre + c] + weights[2] * rows[top + right + c]
                              + weights[3] * rows[middle + left + c] + weights[4] * rows[middle + centre + c] + weights[5] * rows[middle + right + c]
                              + weights[6] * rows[bottom + left + c] + weights[7] * rows[bottom + centre + c] + weights[8] * rows[bottom + right + c];
                    output[centre + c] = sum + bias;
                }

                output[centre + 3] = rows[middle + centre + 3];
            }

            Pixels.WriteRowRaw(output, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>Emboss: a relief kernel with a mid-grey offset, so flat areas become grey and edges stand out.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="strength">Multiplier on the kernel; 1 is the classic relief.</param>
    /// <param name="pool">Where the destination is rented.</param>
    public static PixelBuffer Emboss(PixelView source, float strength = 1f, IPixelPool? pool = null)
    {
        // Sum zero (flat areas go to the bias) with a diagonal gradient.
        Span<float> kernel = stackalloc float[9]
        {
            -2f * strength, -1f * strength, 0f,
            -1f * strength, 0f, 1f * strength,
            0f, 1f * strength, 2f * strength,
        };

        return Convolve3x3(source, kernel, 0.5f, pool);
    }

    /// <summary>
    /// Unsharp mask: <c>src + amount · (src - blur(src))</c>. Alpha comes from the source, not from the
    /// difference, so sharpening cannot change coverage.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="amount">Strength; 0.5 is a typical web default.</param>
    /// <param name="sigma">Blur sigma used to build the mask.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is not finite and positive.</exception>
    public static PixelBuffer Sharpen(PixelView source, float amount = 0.5f, float sigma = 1f, IPixelPool? pool = null)
    {
        using var blurred = GaussianBlur(source, sigma, null);

        var width = source.Width;
        var stride = width * 4;
        var destination = PixelBuffer.Rent(source.Height > 0 ? width : width, source.Height, source.Format, pool);
        var sourceRow = new float[stride];
        var blurredRow = new float[stride];
        var output = new float[stride];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, sourceRow);
            Pixels.ReadRowRaw(blurred.View.Row(y), width, source.Format, blurredRow);

            for (var i = 0; i < stride; i += 4)
            {
                output[i] = sourceRow[i] + amount * (sourceRow[i] - blurredRow[i]);
                output[i + 1] = sourceRow[i + 1] + amount * (sourceRow[i + 1] - blurredRow[i + 1]);
                output[i + 2] = sourceRow[i + 2] + amount * (sourceRow[i + 2] - blurredRow[i + 2]);
                output[i + 3] = sourceRow[i + 3];
            }

            Pixels.WriteRowRaw(output, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>
    /// Gaussian sharpen as a single convolution: a negative Gaussian shell around a boosted centre,
    /// <c>(1 + amount)·δ − amount·g</c>. Algebraically the same operator as <see cref="Sharpen"/>, but the two
    /// kernels are combined first, so edges are clamped once instead of twice and the pass count is one.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="amount">Strength; 0 is identity.</param>
    /// <param name="sigma">Sigma of the Gaussian shell.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is not finite and positive.</exception>
    public static PixelBuffer GaussianSharpen(PixelView source, float amount = 0.5f, float sigma = 1f,
        IPixelPool? pool = null)
    {
        if (!(sigma > 0f) || float.IsInfinity(sigma))
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be finite and greater than 0.");

        var radius = Math.Max(1, (int)MathF.Ceiling(3f * sigma));
        var side = radius * 2 + 1;

        var gaussian = new float[side];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var weight = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            gaussian[i + radius] = weight;
            sum += weight;
        }

        for (var i = 0; i < side; i++)
            gaussian[i] /= sum;

        var kernel = new float[side * side];
        for (var ky = 0; ky < side; ky++)
        {
            for (var kx = 0; kx < side; kx++)
                kernel[ky * side + kx] = -amount * gaussian[ky] * gaussian[kx];
        }

        // The centre tap carries the impulse that the negative shell is subtracted from; the kernel sums to
        // one, so a flat region comes back unchanged.
        kernel[radius * side + radius] = 1f + amount - amount * gaussian[radius] * gaussian[radius];

        return Convolve2D(source, kernel, radius, pool);
    }

    /// <summary>
    /// Full 2-D convolution in premultiplied space with replicated edges. Alpha is copied from the centre
    /// pixel rather than filtered: a negative lobe would otherwise make coverage go negative and clamp to a
    /// translucent halo.
    /// </summary>
    private static PixelBuffer Convolve2D(PixelView source, float[] kernel, int radius, IPixelPool? pool)
    {
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var side = radius * 2 + 1;

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;

        // Row bands, each with its own ring of source rows. This is the shared path behind GaussianSharpen,
        // emboss and every other 2D kernel, and it was serial: sharpen on a real 1080p photo took 282 ms,
        // almost all of it in one core stepping 81 taps per pixel.
        RowParallel.For(width, height, (start, end) =>
        {
        // FloatScratch, not `new`: the audit caught these three per worker per call (81 taps at 1080p is
        // 311 KB, bokeh r6 is 449 KB, times 16 workers, every call). FloatScratch is thread-local and reuses
        // the buffer, which is the whole point of it existing. Every region read below is written first —
        // `rows` by EnsureRow up to `computed`, `acc` by Array.Clear, `output` completely.
        var rows = FloatScratch.Rent(side * stride);
        var output = FloatScratch.Rent(stride);
        var acc = FloatScratch.Rent(stride);
        var computed = start - 1;

        void EnsureRow(int y)
        {
            for (var current = computed + 1; current <= y; current++)
            {
                Pixels.ReadRowPremultiplied(source.Row(current), width, source.Format,
                    rows.AsSpan((current % side) * stride, stride));
            }

            computed = Math.Max(computed, y);
        }

        try
        {
        EnsureRow(Math.Min(start + radius, height - 1));
        for (var y = start; y < end; y++)
        {
            EnsureRow(Math.Min(y + radius, height - 1));
            var centre = rows.AsSpan((y % side) * stride, stride);

            // Shifted-row accumulation, same as the bokeh path: a tap is one shift for the whole row, so the
            // row clamp is hoisted to one per tap and the horizontal shift becomes a vector multiply-add of
            // eight floats. Taps are still visited in ky-major / kx-minor order, so every lane sees the same
            // additions in the same order and the result is bit-identical.
            Array.Clear(acc, 0, stride);

            for (var ky = 0; ky < side; ky++)
            {
                var sy = Math.Clamp(y + ky - radius, 0, height - 1);
                var src = rows.AsSpan((sy % side) * stride, stride);
                var kernelRow = ky * side;

                for (var kx = 0; kx < side; kx++)
                {
                    var weight = kernel[kernelRow + kx];
                    var shift = (kx - radius) * 4;
                    var lo = Math.Max(0, shift);
                    var hi = Math.Min(stride, stride + shift);
                    var j = lo;

                    var wv = Vector256.Create(weight);
                    for (; j + 8 <= hi; j += 8)
                    {
                        var a = Vector256.LoadUnsafe(ref acc[j]);
                        var b = Vector256.LoadUnsafe(ref src[j - shift]);
                        (a + b * wv).StoreUnsafe(ref acc[j]);
                    }

                    for (; j < hi; j++)
                        acc[j] += src[j - shift] * weight;
                }
            }

            // Alpha is carried through untouched, exactly as before — the accumulator also summed the alpha
            // lane, so it has to be overwritten from the centre row rather than written out.
            for (var j = 0; j < stride; j++)
                output[j] = acc[j];
            for (var x = 0; x < width; x++)
                output[x * 4 + 3] = centre[x * 4 + 3];

            // Pixels within `radius` of either side had clamped taps, which the shifted adds skipped.
            for (var x = 0; x < width; x++)
            {
                if (x >= radius && x + radius <= width - 1)
                    continue;

                float sumR = 0f, sumG = 0f, sumB = 0f;
                for (var ky = 0; ky < side; ky++)
                {
                    var sy = Math.Clamp(y + ky - radius, 0, height - 1);
                    var row = rows.AsSpan((sy % side) * stride, stride);
                    var kernelRow = ky * side;
                    for (var kx = 0; kx < side; kx++)
                    {
                        var weight = kernel[kernelRow + kx];
                        var sx = Math.Clamp(x + kx - radius, 0, width - 1) * 4;
                        sumR += weight * row[sx];
                        sumG += weight * row[sx + 1];
                        sumB += weight * row[sx + 2];
                    }
                }

                var offset = x * 4;
                output[offset] = sumR;
                output[offset + 1] = sumG;
                output[offset + 2] = sumB;
            }

            Pixels.WriteRowUnpremultiplied(output, width, source.Format, view.Row(y));
        }
        }
        finally
        {
            FloatScratch.Return(rows);
            FloatScratch.Return(output);
            FloatScratch.Return(acc);
        }
        });

        return destination;
    }

    /// <summary>
    /// Motion blur: averages samples along a line at <paramref name="angleDegrees"/>. Sampling is nearest
    /// (no resampling filter), which is what a real shutter smear looks like at this scale.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="length">Number of samples along the line; 1 is a no-op.</param>
    /// <param name="angleDegrees">0 = horizontal, 90 = vertical.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive.</exception>
    public static PixelBuffer MotionBlur(PixelView source, int length = 9, float angleDegrees = 0f,
        IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (length == 1)
            return Geometry.Transforms.Copy(source, pool);

        var radians = angleDegrees * MathF.PI / 180f;
        var dx = MathF.Cos(radians);
        var dy = MathF.Sin(radians);
        var half = (length - 1) / 2f;

        var offsets = new (int Dx, int Dy)[length];
        for (var i = 0; i < length; i++)
        {
            var t = i - half;
            offsets[i] = ((int)MathF.Round(t * dx), (int)MathF.Round(t * dy));
        }

        return AverageOffsets(source, offsets, pool);
    }

    /// <summary>
    /// Bokeh blur: averages every pixel inside a disc of <paramref name="radius"/>, the shape a wide-open
    /// aperture produces. Unlike a box blur the corners of the bounding square are excluded.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is not positive.</exception>
    public static PixelBuffer BokehBlur(PixelView source, int radius = 4, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);

        var offsets = new List<(int Dx, int Dy)>((2 * radius + 1) * (2 * radius + 1));
        var squared = radius * radius;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy <= squared)
                    offsets.Add((dx, dy));
            }
        }

        return AverageOffsets(source, CollectionsMarshal.AsSpan(offsets), pool);
    }

    /// <summary>
    /// Pixelates: each <paramref name="blockSize"/> square becomes its own average. Averaging happens in
    /// premultiplied space so a transparent pixel cannot drag the block's colour toward black; the block is
    /// then written flat, which is what makes the mosaic look blocky rather than blurred.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="blockSize">Edge length of a block, in pixels; 1 is a copy.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not positive.</exception>
    public static PixelBuffer Pixelate(PixelView source, int blockSize, IPixelPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        if (blockSize == 1)
            return Geometry.Transforms.Copy(source, pool);

        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var columns = (width + blockSize - 1) / blockSize;

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;

        // Two things made this 6x slower than the oracle (14.56 ms against 2.26 ms at 1080p):
        //
        //   * `column = x / blockSize` in the inner loop — one integer division per pixel, two million of
        //     them. Walking block-major removes it, and each accumulator still sees its samples in the same
        //     row-major order, so every sum is bit-identical.
        //   * the whole thing ran on one thread. Block rows are independent, so bands cannot change a byte.
        var blockRows = (height + blockSize - 1) / blockSize;
        RowParallel.ForBands(blockRows, (firstBand, lastBand) =>
        {
            var sums = new float[columns * 4];
            var row = new float[stride];
            var output = new float[stride];

            for (var band = firstBand; band < lastBand; band++)
            {
                var top = band * blockSize;
                var bottom = Math.Min(top + blockSize, height);
                var count = bottom - top;
                Array.Clear(sums);

                for (var y = top; y < bottom; y++)
                {
                    Pixels.ReadRowPremultiplied(source.Row(y), width, source.Format, row);
                    var x = 0;
                    for (var column = 0; column < columns; column++)
                    {
                        var columnEnd = Math.Min(x + blockSize, width);
                        var source4 = column * 4;
                        for (; x < columnEnd; x++)
                        {
                            var offset = x * 4;
                            sums[source4] += row[offset];
                            sums[source4 + 1] += row[offset + 1];
                            sums[source4 + 2] += row[offset + 2];
                            sums[source4 + 3] += row[offset + 3];
                        }
                    }
                }

                Array.Clear(output);
                for (var column = 0; column < columns; column++)
                {
                    var source4 = column * 4;
                    var alphaSum = sums[source4 + 3];
                    var inverse = alphaSum > 0f ? 1f / alphaSum : 0f;
                    var r = sums[source4] * inverse;
                    var g = sums[source4 + 1] * inverse;
                    var b = sums[source4 + 2] * inverse;
                    var a = alphaSum / count;

                    var start = column * blockSize;
                    var endColumn = Math.Min(start + blockSize, width);
                    for (var x = start; x < endColumn; x++)
                    {
                        var offset = x * 4;
                        output[offset] = r;
                        output[offset + 1] = g;
                        output[offset + 2] = b;
                        output[offset + 3] = a;
                    }
                }

                for (var y = top; y < bottom; y++)
                    Pixels.WriteRowRaw(output, width, source.Format, view.Row(y));
            }
        });

        return destination;
    }

    /// <summary>
    /// Radial darkening: nothing at the centre, full <paramref name="strength"/> from <paramref name="radius"/>
    /// out to the corners, smoothstep in between. Distances are normalised by the half-diagonal, so the
    /// falloff is independent of the aspect ratio.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="radius">Where the falloff starts, as a fraction of the half-diagonal (0..1).</param>
    /// <param name="strength">How far the corners are darkened, 0..1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The radius or strength is outside 0..1.</exception>
    public static PixelBuffer Vignette(PixelView source, float radius = 0.75f, float strength = 1f,
        IPixelPool? pool = null)
    {
        if (!(radius > 0f) || radius > 1f)
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Radius must be in (0, 1].");
        if (!(strength >= 0f) || strength > 1f)
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "Strength must be in [0, 1].");

        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var centreX = width * 0.5f;
        var centreY = height * 0.5f;
        var halfDiagonal = MathF.Sqrt(centreX * centreX + centreY * centreY);
        var span = MathF.Max(1f - radius, 1e-6f);

        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var row = new float[stride];

        for (var y = 0; y < height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            var dy = (y + 0.5f - centreY) / halfDiagonal;

            for (var x = 0; x < width; x++)
            {
                var dx = (x + 0.5f - centreX) / halfDiagonal;
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                var t = Math.Clamp((distance - radius) / span, 0f, 1f);
                var shade = 1f - strength * (t * t * (3f - 2f * t));

                var offset = x * 4;
                row[offset] *= shade;
                row[offset + 1] *= shade;
                row[offset + 2] *= shade;
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>
    /// Glow: extract everything brighter than <paramref name="threshold"/>, blur it, add it back. The mask is
    /// built with alpha so the blur spreads a soft halo instead of a hard-edged copy of the highlights.
    /// </summary>
    /// <param name="source">The pixels.</param>
    /// <param name="threshold">Luma above which a pixel contributes, 0..1.</param>
    /// <param name="sigma">Blur sigma of the halo.</param>
    /// <param name="amount">How much of the halo is added.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The threshold is outside 0..1, sigma is not positive, or
    /// the amount is negative.</exception>
    public static PixelBuffer Glow(PixelView source, float threshold = 0.7f, float sigma = 8f, float amount = 0.6f,
        IPixelPool? pool = null)
    {
        if (!(threshold >= 0f) || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be in [0, 1].");
        if (!(sigma > 0f) || float.IsInfinity(sigma))
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be finite and greater than 0.");
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        var width = source.Width;
        var stride = width * 4;
        using var highlights = PixelBuffer.Rent(width, source.Height, source.Format, null);
        var row = new float[stride];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                var luma = 0.299f * row[offset] + 0.587f * row[offset + 1] + 0.114f * row[offset + 2];
                if (luma > threshold)
                {
                    row[offset + 3] = 1f;
                }
                else
                {
                    row[offset] = 0f;
                    row[offset + 1] = 0f;
                    row[offset + 2] = 0f;
                    row[offset + 3] = 0f;
                }
            }

            Pixels.WriteRowRaw(row, width, source.Format, highlights.View.Row(y));
        }

        using var halo = GaussianBlur(highlights.View, sigma, null);

        var destination = PixelBuffer.Rent(width, source.Height, source.Format, pool);
        var glowRow = new float[stride];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);
            Pixels.ReadRowRaw(halo.View.Row(y), width, source.Format, glowRow);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                row[offset] = Math.Clamp(row[offset] + glowRow[offset] * amount, 0f, 1f);
                row[offset + 1] = Math.Clamp(row[offset + 1] + glowRow[offset + 1] * amount, 0f, 1f);
                row[offset + 2] = Math.Clamp(row[offset + 2] + glowRow[offset + 2] * amount, 0f, 1f);
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>Uniform average over a set of integer offsets, in premultiplied space, edges replicated.</summary>
    private static PixelBuffer AverageOffsets(PixelView source, ReadOnlySpan<(int Dx, int Dy)> offsets, IPixelPool? pool)
    {
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;

        // A ReadOnlySpan cannot be captured by a lambda, so the offset list is copied once. It is a few
        // hundred tuples, against 2 M pixels of work.
        var offsetList = offsets.ToArray();

        var maxDy = 0;
        var maxDx = 0;
        foreach (var offset in offsetList)
        {
            maxDy = Math.Max(maxDy, Math.Abs(offset.Dy));
            maxDx = Math.Max(maxDx, Math.Abs(offset.Dx));
        }

        var window = maxDy * 2 + 1;
        var destination = PixelBuffer.Rent(width, height, source.Format, pool);
        var view = destination.View;

        // Same treatment as Convolve2D: the ring is a row cache, so each band keeps its own and the per-pixel
        // sum is untouched. Bokeh is the heaviest operator in the library (a disc of 169 taps at radius 6) and
        // was running on one core: 533 ms on a real 1080p photo.
        RowParallel.For(width, height, (start, end) =>
        {
        var rows = FloatScratch.Rent(window * stride);
        var output = FloatScratch.Rent(stride);
        var acc = FloatScratch.Rent(stride);
        var computed = start - 1;

        void EnsureRow(int y)
        {
            for (var current = computed + 1; current <= y; current++)
            {
                Pixels.ReadRowPremultiplied(source.Row(current), width, source.Format,
                    rows.AsSpan((current % window) * stride, stride));
            }

            computed = Math.Max(computed, y);
        }

        try
        {
        EnsureRow(Math.Min(start + maxDy, height - 1));
        for (var y = start; y < end; y++)
        {
            EnsureRow(Math.Min(y + maxDy, height - 1));

            // Shifted-row accumulation instead of per-pixel taps. Every tap is the same shift for the whole
            // row, so the row clamp is hoisted to one per tap (not one per pixel per tap) and the horizontal
            // shift becomes a plain vector add eight floats at a time. Each lane still visits the taps in
            // offsetList order, so the sum — and therefore the byte — is unchanged.
            Array.Clear(acc, 0, stride);

            foreach (var (dx, dy) in offsetList)
            {
                var sy = Math.Clamp(y + dy, 0, height - 1);
                var src = rows.AsSpan((sy % window) * stride, stride);
                var shift = dx * 4;

                // acc[j] += src[j - shift]; only the j where the source stays inside the row.
                var lo = Math.Max(0, shift);
                var hi = Math.Min(stride, stride + shift);
                var j = lo;
                for (; j + 8 <= hi; j += 8)
                {
                    var a = Vector256.LoadUnsafe(ref acc[j]);
                    var b = Vector256.LoadUnsafe(ref src[j - shift]);
                    (a + b).StoreUnsafe(ref acc[j]);
                }

                for (; j < hi; j++)
                    acc[j] += src[j - shift];
            }

            // Unpremultiplied write happens later, so this stays premultiplied: exactly what the old code
            // stored in `output` before WriteRowUnpremultiplied.
            var scale = 1f / offsetList.Length;
            for (var j = 0; j < stride; j++)
                output[j] = acc[j] * scale;

            // Pixels within maxDx of either side had at least one tap clamped, which the shifted adds above
            // deliberately skipped. They are a fraction of a percent of the row; recompute them exactly.
            for (var x = 0; x < width; x++)
            {
                if (x >= maxDx && x + maxDx <= width - 1)
                    continue;

                float sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                foreach (var (dx, dy) in offsetList)
                {
                    var sy = Math.Clamp(y + dy, 0, height - 1);
                    var sx = Math.Clamp(x + dx, 0, width - 1);
                    var index = (sy % window) * stride + sx * 4;
                    sumR += rows[index];
                    sumG += rows[index + 1];
                    sumB += rows[index + 2];
                    sumA += rows[index + 3];
                }

                var o = x * 4;
                output[o] = sumR * scale;
                output[o + 1] = sumG * scale;
                output[o + 2] = sumB * scale;
                output[o + 3] = sumA * scale;
            }

            Pixels.WriteRowUnpremultiplied(output, width, source.Format, view.Row(y));
        }
        }
        finally
        {
            FloatScratch.Return(rows);
            FloatScratch.Return(output);
            FloatScratch.Return(acc);
        }
        });

        return destination;
    }

}
