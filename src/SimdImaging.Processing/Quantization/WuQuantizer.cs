using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Wu's quantizer: a 5-bit-per-channel 3-D histogram with cumulative moments, split by maximal variance
/// reduction. Unlike median cut it never sorts — the split point is found by evaluating every cut against
/// the moment tables — and the resulting palette minimizes squared error over the boxes it produces.
/// <para>
/// All arithmetic is integer until the final variance comparison, so the output is deterministic.
/// </para>
/// </summary>
public static class WuQuantizer
{
    private const int Bits = 5;
    private const int Side = (1 << Bits) + 1;     // 33: one extra row/plane of zeros for the cumulative table
    private const int CubeSize = Side * Side * Side;

    /// <summary>Builds a palette of at most <paramref name="maxColors"/> colours.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxColors"/> is outside 2..256.</exception>
    public static ColorPalette Build(PixelView source, int maxColors)
    {
        if (maxColors is < 2 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maxColors), maxColors, "A palette holds 2..256 colours.");

        var tables = new Moments();
        tables.Accumulate(source);
        tables.MakeCumulative();

        var boxes = new List<Cube> { Cube.Full };
        while (boxes.Count < maxColors)
        {
            var bestIndex = -1;
            var bestAxis = -1;
            var bestCut = -1;
            var bestValue = double.MinValue;

            for (var i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                for (var axis = 0; axis < 3; axis++)
                {
                    if (box.Length(axis) < 2)
                        continue;

                    for (var cut = box.Lower(axis) + 1; cut < box.Upper(axis); cut++)
                    {
                        var left = box.Cut(axis, cut, first: true);
                        var right = box.Cut(axis, cut, first: false);

                        // Reject only an empty half. A half holding a single colour has zero variance but is
                        // exactly the useful result of splitting two well-separated colours — rejecting it
                        // would leave the palette at one entry.
                        if (tables.Weight(left) == 0 || tables.Weight(right) == 0)
                            continue;

                        var value = tables.Variance(left) + tables.Variance(right);
                        if (value > bestValue)
                        {
                            bestValue = value;
                            bestIndex = i;
                            bestAxis = axis;
                            bestCut = cut;
                        }
                    }
                }
            }

            if (bestIndex < 0)
                break;

            var split = boxes[bestIndex];
            boxes[bestIndex] = split.Cut(bestAxis, bestCut, first: true);
            boxes.Add(split.Cut(bestAxis, bestCut, first: false));
        }

        var colors = new RgbaColor[boxes.Count];
        for (var i = 0; i < boxes.Count; i++)
            colors[i] = tables.Average(boxes[i]);

        return new ColorPalette(colors);
    }

    private sealed class Moments
    {
        private readonly long[] _weight = new long[CubeSize];
        private readonly long[] _r = new long[CubeSize];
        private readonly long[] _g = new long[CubeSize];
        private readonly long[] _b = new long[CubeSize];
        private readonly long[] _square = new long[CubeSize];

        public void Accumulate(PixelView source)
        {
            if (source.Format == WorkingFormat.Rgba8)
            {
                // Byte path: the histogram needs 0..255 codes, so a float round trip per row is pure overhead.
                for (var y = 0; y < source.Height; y++)
                {
                    var row = source.Row(y);
                    for (var x = 0; x < source.Width; x++)
                    {
                        var o = x * 4;
                        Add(row[o], row[o + 1], row[o + 2]);
                    }
                }

                return;
            }

            var floatRow = new float[source.Width * 4];
            for (var y = 0; y < source.Height; y++)
            {
                Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, floatRow);
                for (var x = 0; x < source.Width; x++)
                {
                    var o = x * 4;
                    Add(ToCode(floatRow[o]), ToCode(floatRow[o + 1]), ToCode(floatRow[o + 2]));
                }
            }
        }

        private void Add(int actualR, int actualG, int actualB)
        {
            var index = Index((actualR >> 3) + 1, (actualG >> 3) + 1, (actualB >> 3) + 1);

            // Moments carry the actual 0..255 values, not the 5-bit bin coordinates: the bin only decides
            // where a pixel is counted, the average must still land on the true colour.
            _weight[index]++;
            _r[index] += actualR;
            _g[index] += actualG;
            _b[index] += actualB;
            _square[index] += (long)actualR * actualR + (long)actualG * actualG + (long)actualB * actualB;
        }

        public void MakeCumulative()
        {
            for (var r = 1; r < Side; r++)
            {
                for (var g = 1; g < Side; g++)
                {
                    for (var b = 1; b < Side; b++)
                    {
                        var i = Index(r, g, b);
                        var same = Index(r - 1, g, b);
                        var up = Index(r, g - 1, b);
                        var back = Index(r, g, b - 1);
                        var sameUp = Index(r - 1, g - 1, b);
                        var sameBack = Index(r - 1, g, b - 1);
                        var upBack = Index(r, g - 1, b - 1);
                        var corner = Index(r - 1, g - 1, b - 1);

                        _weight[i] += _weight[same] + _weight[up] + _weight[back]
                                      - _weight[sameUp] - _weight[sameBack] - _weight[upBack] + _weight[corner];
                        _r[i] += _r[same] + _r[up] + _r[back] - _r[sameUp] - _r[sameBack] - _r[upBack] + _r[corner];
                        _g[i] += _g[same] + _g[up] + _g[back] - _g[sameUp] - _g[sameBack] - _g[upBack] + _g[corner];
                        _b[i] += _b[same] + _b[up] + _b[back] - _b[sameUp] - _b[sameBack] - _b[upBack] + _b[corner];
                        _square[i] += _square[same] + _square[up] + _square[back]
                                      - _square[sameUp] - _square[sameBack] - _square[upBack] + _square[corner];
                    }
                }
            }
        }

        public long Weight(Cube box) => Volume(_weight, box);

        public double Variance(Cube box)
        {
            var weight = Volume(_weight, box);
            if (weight == 0)
                return 0;

            var r = Volume(_r, box);
            var g = Volume(_g, box);
            var b = Volume(_b, box);
            var square = Volume(_square, box);

            return square - ((double)r * r + (double)g * g + (double)b * b) / weight;
        }

        public RgbaColor Average(Cube box)
        {
            var weight = Volume(_weight, box);
            if (weight == 0)
                return new RgbaColor(0, 0, 0, 255);

            var r = (double)Volume(_r, box) / weight + 0.5;
            var g = (double)Volume(_g, box) / weight + 0.5;
            var b = (double)Volume(_b, box) / weight + 0.5;

            return new RgbaColor(
                (byte)Math.Clamp((int)r, 0, 255),
                (byte)Math.Clamp((int)g, 0, 255),
                (byte)Math.Clamp((int)b, 0, 255),
                255);
        }

        private static long Volume(long[] table, Cube box)
        {
            var r0 = box.R0;
            var g0 = box.G0;
            var b0 = box.B0;
            var r1 = box.R1;
            var g1 = box.G1;
            var b1 = box.B1;

            return table[Index(r1, g1, b1)]
                   - table[Index(r0, g1, b1)] - table[Index(r1, g0, b1)] - table[Index(r1, g1, b0)]
                   + table[Index(r0, g0, b1)] + table[Index(r0, g1, b0)] + table[Index(r1, g0, b0)]
                   - table[Index(r0, g0, b0)];
        }

        private static int ToCode(float value)
        {
            var scaled = (int)(value * 255f + 0.5f);
            return scaled < 0 ? 0 : (scaled > 255 ? 255 : scaled);
        }

        private static int Index(int r, int g, int b) => (r * Side + g) * Side + b;
    }

    private readonly record struct Cube(int R0, int G0, int B0, int R1, int G1, int B1)
    {
        public static Cube Full { get; } = new(0, 0, 0, Side - 1, Side - 1, Side - 1);

        public int Length(int axis) => axis switch
        {
            0 => R1 - R0,
            1 => G1 - G0,
            _ => B1 - B0,
        };

        public int Lower(int axis) => axis switch { 0 => R0, 1 => G0, _ => B0 };
        public int Upper(int axis) => axis switch { 0 => R1, 1 => G1, _ => B1 };

        public long Volume() => (long)Length(0) * Length(1) * Length(2);

        public Cube Cut(int axis, int at, bool first) => axis switch
        {
            0 => first ? this with { R1 = at } : this with { R0 = at },
            1 => first ? this with { G1 = at } : this with { G0 = at },
            _ => first ? this with { B1 = at } : this with { B0 = at },
        };
    }
}
