using System.Buffers.Binary;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>Which connection space a profile's tags are written in.</summary>
internal enum PcsKind : byte
{
    Xyz,
    Lab,
}

/// <summary>
/// How a tag encodes its PCS codes as 16-bit integers. The ICC specification has three answers, and
/// which one applies depends on the tag type — mixing them up shifts L* by 0.04 or every a*/b* by half
/// a unit, so the encoding travels with the stage that produced the codes.
/// <para>
/// ICC.1:2010 §6.3.4.2 gives the general 16-bit encoding (L* 100 → 0xFFFF, a*/b* 0 → 0x8080) and its
/// NOTE 3 narrows it: <b>only</b> <c>lut16Type</c> (and namedColor2) use the v2 legacy encoding
/// (L* 100 → 0xFF00, a*/b* 0 → 0x8000). A <c>lut8Type</c> writes the 8-bit encoding, which after the
/// ×257 widening the rest of the pipeline uses gives L* 100 → 0xFF00 but a*/b* 0 → 0x8080.
/// </para>
/// </summary>
internal enum LabEncoding : byte
{
    /// <summary><c>lut16Type</c>: L* = v/65280·100, a*/b* = v/256 − 128.</summary>
    Legacy16,

    /// <summary><c>lut8Type</c>, widened: L* = v/65280·100, a*/b* = v/257 − 128.</summary>
    Legacy8,

    /// <summary><c>mAB</c>/<c>mBA</c>: L* = v/65535·100, a*/b* = v/257 − 128.</summary>
    General16,
}

/// <summary>
/// An ICC profile reduced to a pair of connection stages:
/// <c>device → XYZ (D50)</c> and <c>XYZ (D50) → device</c>.
/// <para>
/// Two models are supported and they are chosen per direction: a profile whose colour is described by
/// a lookup table (<c>A2B0</c>/<c>B2A0</c> holding <c>mft1</c>, <c>mft2</c>, <c>mAB</c> or <c>mBA</c>)
/// uses that table; a profile with colorant and tone-curve tags uses the 3×3 matrix + 1-D curve path.
/// A LUT tag wins over the matrix when both are present, which is the CMM's rule: the table is what
/// the profile's author actually measured.
/// </para>
/// <para>
/// The parser lives here, in <c>SimdImaging.Processing</c>, not in the codec layer: the codec package
/// hands over the profile bytes and does not expose the grid. See <c>docs/icc-boundary-decision.md</c>
/// for why, for the numeric contract, and for the conditions that would move the boundary.
/// </para>
/// </summary>
internal sealed class IccProfile
{
    private readonly MatrixTrc? _trc;
    private readonly LutStage? _toPcs;
    private readonly LutStage? _fromPcs;
    private readonly PcsKind _pcs;

    private IccProfile(MatrixTrc? trc, LutStage? toPcs, LutStage? fromPcs, PcsKind pcs)
    {
        _trc = trc;
        _toPcs = toPcs;
        _fromPcs = fromPcs;
        _pcs = pcs;
    }

    /// <summary>Device (RGB) → PCS XYZ, D50.</summary>
    /// <exception cref="NotSupportedException">The profile has no device → PCS direction.</exception>
    public (float X, float Y, float Z) ToXyz(float r, float g, float b)
    {
        if (_toPcs is { } stage)
        {
            stage.Evaluate(ToCode(r), ToCode(g), ToCode(b), out var cr, out var cg, out var cb);
            return DecodePcs(cr, cg, cb, stage.Encoding);
        }

        if (_trc is { } trc)
            return trc.ToPcs(r, g, b);

        throw new NotSupportedException(
            "The ICC profile has no device-to-PCS direction: it carries neither an A2B0 tag nor the " +
            "rXYZ/gXYZ/bXYZ + rTRC/gTRC/bTRC tags that make one.");
    }

    /// <summary>PCS XYZ, D50 → device (RGB).</summary>
    /// <exception cref="NotSupportedException">The profile has no PCS → device direction.</exception>
    public (float R, float G, float B) FromXyz(float x, float y, float z)
    {
        if (_fromPcs is { } stage)
        {
            var (cr, cg, cb) = EncodePcs(x, y, z, stage.Encoding);
            stage.Evaluate(cr, cg, cb, out var dr, out var dg, out var db);
            return (dr / 65535f, dg / 65535f, db / 65535f);
        }

        if (_trc is { } trc)
            return trc.FromPcs(x, y, z);

        throw new NotSupportedException(
            "The ICC profile has no PCS-to-device direction: it carries neither a B2A0 tag nor the " +
            "rXYZ/gXYZ/bXYZ + rTRC/gTRC/bTRC tags that make one. A scanner profile with only A2B0 can " +
            "convert into the PCS but not back out of it.");
    }

    private (float X, float Y, float Z) DecodePcs(ushort r, ushort g, ushort b, LabEncoding encoding)
    {
        if (_pcs == PcsKind.Xyz)
        {
            // u1Fixed15: 0x8000 is 1.0 and 0xFFFF is 1.999969… (ICC.1:2010 Table 11).
            return ((float)(r / 32768.0), (float)(g / 32768.0), (float)(b / 32768.0));
        }

        var (l, a, beta) = DecodeLab(r, g, b, encoding);
        return ColorMath.LabToXyz(l, a, beta, WhitePoint.D50);
    }

    private (ushort R, ushort G, ushort B) EncodePcs(float x, float y, float z, LabEncoding encoding)
    {
        if (_pcs == PcsKind.Xyz)
        {
            return (EncodeU16(x * 32768.0), EncodeU16(y * 32768.0), EncodeU16(z * 32768.0));
        }

        var (l, a, b) = ColorMath.XyzToLab(x, y, z, WhitePoint.D50);
        return (EncodeU16(ToLCode(l, encoding)), EncodeU16(ToAbCode(a, encoding)), EncodeU16(ToAbCode(b, encoding)));
    }

    private static (float L, float A, float B) DecodeLab(ushort r, ushort g, ushort b, LabEncoding encoding)
    {
        var lScale = encoding == LabEncoding.General16 ? 100.0 / 65535.0 : 100.0 / 65280.0;
        var abScale = encoding == LabEncoding.Legacy16 ? 1.0 / 256.0 : 1.0 / 257.0;

        return (
            (float)(r * lScale),
            (float)(g * abScale - 128.0),
            (float)(b * abScale - 128.0));
    }

    private static double ToLCode(float value, LabEncoding encoding) =>
        encoding == LabEncoding.General16 ? (double)value * (65535.0 / 100.0) : (double)value * (65280.0 / 100.0);

    private static double ToAbCode(float value, LabEncoding encoding) =>
        (value + 128.0) * (encoding == LabEncoding.Legacy16 ? 256.0 : 257.0);

    private static ushort EncodeU16(double value) =>
        (ushort)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 65535);

    private static ushort ToCode(float value) =>
        (ushort)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 65535f, MidpointRounding.AwayFromZero), 0, 65535);

    // ---------------------------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Reads a profile's header, tag table and whichever colour model it carries.</summary>
    /// <exception cref="InvalidDataException">The profile is truncated or a tag points outside it.</exception>
    /// <exception cref="NotSupportedException">The profile is not an RGB profile this library evaluates.</exception>
    public static IccProfile Parse(ReadOnlySpan<byte> profile)
    {
        if (profile.Length < 132)
            throw new InvalidDataException("The ICC profile is shorter than its header.");

        // Order matters: the colour space is reported before the signature, so a zero-filled buffer is
        // "not an RGB profile" rather than "not a profile".
        var colourSpace = profile.Slice(16, 4);
        if (!colourSpace.SequenceEqual("RGB "u8))
            throw new NotSupportedException(
                "The ICC profile's data colour space is not RGB; only RGB working spaces are supported.");

        var pcsSignature = profile.Slice(20, 4);
        PcsKind pcs;
        if (pcsSignature.SequenceEqual("XYZ "u8))
            pcs = PcsKind.Xyz;
        else if (pcsSignature.SequenceEqual("Lab "u8))
            pcs = PcsKind.Lab;
        else
            throw new NotSupportedException(
                "The ICC profile's connection space is neither XYZ nor Lab.");

        var tagCount = BinaryPrimitives.ReadInt32BigEndian(profile.Slice(128, 4));
        if (tagCount < 0 || 132L + (long)tagCount * 12 > profile.Length)
            throw new InvalidDataException("The ICC profile's tag table does not fit.");

        ReadOnlySpan<byte> rXyz = default, gXyz = default, bXyz = default;
        ReadOnlySpan<byte> rTrc = default, gTrc = default, bTrc = default;
        ReadOnlySpan<byte> a2b0 = default, b2a0 = default;

        for (var i = 0; i < tagCount; i++)
        {
            var entry = profile.Slice(132 + i * 12, 12);
            var signature = entry[..4];
            var offset = BinaryPrimitives.ReadInt32BigEndian(entry.Slice(4, 4));
            var size = BinaryPrimitives.ReadInt32BigEndian(entry.Slice(8, 4));

            if (offset < 0 || size < 0 || (long)offset + size > profile.Length)
                throw new InvalidDataException("An ICC tag points outside the profile.");

            var tag = profile.Slice(offset, size);
            if (signature.SequenceEqual("rXYZ"u8)) rXyz = tag;
            else if (signature.SequenceEqual("gXYZ"u8)) gXyz = tag;
            else if (signature.SequenceEqual("bXYZ"u8)) bXyz = tag;
            else if (signature.SequenceEqual("rTRC"u8)) rTrc = tag;
            else if (signature.SequenceEqual("gTRC"u8)) gTrc = tag;
            else if (signature.SequenceEqual("bTRC"u8)) bTrc = tag;
            else if (signature.SequenceEqual("A2B0"u8)) a2b0 = tag;
            else if (signature.SequenceEqual("B2A0"u8)) b2a0 = tag;
        }

        var toPcs = a2b0.IsEmpty ? null : ParseStage(a2b0, toPcs: true, pcs);
        var fromPcs = b2a0.IsEmpty ? null : ParseStage(b2a0, toPcs: false, pcs);

        MatrixTrc? trc = null;
        if (!rXyz.IsEmpty && !gXyz.IsEmpty && !bXyz.IsEmpty && !rTrc.IsEmpty && !gTrc.IsEmpty && !bTrc.IsEmpty)
            trc = MatrixTrc.Create(rXyz, gXyz, bXyz, rTrc, gTrc, bTrc);

        if (toPcs is null && fromPcs is null && trc is null)
        {
            throw new NotSupportedException(
                "The ICC profile has neither matrix/TRC tags (rXYZ/gXYZ/bXYZ + rTRC/gTRC/bTRC) nor LUT " +
                "tags (A2B0/B2A0); there is no colour transform to apply.");
        }

        return new IccProfile(trc, toPcs, fromPcs, pcs);
    }

    private static LutStage ParseStage(ReadOnlySpan<byte> tag, bool toPcs, PcsKind pcs)
    {
        if (tag.Length < 4)
            throw new InvalidDataException("An ICC LUT tag is shorter than its signature.");

        var type = tag[..4];
        if (type.SequenceEqual("mft2"u8))
            return ParseLut16(tag, toPcs);

        if (type.SequenceEqual("mft1"u8))
        {
            if (pcs == PcsKind.Xyz)
                throw new NotSupportedException(
                    "The ICC profile uses an 8-bit LUT (lut8Type) with a PCSXYZ connection space, which " +
                    "the specification leaves undefined. Use a 16-bit LUT or a Lab PCS.");

            return ParseLut8(tag, toPcs);
        }

        if (type.SequenceEqual("mAB "u8) || type.SequenceEqual("mBA "u8))
        {
            // Both types share a layout; the signature says which end of the chain the B curves sit on,
            // and that is exactly the direction flag.
            var isMab = type.SequenceEqual("mAB "u8);
            if (isMab != toPcs)
            {
                throw new NotSupportedException(
                    isMab
                        ? "The ICC profile's B2A0 tag is an mAB type; a B2A direction needs mBA."
                        : "The ICC profile's A2B0 tag is an mBA type; an A2B direction needs mAB.");
            }

            return ParseMab(tag, toPcs);
        }

        throw new NotSupportedException(
            $"The ICC profile's LUT tag type '{System.Text.Encoding.ASCII.GetString(type)}' is not lut8, lut16, mAB or mBA.");
    }

    private static LutStage ParseLut16(ReadOnlySpan<byte> tag, bool toPcs)
    {
        const int header = 48;
        if (tag.Length < header)
            throw new InvalidDataException("An ICC lut16 tag is shorter than its header.");

        RequireThreeChannels(tag[8], tag[9], "lut16");
        var grid = tag[10];
        RequireGrid(grid, "lut16");

        // The 3×3 matrix of a lut is only consulted when the input side is PCSXYZ, i.e. in the B2A
        // direction. Applying one here would be a guess, so a non-identity matrix in that position is
        // refused instead of ignored.
        if (!toPcs && !IsIdentity(tag.Slice(12, 36)))
        {
            throw new NotSupportedException(
                "The ICC profile's B2A0 lut16 tag carries a non-identity matrix. That form is legal but " +
                "has no reference corpus here; it is refused rather than approximated.");
        }

        var offset = header;
        var a = ReadTables(tag, ref offset, 3, 256, twoBytes: true);
        var clut = ReadClut(tag, ref offset, grid, 3);
        var b = ReadTables(tag, ref offset, 3, 256, twoBytes: true);

        return new LutStage(a, clut, null, null, b, LabEncoding.Legacy16, toPcs);
    }

    private static LutStage ParseLut8(ReadOnlySpan<byte> tag, bool toPcs)
    {
        const int header = 48;
        if (tag.Length < header)
            throw new InvalidDataException("An ICC lut8 tag is shorter than its header.");

        RequireThreeChannels(tag[8], tag[9], "lut8");
        var grid = tag[10];
        RequireGrid(grid, "lut8");

        if (!toPcs && !IsIdentity(tag.Slice(12, 36)))
        {
            throw new NotSupportedException(
                "The ICC profile's B2A0 lut8 tag carries a non-identity matrix; it is refused rather than approximated.");
        }

        var offset = header;
        var a = ReadTables(tag, ref offset, 3, 256, twoBytes: false);
        var clut = ReadClut(tag, ref offset, grid, 3);
        var b = ReadTables(tag, ref offset, 3, 256, twoBytes: false);

        return new LutStage(a, clut, null, null, b, LabEncoding.Legacy8, toPcs);
    }

    /// <summary>
    /// <c>mAB </c>/<c>mBA </c>: five offsets into the tag, a fixed 3×3 matrix, and a chain of
    /// A curves → CLUT → M curves → matrix → B curves (reversed for mBA).
    /// </summary>
    private static LutStage ParseMab(ReadOnlySpan<byte> tag, bool toPcs)
    {
        const int header = 68;   // 32 bytes of fields + the 36-byte matrix
        if (tag.Length < header)
            throw new InvalidDataException("An ICC mAB/mBA tag is shorter than its header.");

        RequireThreeChannels(tag[8], tag[9], "mAB/mBA");

        var bOffset = ReadOffset(tag, 12);
        var matrixOffset = ReadOffset(tag, 16);
        var mOffset = ReadOffset(tag, 20);
        var clutOffset = ReadOffset(tag, 24);
        var aOffset = ReadOffset(tag, 28);

        int[] aCurves = ParseCurveArray(tag, aOffset);
        int[] mCurves = ParseCurveArray(tag, mOffset);
        int[] bCurves = ParseCurveArray(tag, bOffset);

        var a = ReadCurves(tag, aCurves);
        var m = ReadCurves(tag, mCurves);
        var b = ReadCurves(tag, bCurves);

        IccClut3D? clut = null;
        if (clutOffset != 0)
        {
            // The mAB CLUT is introduced by two bytes of its own: grid points per axis, then the
            // precision of the samples (1 = 8-bit, 2 = 16-bit); the table follows.
            if (clutOffset + 2 > tag.Length)
                throw new InvalidDataException("An ICC mAB CLUT offset points outside the tag.");

            var grid = tag[clutOffset];
            var precision = tag[clutOffset + 1];
            if (precision is not (1 or 2))
                throw new InvalidDataException($"An ICC mAB CLUT declares a precision of {precision}; only 1 or 2 is defined.");

            var data = clutOffset + 2;
            clut = ReadClut(tag, ref data, grid, 3, twoBytes: precision == 2);
        }

        double[]? matrix = null;
        if (matrixOffset != 0)
        {
            var values = ReadMatrix(tag.Slice(32, 36));
            if (!IsIdentity(values))
                matrix = values;
        }

        return new LutStage(a, clut, m, matrix, b, LabEncoding.General16, toPcs);
    }

    private static void RequireThreeChannels(byte input, byte output, string what)
    {
        if (input != 3 || output != 3)
            throw new NotSupportedException(
                $"The ICC profile's {what} tag declares {input} input and {output} output channels; only " +
                "three-in, three-out RGB profiles are supported.");
    }

    private static void RequireGrid(byte grid, string what)
    {
        if (grid < 2)
            throw new InvalidDataException($"The ICC profile's {what} tag declares a {grid}-point CLUT grid; at least 2 is required.");
    }

    private static Curve1D[] ReadTables(ReadOnlySpan<byte> tag, ref int offset, int channels, int entries, bool twoBytes)
    {
        var curves = new Curve1D[channels];
        var width = twoBytes ? 2 : 1;
        var bytes = entries * width;

        for (var channel = 0; channel < channels; channel++)
        {
            if (offset < 0 || (long)offset + bytes > tag.Length)
                throw new InvalidDataException("An ICC LUT's tone table runs past the end of its tag.");

            var table = new ushort[entries];
            for (var i = 0; i < entries; i++)
            {
                table[i] = twoBytes
                    ? BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(offset + i * 2, 2))
                    : (ushort)(tag[offset + i] * 257);
            }

            curves[channel] = Curve1D.FromTable(table);
            offset += bytes;
        }

        return curves;
    }

    private static IccClut3D ReadClut(ReadOnlySpan<byte> tag, ref int offset, int grid, int channels)
        => ReadClut(tag, ref offset, grid, channels, twoBytes: true);

    private static IccClut3D ReadClut(ReadOnlySpan<byte> tag, ref int offset, int grid, int channels, bool twoBytes)
    {
        var width = twoBytes ? 2 : 1;
        var count = checked((long)grid * grid * grid * channels);
        var bytes = count * width;

        if (offset < 0 || (long)offset + bytes > tag.Length)
            throw new InvalidDataException("An ICC LUT's CLUT runs past the end of its tag.");

        var samples = new ushort[count];
        for (long i = 0; i < count; i++)
        {
            samples[i] = twoBytes
                ? BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(offset + (int)i * 2, 2))
                : (ushort)(tag[offset + (int)i] * 257);
        }

        offset += (int)bytes;
        return new IccClut3D(samples, grid);
    }

    private static int ReadOffset(ReadOnlySpan<byte> tag, int position)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(tag.Slice(position, 4));
        if (value < 0 || value > tag.Length)
            throw new InvalidDataException("An ICC mAB/mBA offset points outside its tag.");

        return value;
    }

    /// <summary>An offset array holds one offset per channel; a zero entry means "identity".</summary>
    private static int[] ParseCurveArray(ReadOnlySpan<byte> tag, int offset)
    {
        if (offset == 0)
            return [];

        if ((long)offset + 12 > tag.Length)
            throw new InvalidDataException("An ICC mAB curve offset array runs past the end of its tag.");

        var offsets = new int[3];
        for (var i = 0; i < 3; i++)
        {
            offsets[i] = BinaryPrimitives.ReadInt32BigEndian(tag.Slice(offset + i * 4, 4));
            if (offsets[i] < 0 || offsets[i] > tag.Length)
                throw new InvalidDataException("An ICC mAB curve offset points outside its tag.");
        }

        return offsets;
    }

    private static Curve1D[] ReadCurves(ReadOnlySpan<byte> tag, int[] offsets)
    {
        if (offsets.Length == 0)
            return [Curve1D.Identity, Curve1D.Identity, Curve1D.Identity];

        var curves = new Curve1D[3];
        for (var i = 0; i < 3; i++)
            curves[i] = offsets[i] == 0 ? Curve1D.Identity : ParseCurve(tag[offsets[i]..]);

        return curves;
    }

    /// <summary>A <c>curv</c> or <c>para</c> tag as a 16-bit table.</summary>
    private static Curve1D ParseCurve(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 12)
            throw new InvalidDataException("An ICC curve tag is too short.");

        if (tag[..4].SequenceEqual("curv"u8))
        {
            var count = BinaryPrimitives.ReadInt32BigEndian(tag.Slice(8, 4));
            if (count < 0)
                throw new InvalidDataException("An ICC curve tag declares a negative count.");

            if (count == 0)
                return Curve1D.Identity;

            if (count == 1)
            {
                var gamma = BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(12, 2)) / 256.0;
                var table = new ushort[Samples];
                for (var i = 0; i < Samples; i++)
                {
                    var x = i / (double)(Samples - 1);
                    table[i] = EncodeU16(Math.Pow(x, gamma) * 65535.0);
                }

                return Curve1D.FromTable(table);
            }

            if (12L + (long)count * 2 > tag.Length)
                throw new InvalidDataException("An ICC curve table does not fit in its tag.");

            var entries = new ushort[count];
            for (var i = 0; i < count; i++)
                entries[i] = BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(12 + i * 2, 2));

            return Curve1D.FromTable(entries);
        }

        if (!tag[..4].SequenceEqual("para"u8))
            throw new NotSupportedException("The ICC curve tag is neither curv nor para.");

        var type = BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(8, 2));
        Span<double> p = stackalloc double[7];
        var required = type switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
        if (required < 0 || 12 + required * 4 > tag.Length)
            throw new InvalidDataException("An ICC parametric curve is malformed.");

        for (var i = 0; i < required; i++)
            p[i] = BinaryPrimitives.ReadInt32BigEndian(tag.Slice(12 + i * 4, 4)) / 65536.0;

        var sampled = new ushort[Samples];
        for (var i = 0; i < Samples; i++)
        {
            var x = i / (double)(Samples - 1);
            var y = type switch
            {
                0 => Math.Pow(Math.Max(x, 0.0), p[0]),
                1 => Math.Pow(Math.Max(p[1] * x + p[2], 0.0), p[0]),
                2 => Math.Pow(Math.Max(p[1] * x + p[2], 0.0), p[0]) + p[3],
                3 => x >= p[4] ? Math.Pow(Math.Max(p[1] * x + p[2], 0.0), p[0]) : p[3] * x,
                _ => x >= p[4] ? Math.Pow(Math.Max(p[1] * x + p[2], 0.0), p[0]) + p[5] : p[3] * x + p[6],
            };

            sampled[i] = EncodeU16(y * 65535.0);
        }

        return Curve1D.FromTable(sampled);
    }

    /// <summary>Samples a parametric curve at this many points. 4096 keeps the table 8 KiB per curve.</summary>
    private const int Samples = 4096;

    private static double[] ReadMatrix(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 36)
            throw new InvalidDataException("An ICC matrix is shorter than nine s15Fixed16 values.");

        var matrix = new double[9];
        for (var i = 0; i < 9; i++)
            matrix[i] = BinaryPrimitives.ReadInt32BigEndian(tag.Slice(i * 4, 4)) / 65536.0;

        return matrix;
    }

    private static bool IsIdentity(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 36)
            return false;

        for (var i = 0; i < 9; i++)
        {
            var value = BinaryPrimitives.ReadInt32BigEndian(tag.Slice(i * 4, 4));
            var expected = i % 4 == 0 ? 65536 : 0;
            if (value != expected)
                return false;
        }

        return true;
    }

    private static bool IsIdentity(double[] matrix)
    {
        for (var i = 0; i < 9; i++)
        {
            var expected = i % 4 == 0 ? 1.0 : 0.0;
            if (Math.Abs(matrix[i] - expected) > 1e-12)
                return false;
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------------------
    // Stages
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// One chain of LUT components, tagged with the direction it runs in. The components are stored in
    /// tag order (A curves, CLUT, M curves, matrix, B curves) and the direction decides which end the
    /// codes enter: A→CLUT→M→matrix→B for A2B, B→matrix→M→CLUT→A for B2A.
    /// </summary>
    private sealed class LutStage
    {
        private readonly Curve1D[] _a;
        private readonly IccClut3D? _clut;
        private readonly Curve1D[] _m;
        private readonly double[]? _matrix;
        private readonly Curve1D[] _b;
        private readonly bool _toPcs;

        public LutStage(Curve1D[] a, IccClut3D? clut, Curve1D[]? m, double[]? matrix, Curve1D[] b,
            LabEncoding encoding, bool toPcs)
        {
            _a = a;
            _clut = clut;
            _m = m ?? Identity3;
            _matrix = matrix;
            _b = b;
            _toPcs = toPcs;
            Encoding = encoding;
        }

        private static readonly Curve1D[] Identity3 = [Curve1D.Identity, Curve1D.Identity, Curve1D.Identity];

        public LabEncoding Encoding { get; }

        public void Evaluate(ushort inputR, ushort inputG, ushort inputB,
            out ushort outputR, out ushort outputG, out ushort outputB)
        {
            if (_toPcs)
            {
                var r = _a[0].Map(inputR);
                var g = _a[1].Map(inputG);
                var b = _a[2].Map(inputB);

                if (_clut is not null)
                    _clut.Evaluate(r, g, b, out r, out g, out b);

                r = _m[0].Map(r);
                g = _m[1].Map(g);
                b = _m[2].Map(b);

                if (_matrix is not null)
                    ApplyMatrix(ref r, ref g, ref b);

                outputR = _b[0].Map(r);
                outputG = _b[1].Map(g);
                outputB = _b[2].Map(b);
                return;
            }

            var br = _b[0].Map(inputR);
            var bg = _b[1].Map(inputG);
            var bb = _b[2].Map(inputB);

            if (_matrix is not null)
                ApplyMatrix(ref br, ref bg, ref bb);

            br = _m[0].Map(br);
            bg = _m[1].Map(bg);
            bb = _m[2].Map(bb);

            if (_clut is not null)
                _clut.Evaluate(br, bg, bb, out br, out bg, out bb);

            outputR = _a[0].Map(br);
            outputG = _a[1].Map(bg);
            outputB = _a[2].Map(bb);
        }

        /// <summary>
        /// The <c>mAB</c> matrix stage. It is a real matrix in s15Fixed16, so it runs in double — this
        /// stage is outside the D0 claim the integer CLUT makes.
        /// </summary>
        private void ApplyMatrix(ref ushort r, ref ushort g, ref ushort b)
        {
            var v0 = r / 65535.0;
            var v1 = g / 65535.0;
            var v2 = b / 65535.0;

            var m = _matrix!;
            r = EncodeU16((m[0] * v0 + m[1] * v1 + m[2] * v2) * 65535.0);
            g = EncodeU16((m[3] * v0 + m[4] * v1 + m[5] * v2) * 65535.0);
            b = EncodeU16((m[6] * v0 + m[7] * v1 + m[8] * v2) * 65535.0);
        }
    }

    /// <summary>
    /// The matrix/TRC model: three colorants, a 3×3 matrix into D50 XYZ, and one tone curve per
    /// channel sampled into a 1025-entry float table. Unchanged from the pre-LUT implementation.
    /// </summary>
    private sealed class MatrixTrc
    {
        private const int TableSize = 1024;

        private readonly float[] _matrix;
        private readonly float[] _inverse;
        private readonly float[][] _curves;

        private MatrixTrc(float[] matrix, float[] inverse, float[][] curves)
        {
            _matrix = matrix;
            _inverse = inverse;
            _curves = curves;
        }

        public static MatrixTrc Create(ReadOnlySpan<byte> rXyz, ReadOnlySpan<byte> gXyz, ReadOnlySpan<byte> bXyz,
            ReadOnlySpan<byte> rTrc, ReadOnlySpan<byte> gTrc, ReadOnlySpan<byte> bTrc)
        {
            // Columns, because the colourants describe where each primary lands.
            var matrix = new[]
            {
                XyzComponent(rXyz, 0), XyzComponent(gXyz, 0), XyzComponent(bXyz, 0),
                XyzComponent(rXyz, 1), XyzComponent(gXyz, 1), XyzComponent(bXyz, 1),
                XyzComponent(rXyz, 2), XyzComponent(gXyz, 2), XyzComponent(bXyz, 2),
            };

            return new MatrixTrc(matrix, Invert(matrix), [BuildCurve(rTrc), BuildCurve(gTrc), BuildCurve(bTrc)]);
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
}
