using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Internal;

/// <summary>
/// The 64 KiB table that turns a two-byte combine into a table lookup: <c>f(sourceByte, otherByte)</c> packed as
/// one byte per (s, o) pair. Every operator that adds or blends two 8-bit images needs exactly this shape — sharpen
/// blends a source with its blur, glow adds a blurred highlight — and they differ only in the arithmetic inside the
/// table.
/// <para>
/// This type exists because the same twenty-line table build and the same row loop were written out by hand in
/// three places, and each copy re-allocated its table per band. The band executor calls a step once per band, so on
/// a 1920x1080 sharpen that was seventeen 64 KiB arrays per call — measured as 1554 KiB allocated against 66 KiB
/// for the same operator on one band. Handing the table in as a <see cref="Lazy{T}"/> makes it per-step state, the
/// same device <c>PlanSteps.PointTable</c> already uses for its one-input table.
/// </para>
/// <para>
/// The row loop is shared for the same reason the table is: copy-pasted loops drift, and the alpha rule (take it
/// from the source, never from the combine) is the kind of detail that is right in one copy and forgotten in the
/// next. Bytes are unchanged — the arithmetic that produced the table is passed in by the caller.
/// </para>
/// </summary>
internal static class TwoInputLut
{
    /// <summary>Bytes one table occupies: 256 source values x 256 other values.</summary>
    internal const int TableBytes = 256 * 256;

    /// <summary>
    /// Pixels below which a table is not worth building. Below it the operator's own float loop is cheaper than
    /// building 64 KiB and chasing it through cache; the callers all test this before taking the table path.
    /// </summary>
    internal const long PixelFloor = RowParallel.MinWorkPixels;

    /// <summary>Whether the table path applies: 8-bit source and destination, and enough pixels to amortise it.</summary>
    internal static bool Applies(PixelView source, PixelView destination) =>
        source.Format == WorkingFormat.Rgba8
        && destination.Format == WorkingFormat.Rgba8
        && (long)source.Width * source.Height >= PixelFloor;

    /// <summary>
    /// Builds the table for one combine. The caller supplies the arithmetic; this owns the packing rule so every
    /// table quantises the same way.
    /// </summary>
    /// <param name="combine">Maps the two normalised samples to the combined value, already in 0..1.</param>
    private static byte[] Build(Func<float, float, float> combine)
    {
        var table = new byte[TableBytes];
        for (var s8 = 0; s8 < 256; s8++)
        {
            var s = s8 * (1f / 255f);
            var row = s8 << 8;
            for (var o8 = 0; o8 < 256; o8++)
            {
                var value = combine(s, o8 * (1f / 255f));

                // Exactly Pixels.ToByte: scale, add a half, truncate, clamp. MathF.Round is banker's rounding and
                // would move one code in every hundred pixels, so the two sides of a comparison would disagree.
                var scaled = (value * 255f) + 0.5f;
                table[row | o8] = scaled <= 0f ? (byte)0 : scaled >= 255f ? (byte)255 : (byte)scaled;
            }
        }

        return table;
    }

    /// <summary>
    /// A sharpen-style combine: <c>s + amount * (s - other)</c>, alpha taken from the source. The table is built
    /// once on first use and shared by every band of the step.
    /// </summary>
    internal static Lazy<byte[]> SharpenTable(float amount) =>
        new(() => Build((s, b) => s + (amount * (s - b))),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A glow-style combine: <c>s + other * amount</c> clamped, alpha taken from the source.
    /// </summary>
    internal static Lazy<byte[]> GlowTable(float amount) =>
        new(() => Build((s, h) => Math.Clamp(s + (h * amount), 0f, 1f)),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Applies a table to every row of <paramref name="source"/>, reading <paramref name="other"/> alongside it and
    /// writing back over <paramref name="destination"/> (which is usually the same view as <paramref name="other"/>,
    /// because the combine is in place).
    /// <para>
    /// Alpha comes from the source: a sharpen or a glow must not change coverage.
    /// </para>
    /// </summary>
    /// <param name="table">The built table; the caller passes <c>lazy.Value</c> after it decided to take this path.</param>
    /// <remarks>
    /// Four pixels per iteration, each an independent lookup chain. The eight lookups of a four-pixel group all hit
    /// L1 (the table is 64 KiB and stays resident), so the loop is bound by load-to-use latency rather than by
    /// bandwidth — measured 1.7 GB/s against a machine that does tens of GB/s. One lookup at a time leaves that
    /// latency exposed; four in flight overlap it, which measured 2.6-2.7x on this shape with identical bytes.
    /// <para>
    /// AVX2 gather was tried first and is slower here (0.81x): a gather of 32-bit lanes costs more than the four
    /// scalar loads it replaces on this microarchitecture, and the table is bytes, so it would have to be widened
    /// to 32-bit lanes first. The unroll needs no instruction-set check, works on every tier, and keeps the scalar
    /// version as its own tail.
    /// </para>
    /// </remarks>
    internal static void Apply(byte[] table, PixelView source, PixelView other, PixelView destination)
    {
        var width = source.Width;
        RowParallel.For(width, source.Height, (start, end) =>
        {
            for (var y = start; y < end; y++)
            {
                var sourceRow = source.Row(y);
                var otherRow = other.Row(y);
                var output = destination.Row(y);

                var i = 0;
                for (; i + 16 <= sourceRow.Length; i += 16)
                {
                    var p1 = i + 4;
                    var p2 = i + 8;
                    var p3 = i + 12;

                    // Interleaved on purpose: each statement depends only on its own bytes, so the processor can
                    // issue the next pixel's load before the previous one's result is used.
                    output[i] = table[(sourceRow[i] << 8) | otherRow[i]];
                    output[p1] = table[(sourceRow[p1] << 8) | otherRow[p1]];
                    output[p2] = table[(sourceRow[p2] << 8) | otherRow[p2]];
                    output[p3] = table[(sourceRow[p3] << 8) | otherRow[p3]];

                    output[i + 1] = table[(sourceRow[i + 1] << 8) | otherRow[i + 1]];
                    output[p1 + 1] = table[(sourceRow[p1 + 1] << 8) | otherRow[p1 + 1]];
                    output[p2 + 1] = table[(sourceRow[p2 + 1] << 8) | otherRow[p2 + 1]];
                    output[p3 + 1] = table[(sourceRow[p3 + 1] << 8) | otherRow[p3 + 1]];

                    output[i + 2] = table[(sourceRow[i + 2] << 8) | otherRow[i + 2]];
                    output[p1 + 2] = table[(sourceRow[p1 + 2] << 8) | otherRow[p1 + 2]];
                    output[p2 + 2] = table[(sourceRow[p2 + 2] << 8) | otherRow[p2 + 2]];
                    output[p3 + 2] = table[(sourceRow[p3 + 2] << 8) | otherRow[p3 + 2]];

                    output[i + 3] = sourceRow[i + 3];
                    output[p1 + 3] = sourceRow[p1 + 3];
                    output[p2 + 3] = sourceRow[p2 + 3];
                    output[p3 + 3] = sourceRow[p3 + 3];
                }

                // Tail: fewer than four pixels left, or a width that is not a multiple of four (it always is for
                // Rgba8, but the loop must not depend on that).
                for (; i < sourceRow.Length; i += 4)
                {
                    output[i] = table[(sourceRow[i] << 8) | otherRow[i]];
                    output[i + 1] = table[(sourceRow[i + 1] << 8) | otherRow[i + 1]];
                    output[i + 2] = table[(sourceRow[i + 2] << 8) | otherRow[i + 2]];
                    output[i + 3] = sourceRow[i + 3];
                }
            }
        });
    }
}
