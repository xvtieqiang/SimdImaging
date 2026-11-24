using System.Buffers.Binary;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tiff;

namespace SimdImaging.Tests;

/// <summary>
/// TIFF bytes for the region tests, produced at run time — the repository never commits an image.
/// <para>Two legs: the codec's own writer for the layouts it can produce (strips), and a small hand-written
/// classic-TIFF builder for the ones it cannot (tiles, MinIsWhite, float, extra samples, unsupported photometrics).
/// Every hand-written fixture is compared against <c>Image.Load</c> in the tests, so a wrong offset fails loudly
/// instead of passing silently.</para>
/// </summary>
internal static class TiffFixture
{
    /// <summary>Rows per strip for the codec-written fixtures.</summary>
    public const int RowsPerStrip = 8;

    /// <summary>Writes a stripped TIFF through the codec's encoder. The pixel format decides samples, photometric
    /// and depth, exactly as a caller's own encode would.</summary>
    public static byte[] Strips(int width, int height, PixelFormat format, byte[] pixels, int rowsPerStrip = RowsPerStrip) =>
        ImageCodecs.Encode(pixels, width, height, format, ImageFormat.Tiff,
            options: new TiffEncodeOptions { Page = new TiffPageOptions { RowsPerStrip = rowsPerStrip } });

    /// <summary>
    /// A classic little-endian, uncompressed TIFF: strips when <paramref name="tileWidth"/> is 0, tiles otherwise.
    /// <paramref name="pixels"/> is tightly packed at <paramref name="samplesPerPixel"/> ×
    /// <paramref name="bitsPerSample"/> / 8 bytes per pixel.
    /// </summary>
    public static byte[] Build(int width, int height, int samplesPerPixel, int bitsPerSample, ushort photometric,
        byte[] pixels, ushort sampleFormat = 1, short orientation = 1, short? extraSamples = null,
        int tileWidth = 0, int tileHeight = 0, int rowsPerStrip = 8)
    {
        var tiled = tileWidth > 0;
        var bytesPerSample = bitsPerSample / 8;
        var bytesPerPixel = samplesPerPixel * bytesPerSample;
        var across = tiled ? Ceil(width, tileWidth) : 1;
        var down = tiled ? Ceil(height, tileHeight) : Ceil(height, rowsPerStrip);

        var blocks = new List<byte[]>();
        for (var segmentY = 0; segmentY < down; segmentY++)
        {
            for (var segmentX = 0; segmentX < across; segmentX++)
            {
                var originX = tiled ? segmentX * tileWidth : 0;
                var originY = segmentY * (tiled ? tileHeight : rowsPerStrip);
                var clippedWidth = Math.Min(tiled ? tileWidth : width, width - originX);
                var clippedHeight = Math.Min(tiled ? tileHeight : rowsPerStrip, height - originY);
                var storedWidth = tiled ? tileWidth : clippedWidth;
                var storedHeight = tiled ? tileHeight : clippedHeight;
                var block = new byte[storedWidth * storedHeight * bytesPerPixel];

                for (var y = 0; y < clippedHeight; y++)
                {
                    var sourceRow = ((originY + y) * width + originX) * bytesPerPixel;
                    var targetRow = y * storedWidth * bytesPerPixel;
                    Buffer.BlockCopy(pixels, sourceRow, block, targetRow, clippedWidth * bytesPerPixel);
                }

                blocks.Add(block);
            }
        }

        var shortArray = (ushort value, int count) =>
        {
            var data = new byte[count * 2];
            for (var i = 0; i < count; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), value);
            return data;
        };

        var entries = new List<Entry>
        {
            Entry.Long(256, (uint)width),
            Entry.Long(257, (uint)height),
            Entry.ShortArray(258, shortArray((ushort)bitsPerSample, samplesPerPixel)),
            Entry.Short(259, 1),
            Entry.Short(262, photometric),
            Entry.Short(274, (ushort)orientation),
            Entry.Short(277, (ushort)samplesPerPixel),
            Entry.Short(284, 1),
            Entry.ShortArray(339, shortArray(sampleFormat, samplesPerPixel)),
        };

        if (extraSamples is { } extra)
            entries.Add(Entry.ShortArray(338, shortArray((ushort)extra, 1)));

        if (tiled)
        {
            entries.Add(Entry.Short(322, (ushort)tileWidth));
            entries.Add(Entry.Short(323, (ushort)tileHeight));
            entries.Add(Entry.LongArray(324, blocks.Count));
            entries.Add(Entry.LongArray(325, blocks.Count));
        }
        else
        {
            entries.Add(Entry.Long(278, (uint)rowsPerStrip));
            entries.Add(Entry.LongArray(273, blocks.Count));
            entries.Add(Entry.LongArray(279, blocks.Count));
        }

        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        // Layout: header, IFD, out-of-line values, pixel data.
        const int headerSize = 8;
        var ifdSize = 2 + (12 * entries.Count) + 4;
        var valueOffset = headerSize + ifdSize;
        var valuesSize = entries.Where(e => e.NeedsPointer).Sum(e => e.Data.Length);
        var dataOffset = valueOffset + valuesSize;

        var offsets = new int[blocks.Count];
        var running = dataOffset;
        for (var i = 0; i < blocks.Count; i++)
        {
            offsets[i] = running;
            running += blocks[i].Length;
        }

        var offsetTag = tiled ? 324 : 273;
        var countTag = tiled ? 325 : 279;
        foreach (var entry in entries)
        {
            if (entry.Tag == offsetTag)
            {
                for (var i = 0; i < blocks.Count; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(entry.Data.AsSpan(i * 4), (uint)offsets[i]);
            }
            else if (entry.Tag == countTag)
            {
                for (var i = 0; i < blocks.Count; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(entry.Data.AsSpan(i * 4), (uint)blocks[i].Length);
            }
        }

        var output = new byte[running];
        output[0] = 0x49;
        output[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(headerSize), (ushort)entries.Count);

        var cursor = headerSize + 2;
        var valueCursor = valueOffset;
        foreach (var entry in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor), entry.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor + 2), entry.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 4), (uint)entry.Count);

            if (entry.NeedsPointer)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 8), (uint)valueCursor);
                entry.Data.CopyTo(output.AsSpan(valueCursor));
                valueCursor += entry.Data.Length;
            }
            else
            {
                entry.Data.CopyTo(output.AsSpan(cursor + 8));
            }

            cursor += 12;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor), 0);

        var blockCursor = dataOffset;
        foreach (var block in blocks)
        {
            block.CopyTo(output.AsSpan(blockCursor));
            blockCursor += block.Length;
        }

        return output;
    }

    private static int Ceil(int value, int divisor) => (value + divisor - 1) / divisor;

    private readonly struct Entry(ushort tag, ushort type, int count, byte[] data)
    {
        public ushort Tag { get; } = tag;
        public ushort Type { get; } = type;
        public int Count { get; } = count;
        public byte[] Data { get; } = data;

        public bool NeedsPointer => Data.Length > 4;

        public static Entry Short(ushort tag, ushort value)
        {
            var data = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(data, value);
            return new Entry(tag, 3, 1, data);
        }

        public static Entry Long(ushort tag, uint value)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            return new Entry(tag, 4, 1, data);
        }

        public static Entry ShortArray(ushort tag, byte[] payload) => new(tag, 3, payload.Length / 2, payload);

        public static Entry LongArray(ushort tag, int count) => new(tag, 4, count, new byte[count * 4]);
    }
}
