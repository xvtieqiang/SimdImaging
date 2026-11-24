namespace SimdImaging.Abstractions;

/// <summary>
/// A source of pixels that can only be read downwards, a band of rows at a time: a decoder reading a file or a
/// request body, a generator, a test. The counterpart of <see cref="IRowSink"/>, and the seam that lets a plan
/// run without the whole image in memory.
/// <para>
/// <see cref="Height"/> is known from the header, not discovered while reading. That is what makes a banded
/// executor identical to a whole-image one: the kernels clamp at the real image edge, not at the edge of whatever
/// rows happen to have arrived.
/// </para>
/// </summary>
public interface IRowSource
{
    /// <summary>Pixels per row.</summary>
    int Width { get; }

    /// <summary>Rows in the whole image.</summary>
    int Height { get; }

    /// <summary>The layout of the rows this source writes.</summary>
    WorkingFormat Format { get; }

    /// <summary>
    /// Writes the next rows into <paramref name="destination"/>, row <c>i</c> at byte <c>i × stride</c>, and
    /// returns how many. Fewer than <paramref name="maxRows"/> only at the end of the image; 0 once every row has
    /// been read.
    /// <para>
    /// <paramref name="destination"/> holds at least <c>stride × maxRows</c> bytes, and <paramref name="stride"/>
    /// is at least <c>Width × Format.BytesPerPixel()</c> (0 means tightly packed). The bytes written are the same
    /// bytes a whole-image decode of the same input would produce in this format.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidDataException">The underlying stream is malformed or ends before
    /// <see cref="Height"/> rows.</exception>
    int ReadRows(Span<byte> destination, int stride, int maxRows);
}

/// <summary>
/// A destination for pixels that only accepts appends, a band of rows at a time: an encoder writing a file, a
/// hash, a socket. Rows arrive top to bottom, once, in order.
/// </summary>
public interface IRowSink
{
    /// <summary>
    /// Appends <paramref name="rowCount"/> rows; row <c>i</c> starts at byte <c>i × stride</c> of
    /// <paramref name="rows"/> (0 means tightly packed).
    /// </summary>
    void WriteRows(ReadOnlySpan<byte> rows, int stride, int rowCount);
}
