using System.Buffers.Binary;

namespace SimdImaging.Tests;

/// <summary>
/// Hand-built ICC profiles, written byte by byte against ICC.1:2010 rather than by the parser they are
/// fed to. Two jobs: they make the PCS encodings testable at their exact endpoints, and they are the
/// only independent witness the LUT path has — no redistributable scanner or printer profile exists in
/// this repository (see <c>docs/known-gaps.md</c>).
/// <para>
/// Nothing here is derived from the production code. The numbers a test asserts are either exact by
/// construction (a constant CLUT decodes to the code that was written into it) or computed in the test
/// from <c>ColorMath</c>, which is a different path.
/// </para>
/// </summary>
internal static class SyntheticIccProfiles
{
    private const int HeaderSize = 128;

    /// <summary>A profile with one <c>A2B0</c> LUT that maps device RGB linearly onto PCS XYZ.</summary>
    public static byte[] Lut16XyzIdentity() =>
        Profile("RGB ", "XYZ ", 0x02300000,
            ("A2B0", Lut16(grid: 2, XyzIdentityClut(2), IdentityTable(), IdentityTable(), matrix: null)));

    /// <summary>A profile whose <c>A2B0</c> LUT answers every input with one PCS Lab code.</summary>
    public static byte[] Lut16LabConstant(ushort l, ushort a, ushort b) =>
        Profile("RGB ", "Lab ", 0x02300000,
            ("A2B0", Lut16(grid: 2, ConstantClut(2, l, a, b), IdentityTable(), IdentityTable(), matrix: null)));

    /// <summary>A <c>lut8Type</c> profile, which the specification only defines for a Lab PCS.</summary>
    public static byte[] Lut8LabConstant(byte l, byte a, byte b) =>
        Profile("RGB ", "Lab ", 0x02300000,
            ("A2B0", Lut8(grid: 2, ConstantClut8(2, l, a, b))));

    /// <summary>A <c>lut8Type</c> profile with a PCSXYZ connection space — undefined, must be refused.</summary>
    public static byte[] Lut8WithXyzPcs() =>
        Profile("RGB ", "XYZ ", 0x02300000,
            ("A2B0", Lut8(grid: 2, ConstantClut8(2, 0, 0, 0))));

    /// <summary>An <c>mAB</c> profile with identity curves and a constant CLUT.</summary>
    public static byte[] MabLabConstant(ushort l, ushort a, ushort b) =>
        Profile("RGB ", "Lab ", 0x04300000,
            ("A2B0", Mab(toPcs: true, grid: 2, ConstantClut(2, l, a, b), aCurves: null, mCurves: null,
                bCurves: null, matrix: null)));

    /// <summary>
    /// <c>mAB</c> with every stage doing something measurable: the A curve halves the code, the CLUT
    /// passes it through, the matrix doubles X, and the B curve is an identity <c>curv</c> table.
    /// </summary>
    public static byte[] MabWithEveryStage() =>
        Profile("RGB ", "XYZ ", 0x04300000,
            ("A2B0", Mab(toPcs: true, grid: 2, XyzIdentityClut(2),
                aCurves: [QuarterCurve(), QuarterCurve(), QuarterCurve()],
                mCurves: null,
                bCurves: [IdentityCurve(), IdentityCurve(), IdentityCurve()],
                matrix: [2.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0])));

    /// <summary>An <c>mBA</c> profile: the PCS → device direction only.</summary>
    public static byte[] MbaLabConstant(ushort l, ushort a, ushort b) =>
        Profile("RGB ", "Lab ", 0x04300000,
            ("B2A0", Mab(toPcs: false, grid: 2, ConstantClut(2, l, a, b), aCurves: null, mCurves: null,
                bCurves: null, matrix: null)));

    /// <summary>A device colour space this library refuses.</summary>
    public static byte[] Cmyk() =>
        Profile("CMYK", "Lab ", 0x02300000,
            ("A2B0", Lut16(grid: 2, ConstantClut(2, 0, 0, 0), IdentityTable(), IdentityTable(), matrix: null)));

    /// <summary>A profile that carries only Intent 1 tables, which no CMM default path can use.</summary>
    public static byte[] OnlyIntentOne() =>
        Profile("RGB ", "XYZ ", 0x02300000,
            ("A2B1", Lut16(grid: 2, XyzIdentityClut(2), IdentityTable(), IdentityTable(), matrix: null)));

    /// <summary>A profile whose only information is an unusable 4-byte LUT tag.</summary>
    public static byte[] UnknownTagType() =>
        Profile("RGB ", "XYZ ", 0x02300000, ("A2B0", "zzzz"u8.ToArray()));

    /// <summary>A tag whose declared size runs past the end of the profile.</summary>
    public static byte[] TagOutsideProfile()
    {
        var profile = Lut16XyzIdentity();
        var offset = BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(136, 4));
        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(136, 4), offset + profile.Length);
        return profile;
    }

    // ---------------------------------------------------------------------------------------------------
    // Tags
    // ---------------------------------------------------------------------------------------------------

    private static byte[] Lut16(byte grid, ushort[] clut, ushort[] inputTables, ushort[] outputTables, double[]? matrix)
    {
        var size = 48 + 3 * 256 * 2 + clut.Length * 2 + 3 * 256 * 2;
        var tag = new byte[size];
        "mft2"u8.CopyTo(tag);
        tag[8] = 3;
        tag[9] = 3;
        tag[10] = grid;
        WriteMatrix(tag.AsSpan(12, 36), matrix);

        var offset = 48;
        offset = WriteTable(tag, offset, inputTables);
        offset = WriteTable(tag, offset, clut);
        WriteTable(tag, offset, outputTables);
        return tag;
    }

    private static byte[] Lut8(byte grid, ushort[] clut)
    {
        var size = 48 + 3 * 256 + clut.Length + 3 * 256;
        var tag = new byte[size];
        "mft1"u8.CopyTo(tag);
        tag[8] = 3;
        tag[9] = 3;
        tag[10] = grid;
        WriteMatrix(tag.AsSpan(12, 36), null);

        var offset = 48;
        for (var i = 0; i < 3; i++)
            for (var entry = 0; entry < 256; entry++)
                tag[offset++] = (byte)entry;

        for (var i = 0; i < clut.Length; i++)
            tag[offset++] = (byte)(clut[i] / 257);

        for (var i = 0; i < 3; i++)
            for (var entry = 0; entry < 256; entry++)
                tag[offset++] = (byte)entry;

        return tag;
    }

    /// <summary>
    /// <c>mAB </c>/<c>mBA </c>: a fixed header with five offsets, the matrix at 32, the three offset
    /// arrays at 68/80/92, then the curve tags and finally the CLUT.
    /// </summary>
    private static byte[] Mab(bool toPcs, byte grid, ushort[] clut, byte[]?[]? aCurves, byte[]?[]? mCurves,
        byte[]?[]? bCurves, double[]? matrix)
    {
        const int header = 68;
        var aArray = header;
        var mArray = header + 12;
        var bArray = header + 24;
        var cursor = header + 36;

        var aOffsets = new int[3];
        var mOffsets = new int[3];
        var bOffsets = new int[3];

        var curveBytes = new List<byte>();
        void Place(byte[]?[]? curves, int[] offsets)
        {
            if (curves is null)
                return;

            for (var i = 0; i < 3; i++)
            {
                if (curves[i] is null)
                    continue;

                while ((cursor + curveBytes.Count) % 4 != 0)
                    curveBytes.Add(0);

                offsets[i] = cursor + curveBytes.Count;
                curveBytes.AddRange(curves[i]!);
            }
        }

        Place(aCurves, aOffsets);
        Place(mCurves, mOffsets);
        Place(bCurves, bOffsets);

        while ((cursor + curveBytes.Count) % 4 != 0)
            curveBytes.Add(0);

        var clutOffset = cursor + curveBytes.Count;

        var tag = new byte[clutOffset + 2 + clut.Length * 2];
        (toPcs ? "mAB "u8 : "mBA "u8).CopyTo(tag);
        tag[8] = 3;
        tag[9] = 3;

        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(12, 4), bArray);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(16, 4), matrix is null ? 0 : 32);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(20, 4), mArray);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(24, 4), clutOffset);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(28, 4), aArray);

        WriteMatrix(tag.AsSpan(32, 36), matrix);

        for (var i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(aArray + i * 4, 4), aOffsets[i]);
            BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(mArray + i * 4, 4), mOffsets[i]);
            BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(bArray + i * 4, 4), bOffsets[i]);
        }

        curveBytes.CopyTo(tag, cursor);
        tag[clutOffset] = grid;
        tag[clutOffset + 1] = 2;

        var write = clutOffset + 2;
        for (var i = 0; i < clut.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(write + i * 2, 2), clut[i]);
        }

        return tag;
    }

    /// <summary>A <c>curv</c> tag with two entries: 0 → 0 and 65535 → 16384 (a quarter).</summary>
    private static byte[] QuarterCurve()
    {
        var tag = new byte[16];
        "curv"u8.CopyTo(tag);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(12, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(14, 2), 16384);
        return tag;
    }

    /// <summary>A <c>curv</c> tag with the identity table.</summary>
    private static byte[] IdentityCurve()
    {
        var tag = new byte[16];
        "curv"u8.CopyTo(tag);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(12, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(14, 2), 65535);
        return tag;
    }

    // ---------------------------------------------------------------------------------------------------
    // Tables
    // ---------------------------------------------------------------------------------------------------

    private static ushort[] IdentityTable()
    {
        var table = new ushort[3 * 256];
        for (var channel = 0; channel < 3; channel++)
            for (var i = 0; i < 256; i++)
                table[channel * 256 + i] = (ushort)(i * 257);

        return table;
    }

    private static ushort[] ConstantClut(byte grid, ushort l, ushort a, ushort b)
    {
        var samples = new ushort[grid * grid * grid * 3];
        for (var i = 0; i < samples.Length; i += 3)
        {
            samples[i] = l;
            samples[i + 1] = a;
            samples[i + 2] = b;
        }

        return samples;
    }

    private static ushort[] ConstantClut8(byte grid, byte l, byte a, byte b) =>
        ConstantClut(grid, (ushort)(l * 257), (ushort)(a * 257), (ushort)(b * 257));

    /// <summary>The cube whose corners are the device coordinates themselves, in u1Fixed15.</summary>
    private static ushort[] XyzIdentityClut(byte grid)
    {
        var samples = new ushort[grid * grid * grid * 3];
        var index = 0;
        for (var r = 0; r < grid; r++)
            for (var g = 0; g < grid; g++)
                for (var b = 0; b < grid; b++)
                {
                    samples[index++] = (ushort)(r * 32768 / (grid - 1));
                    samples[index++] = (ushort)(g * 32768 / (grid - 1));
                    samples[index++] = (ushort)(b * 32768 / (grid - 1));
                }

        return samples;
    }

    // ---------------------------------------------------------------------------------------------------
    // Profile assembly
    // ---------------------------------------------------------------------------------------------------

    private static byte[] Profile(string colourSpace, string pcs, uint version, params (string Signature, byte[] Data)[] tags)
    {
        var tableSize = 4 + tags.Length * 12;
        var total = HeaderSize + tableSize;

        var offsets = new int[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            while (total % 4 != 0)
                total++;

            offsets[i] = total;
            total += tags[i].Data.Length;
        }

        var profile = new byte[total];
        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(0, 4), total);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8, 4), version);
        "scnr"u8.CopyTo(profile.AsSpan(12));
        System.Text.Encoding.ASCII.GetBytes(colourSpace.PadRight(4)).AsSpan(0, 4).CopyTo(profile.AsSpan(16));
        System.Text.Encoding.ASCII.GetBytes(pcs.PadRight(4)).AsSpan(0, 4).CopyTo(profile.AsSpan(20));
        "acsp"u8.CopyTo(profile.AsSpan(36));

        // D50 illuminant, s15Fixed16.
        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(68, 4), 63189);
        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(72, 4), 65536);
        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(76, 4), 54061);

        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(HeaderSize, 4), tags.Length);
        for (var i = 0; i < tags.Length; i++)
        {
            var entry = HeaderSize + 4 + i * 12;
            System.Text.Encoding.ASCII.GetBytes(tags[i].Signature).AsSpan(0, 4).CopyTo(profile.AsSpan(entry));
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(entry + 4, 4), offsets[i]);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(entry + 8, 4), tags[i].Data.Length);
            tags[i].Data.CopyTo(profile, offsets[i]);
        }

        return profile;
    }

    private static void WriteMatrix(Span<byte> destination, double[]? matrix)
    {
        for (var i = 0; i < 9; i++)
        {
            var value = matrix is null ? (i % 4 == 0 ? 1.0 : 0.0) : matrix[i];
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(i * 4, 4), (int)Math.Round(value * 65536.0));
        }
    }

    private static int WriteTable(byte[] tag, int offset, ushort[] table)
    {
        for (var i = 0; i < table.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(offset + i * 2, 2), table[i]);

        return offset + table.Length * 2;
    }
}
