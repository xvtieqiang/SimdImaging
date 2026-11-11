using System.Buffers.Binary;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// ICC colour transforms for matrix/TRC profiles — the shape every RGB working space takes (sRGB, Display P3,
/// Adobe RGB, ProPhoto). The profile supplies a 3×3 matrix from linear RGB into the D50 connection space and a
/// per-channel tone curve; the transform linearises, multiplies, adapts the white and encodes.
/// <para>
/// Profiles whose colour is described by a lookup table (mAB/mBA, the usual CMYK and scanner profiles) are
/// rejected with <see cref="NotSupportedException"/>: evaluating a CLUT needs the parser to hand over the grid,
/// which is a codec-layer job and is not exposed today.
/// </para>
/// <para>
/// An empty profile means sRGB: the destination defaults to it, so <c>Transform(view, profile)</c> converts a
/// file's colours into sRGB, and <c>Transform(view, default, profile)</c> goes the other way.
/// </para>
/// </summary>
public static class IccTransformOps
{
    private const int TableSize = 1024;

    /// <summary>Converts pixels from <paramref name="sourceProfile"/> into <paramref name="destinationProfile"/>.</summary>
    /// <param name="source">The pixels, encoded as <paramref name="sourceProfile"/> says.</param>
    /// <param name="sourceProfile">The profile the pixels are in; empty means sRGB.</param>
    /// <param name="destinationProfile">The profile to convert to; empty means sRGB.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="InvalidDataException">A profile is malformed or not an RGB matrix/TRC profile.</exception>
    /// <exception cref="NotSupportedException">A profile describes colour with a lookup table.</exception>
    public static WorkingBuffer Transform(PixelView source, ReadOnlySpan<byte> sourceProfile,
        ReadOnlySpan<byte> destinationProfile = default, IPixelPool? pool = null)
    {
        var from = sourceProfile.IsEmpty ? null : Parse(sourceProfile);
        var to = destinationProfile.IsEmpty ? null : Parse(destinationProfile);
        if (from is null && to is null)
            return Geometry.Transforms.Copy(source, pool);

        var width = source.Width;
        var stride = width * 4;
        var destination = WorkingBuffer.Rent(width, source.Height, source.Format, pool);
        var row = new float[stride];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;

                // Source → PCS (D50 XYZ).
                float px, py, pz;
                if (from is null)
                {
                    var (x65, y65, z65) = ColorMath.ToXyz(row[offset], row[offset + 1], row[offset + 2]);
                    (px, py, pz) = ColorMath.Adapt(x65, y65, z65, WhitePoint.D65, WhitePoint.D50);
                }
                else
                {
                    (px, py, pz) = from.ToPcs(row[offset], row[offset + 1], row[offset + 2]);
                }

                // PCS → destination.
                if (to is null)
                {
                    var (x65, y65, z65) = ColorMath.Adapt(px, py, pz, WhitePoint.D50, WhitePoint.D65);
                    var (r, g, b) = ColorMath.FromXyz(x65, y65, z65);
                    row[offset] = r;
                    row[offset + 1] = g;
                    row[offset + 2] = b;
                }
                else
                {
                    var (r, g, b) = to.FromPcs(px, py, pz);
                    row[offset] = r;
                    row[offset + 1] = g;
                    row[offset + 2] = b;
                }
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    private sealed class Profile
    {
        private readonly float[] _matrix;      // linear RGB → PCS XYZ (D50), row major
        private readonly float[] _inverse;     // PCS XYZ (D50) → linear RGB
        private readonly float[][] _curves;    // encoded → linear, three channels

        private Profile(float[] matrix, float[] inverse, float[][] curves)
        {
            _matrix = matrix;
            _inverse = inverse;
            _curves = curves;
        }

        public (float X, float Y, float Z) ToPcs(float r, float g, float b)
        {
            var lr = Lookup(_curves[0], r);
            var lg = Lookup(_curves[1], g);
            var lb = Lookup(_curves[2], b);

            return (
                _matrix[0] * lr + _matrix[1] * lg + _matrix[2] * lb,
                _matrix[3] * lr + _matrix[4] * lg + _matrix[5] * lb,
                _matrix[6] * lr + _matrix[7] * lg + _matrix[8] * lb);
        }

        public (float R, float G, float B) FromPcs(float x, float y, float z)
        {
            var lr = _inverse[0] * x + _inverse[1] * y + _inverse[2] * z;
            var lg = _inverse[3] * x + _inverse[4] * y + _inverse[5] * z;
            var lb = _inverse[6] * x + _inverse[7] * y + _inverse[8] * z;

            return (LookupInverse(_curves[0], lr), LookupInverse(_curves[1], lg), LookupInverse(_curves[2], lb));
        }

        public static Profile Create(float[] matrix, float[][] curves) =>
            new(matrix, Invert(matrix), curves);
    }

    private static Profile Parse(ReadOnlySpan<byte> profile)
    {
        if (profile.Length < 132)
            throw new InvalidDataException("The ICC profile is shorter than its header.");

        var colourSpace = profile.Slice(16, 4);
        if (!colourSpace.SequenceEqual("RGB "u8))
            throw new NotSupportedException(
                $"The ICC profile's data colour space is not RGB; only RGB working spaces are supported.");

        var tagCount = BinaryPrimitives.ReadInt32BigEndian(profile.Slice(128, 4));
        if (tagCount < 0 || 132 + tagCount * 12 > profile.Length)
            throw new InvalidDataException("The ICC profile's tag table does not fit.");

        ReadOnlySpan<byte> rXyz = default, gXyz = default, bXyz = default, rTrc = default, gTrc = default, bTrc = default;

        for (var i = 0; i < tagCount; i++)
        {
            var entry = profile.Slice(132 + i * 12, 12);
            var signature = entry[..4];
            var offset = BinaryPrimitives.ReadInt32BigEndian(entry.Slice(4, 4));
            var size = BinaryPrimitives.ReadInt32BigEndian(entry.Slice(8, 4));

            if (offset < 0 || size < 0 || offset + size > profile.Length)
                throw new InvalidDataException("An ICC tag points outside the profile.");

            var tag = profile.Slice(offset, size);
            if (signature.SequenceEqual("rXYZ"u8)) rXyz = tag;
            else if (signature.SequenceEqual("gXYZ"u8)) gXyz = tag;
            else if (signature.SequenceEqual("bXYZ"u8)) bXyz = tag;
            else if (signature.SequenceEqual("rTRC"u8)) rTrc = tag;
            else if (signature.SequenceEqual("gTRC"u8)) gTrc = tag;
            else if (signature.SequenceEqual("bTRC"u8)) bTrc = tag;
        }

        if (rXyz.IsEmpty || gXyz.IsEmpty || bXyz.IsEmpty || rTrc.IsEmpty || gTrc.IsEmpty || bTrc.IsEmpty)
            throw new NotSupportedException(
                "The ICC profile has no matrix/TRC tags (rXYZ/gXYZ/bXYZ + rTRC/gTRC/bTRC); LUT-based profiles are not supported.");

        // Columns, because the colourants describe where each primary lands.
        var matrix = new[]
        {
            XyzComponent(rXyz, 0), XyzComponent(gXyz, 0), XyzComponent(bXyz, 0),
            XyzComponent(rXyz, 1), XyzComponent(gXyz, 1), XyzComponent(bXyz, 1),
            XyzComponent(rXyz, 2), XyzComponent(gXyz, 2), XyzComponent(bXyz, 2),
        };

        return Profile.Create(matrix, new[] { BuildCurve(rTrc), BuildCurve(gTrc), BuildCurve(bTrc) });
    }

    /// <summary>One component of an <c>XYZType</c> tag, as s15Fixed16.</summary>
    private static float XyzComponent(ReadOnlySpan<byte> tag, int component)
    {
        if (tag.Length < 20 || !tag[..4].SequenceEqual("XYZ "u8))
            throw new InvalidDataException("An ICC colorant tag is not an XYZType.");

        return BinaryPrimitives.ReadInt32BigEndian(tag.Slice(8 + component * 4, 4)) / 65536f;
    }

    /// <summary>Builds encoded → linear from a <c>curv</c> or <c>para</c> tag, sampled into a table.</summary>
    private static float[] BuildCurve(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 12)
            throw new InvalidDataException("An ICC tone curve tag is too short.");

        var table = new float[TableSize + 1];

        if (tag[..4].SequenceEqual("curv"u8))
        {
            var count = BinaryPrimitives.ReadInt32BigEndian(tag.Slice(8, 4));
            if (count < 0)
                throw new InvalidDataException("An ICC curve tag declares a negative count.");

            if (count == 0)
            {
                for (var i = 0; i <= TableSize; i++)
                    table[i] = i / (float)TableSize;
                return table;
            }

            if (count == 1)
            {
                var gamma = BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(12, 2)) / 256f;
                for (var i = 0; i <= TableSize; i++)
                    table[i] = MathF.Pow(i / (float)TableSize, gamma);
                return table;
            }

            if (12 + count * 2 > tag.Length)
                throw new InvalidDataException("An ICC curve table does not fit in its tag.");

            for (var i = 0; i <= TableSize; i++)
            {
                var position = i / (float)TableSize * (count - 1);
                var index = (int)position;
                var next = Math.Min(index + 1, count - 1);
                var fraction = position - index;

                var a = BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(12 + index * 2, 2)) / 65535f;
                var b = BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(12 + next * 2, 2)) / 65535f;
                table[i] = a + (b - a) * fraction;
            }

            return table;
        }

        if (!tag[..4].SequenceEqual("para"u8))
            throw new NotSupportedException("The ICC tone curve tag is neither curv nor para.");

        var type = BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(8, 2));
        Span<float> p = stackalloc float[7];
        var required = type switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
        if (required < 0 || 12 + required * 4 > tag.Length)
            throw new InvalidDataException("An ICC parametric curve is malformed.");

        for (var i = 0; i < required; i++)
            p[i] = BinaryPrimitives.ReadInt32BigEndian(tag.Slice(12 + i * 4, 4)) / 65536f;

        for (var i = 0; i <= TableSize; i++)
        {
            var x = i / (float)TableSize;
            table[i] = type switch
            {
                0 => MathF.Pow(MathF.Max(x, 0f), p[0]),
                1 => MathF.Pow(MathF.Max(p[1] * x + p[2], 0f), p[0]),
                2 => MathF.Pow(MathF.Max(p[1] * x + p[2], 0f), p[0]) + p[3],
                3 => x >= p[4] ? MathF.Pow(MathF.Max(p[1] * x + p[2], 0f), p[0]) : p[3] * x,
                _ => x >= p[4] ? MathF.Pow(MathF.Max(p[1] * x + p[2], 0f), p[0]) + p[5] : p[3] * x + p[6],
            };
        }

        return table;
    }

    private static float Lookup(float[] table, float value)
    {
        var scaled = Math.Clamp(value, 0f, 1f) * TableSize;
        var index = (int)scaled;
        if (index >= TableSize)
            return table[TableSize];

        var fraction = scaled - index;
        return table[index] + (table[index + 1] - table[index]) * fraction;
    }

    /// <summary>Inverts the sampled curve by walking the table; the curve is monotonic in every real profile.</summary>
    private static float LookupInverse(float[] table, float value)
    {
        var target = Math.Clamp(value, 0f, 1f);
        var low = 0;
        var high = TableSize;

        if (target <= table[0])
            return 0f;
        if (target >= table[TableSize])
            return 1f;

        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (table[middle] <= target)
                low = middle;
            else
                high = middle;
        }

        var span = table[high] - table[low];
        var fraction = span > 0f ? (target - table[low]) / span : 0f;
        return (low + fraction) / TableSize;
    }

    /// <summary>Adjugate over determinant; a singular matrix means a malformed profile.</summary>
    private static float[] Invert(float[] m)
    {
        var a = m[0]; var b = m[1]; var c = m[2];
        var d = m[3]; var e = m[4]; var f = m[5];
        var g = m[6]; var h = m[7]; var i = m[8];

        var determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (MathF.Abs(determinant) < 1e-9f)
            throw new InvalidDataException("The ICC profile's colorant matrix is singular.");

        var inverse = 1f / determinant;
        return
        [
            (e * i - f * h) * inverse, (c * h - b * i) * inverse, (b * f - c * e) * inverse,
            (f * g - d * i) * inverse, (a * i - c * g) * inverse, (c * d - a * f) * inverse,
            (d * h - e * g) * inverse, (b * g - a * h) * inverse, (a * e - b * d) * inverse,
        ];
    }
}
