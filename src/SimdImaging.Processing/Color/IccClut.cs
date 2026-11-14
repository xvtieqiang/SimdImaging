namespace SimdImaging.Processing;

/// <summary>
/// One 1-D table of an ICC LUT, in the 16-bit domain every LUT type shares: the first entry sits at 0,
/// the last at 65535, and the entries in between are evenly spaced. A table with no entries is the
/// identity, which is how the ICC types spell "this stage is absent".
/// <para>
/// The arithmetic is integer. Position is <c>value · (n − 1) / 65535</c>, evaluated with an exact
/// product and remainder rather than a float multiply, so every SIMD tier lands on the same code.
/// </para>
/// </summary>
internal sealed class Curve1D
{
    private const int Domain = 65535;

    private readonly ushort[]? _table;

    private Curve1D(ushort[]? table) => _table = table;

    /// <summary>The identity: no table, the value passes through.</summary>
    public static Curve1D Identity { get; } = new(null);

    public static Curve1D FromTable(ushort[] table)
    {
        if (table.Length == 0)
            throw new ArgumentException("An ICC curve table needs at least one entry.", nameof(table));

        return new Curve1D(table);
    }

    /// <summary>Entries in the table; zero for the identity.</summary>
    public int Count => _table?.Length ?? 0;

    public ushort Map(ushort value)
    {
        var table = _table;
        if (table is null)
            return value;
        if (table.Length == 1)
            return table[0];

        var product = (long)value * (table.Length - 1);
        var index = (int)(product / Domain);
        if (index >= table.Length - 1)
            return table[^1];

        var fraction = (int)(product - (long)index * Domain);
        var a = table[index];
        var b = table[index + 1];

        // Rounded, not truncated: a truncating table biases every stage down by half a code.
        var delta = (long)(b - a) * fraction;
        var rounded = delta >= 0
            ? (delta + Domain / 2) / Domain
            : -((-delta + Domain / 2) / Domain);

        return (ushort)Math.Clamp(a + rounded, 0, 65535);
    }
}

/// <summary>
/// A 3-D ICC CLUT: a cube of <c>size³</c> anchor points per output channel, evaluated tetrahedrally.
/// <para>
/// Tetrahedral, not trilinear, because that is what the ICC specification defines for a 3-D CLUT and
/// what a print profile was measured against: it splits the cube into six tetrahedra along the
/// diagonal, so a function that is planar inside the cube is reproduced exactly instead of being
/// rounded towards the cube's own diagonal.
/// </para>
/// <para>
/// Weights are Q15 and the accumulator is <see cref="long"/>. A corner difference spans ±65535 and a
/// weight spans 32767, so one term needs 32 bits and four of them need 34; 64 is ample, and the single
/// final shift is the only rounding step. Integer in, integer out: the whole stage is bit-exact by
/// construction, which is why the CLUT half of an ICC transform can claim D0 even though the PCS seam
/// around it is D1.
/// </para>
/// </summary>
internal sealed class IccClut3D
{
    private const int Channels = 3;
    private const int OneQ15 = 32768;

    private readonly ushort[] _grid;
    private readonly int _size;

    public IccClut3D(ushort[] grid, int size)
    {
        if (size < 2)
            throw new InvalidDataException($"An ICC CLUT needs at least 2 grid points per axis; this one declares {size}.");

        var expected = checked((long)size * size * size * Channels);
        if (grid.LongLength != expected)
            throw new InvalidDataException(
                $"An ICC CLUT with {size}³ points and {Channels} channels needs {expected} samples; the tag holds {grid.LongLength}.");

        _grid = grid;
        _size = size;
    }

    public int Size => _size;

    /// <summary>Samples the cube at <paramref name="input"/> (three 16-bit codes), writing three codes.</summary>
    public void Evaluate(ReadOnlySpan<ushort> input, Span<ushort> output)
    {
        if (input.Length < Channels || output.Length < Channels)
            throw new ArgumentException("An ICC CLUT evaluates three channels.");

        Evaluate(input[0], input[1], input[2], out output[0], out output[1], out output[2]);
    }

    /// <summary>
    /// The per-pixel form. It exists because a span does not: hoisting the three codes into arguments
    /// lets the whole stage chain run in locals, which matters more than the arithmetic does — measured
    /// at 1080p, a 24-sample cube and a 215 KB cube differ by 7 ms, while the per-pixel scaffolding
    /// around them costs more than that.
    /// </summary>
    public void Evaluate(ushort inputR, ushort inputG, ushort inputB,
        out ushort outputR, out ushort outputG, out ushort outputB)
    {
        Split(inputR, out var ir, out var fr);
        Split(inputG, out var ig, out var fg);
        Split(inputB, out var ib, out var fb);

        var strideR = (long)_size * _size * Channels;
        var strideG = (long)_size * Channels;
        var origin = (int)(ir * strideR + ig * strideG + ib * Channels);

        // The six tetrahedra, chosen by the order of the three fractions. Each names the three corners
        // the point walks through on its way to 111, and the fraction that weights that step: the walk
        // along an axis is owned by the axis, so the weights are the fractions in visiting order.
        int c1, c2, c3, w1, w2, w3;
        if (fr >= fg)
        {
            if (fg >= fb)
            {
                c1 = origin + (int)strideR;               // 100
                c2 = origin + (int)(strideR + strideG);   // 110
                c3 = origin + (int)(strideR + strideG + Channels); // 111
                (w1, w2, w3) = (fr, fg, fb);
            }
            else if (fr >= fb)
            {
                c1 = origin + (int)strideR;               // 100
                c2 = origin + (int)(strideR + Channels);  // 101
                c3 = origin + (int)(strideR + strideG + Channels);
                (w1, w2, w3) = (fr, fb, fg);
            }
            else
            {
                c1 = origin + Channels;                   // 001
                c2 = origin + (int)(strideR + Channels);  // 101
                c3 = origin + (int)(strideR + strideG + Channels);
                (w1, w2, w3) = (fb, fr, fg);
            }
        }
        else
        {
            if (fb >= fg)
            {
                c1 = origin + Channels;                        // 001
                c2 = origin + (int)(strideG + Channels);       // 011
                c3 = origin + (int)(strideR + strideG + Channels);
                (w1, w2, w3) = (fb, fg, fr);
            }
            else if (fb >= fr)
            {
                c1 = origin + (int)strideG;                    // 010
                c2 = origin + (int)(strideG + Channels);       // 011
                c3 = origin + (int)(strideR + strideG + Channels);
                (w1, w2, w3) = (fg, fb, fr);
            }
            else
            {
                c1 = origin + (int)strideG;                    // 010
                c2 = origin + (int)(strideR + strideG);        // 110
                c3 = origin + (int)(strideR + strideG + Channels);
                (w1, w2, w3) = (fg, fr, fb);
            }
        }

        outputR = Channel(origin, c1, c2, c3, w1, w2, w3, 0);
        outputG = Channel(origin, c1, c2, c3, w1, w2, w3, 1);
        outputB = Channel(origin, c1, c2, c3, w1, w2, w3, 2);
    }

    private ushort Channel(int origin, int c1, int c2, int c3, int w1, int w2, int w3, int channel)
    {
        int v0 = _grid[origin + channel];
        int v1 = _grid[c1 + channel];
        int v2 = _grid[c2 + channel];
        int v3 = _grid[c3 + channel];

        var accumulator = (long)v0 * OneQ15
                          + (long)(v1 - v0) * w1
                          + (long)(v2 - v1) * w2
                          + (long)(v3 - v2) * w3;

        return (ushort)Math.Clamp((accumulator + OneQ15 / 2) >> 15, 0, 65535);
    }

    /// <summary>Splits a 16-bit code into a grid cell and a Q15 position inside it.</summary>
    private void Split(ushort value, out int index, out int fractionQ15)
    {
        var product = (long)value * (_size - 1);
        index = (int)(product / 65535);
        if (index >= _size - 1)
        {
            // The top code lands exactly on the last anchor; keeping it in the last cell with a full
            // weight is the same point and avoids an out-of-range corner fetch.
            index = _size - 2;
            fractionQ15 = OneQ15;
            return;
        }

        fractionQ15 = (int)((product - (long)index * 65535) >> 1);
    }
}
