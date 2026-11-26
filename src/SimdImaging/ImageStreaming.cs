using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tiff;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>What <see cref="ImageTranscode"/> asks of the decode, the plan and the encode.</summary>
public sealed record ProcessOptions
{
    /// <summary>Defaults: native depth, whole image, encoder defaults.</summary>
    public static ProcessOptions Default { get; } = new();

    /// <summary>
    /// Decode options. <see cref="LoadOptions.Region"/>, <see cref="LoadOptions.ScaleDenominator"/> and
    /// <see cref="LoadOptions.ApplyOrientation"/> are handed to the codec's row reader, so a region decode streams
    /// in the same pass. <see cref="LoadOptions.PreferNativeScale"/> (with a scale above 1) cannot stream — the
    /// output size has to be negotiated from the whole header before decoding — so it falls back to the whole-image
    /// path and is reported as <see cref="StreamRefusal.DecodeNeedsWholeInput"/>.
    /// </summary>
    public LoadOptions? Load { get; init; }

    /// <summary>
    /// Whether the source's Exif / XMP / ICC blocks are carried into the output.
    /// <para>Default <c>false</c>: the codec reads metadata by walking the whole file, which is exactly what a
    /// streamed transcode exists to avoid. Turning it on is an explicit trade — one extra pass over the source —
    /// and it is why the default output is no longer byte-identical to <c>Load → Save</c> for a file that carries
    /// metadata. Pass the blocks yourself with <see cref="SaveOptions.Metadata"/> when only some of them matter.</para>
    /// </summary>
    public bool PreserveMetadata { get; init; }

    /// <summary>Encoder options. <see cref="SaveOptions.Metadata"/> replaces whatever
    /// <see cref="PreserveMetadata"/> would have carried, and works regardless of it.</summary>
    public SaveOptions? Save { get; init; }

    /// <summary>Band height, parallel degree, working-set ceiling and cancellation for the plan.</summary>
    public ExecuteOptions? Execute { get; init; }
}

/// <summary>How a file-side chain produced its output.</summary>
public enum ProcessMode : byte
{
    /// <summary>The plan ran over decoded rows: the source was never held whole.</summary>
    Streamed = 0,

    /// <summary>The result was produced whole: the plan cannot stream, or a region was selected.</summary>
    Materialized,

    /// <summary>The plan ran one rectangle at a time and the encoder consumed rows as bands completed.</summary>
    Tiled,
}

/// <summary>What a file-side chain did, and how much of it was actually streamed.</summary>
/// <remarks>
/// The two <c>IsStreaming</c> flags are separate from <see cref="Mode"/> on purpose. A codec that cannot decode a
/// variant as its input arrives (progressive JPEG, interlaced PNG, a scaled decode) buffers the whole image behind
/// the row API: the plan still streams, the decode did not. Reporting one boolean would make that case
/// indistinguishable from a real stream.
/// </remarks>
/// <param name="Mode">Whether the plan ran over rows or over a whole image.</param>
/// <param name="Refusal">Why the plan was materialised; <see cref="StreamRefusal.None"/> when it streamed. A region
/// run also reports <see cref="StreamRefusal.None"/>: it is materialised because the caller selected a rectangle,
/// not because the plan refused rows. The two are told apart by the entry point that was called.</param>
/// <param name="ReaderIsStreaming">The decoder produced rows as the input arrived.</param>
/// <param name="EncoderIsStreaming">The encoder wrote the file as rows arrived (false for WebP lossless, palette
/// quantisation, or a TIFF writer whose stream cannot seek).</param>
/// <param name="Width">Output width.</param>
/// <param name="Height">Output height.</param>
/// <param name="RowsWritten">Rows written to the encoder.</param>
/// <param name="MetadataCarried">True when metadata blocks reached the encoder — from the source
/// (<see cref="ProcessOptions.PreserveMetadata"/> for a streamed run; a materialised run decodes the file whole and
/// carries what it holds) or from <see cref="ProcessOptions.Save"/>. False is the default for a streamed run and is
/// reported rather than left to be discovered.</param>
public readonly record struct ProcessReport(ProcessMode Mode, StreamRefusal Refusal, bool ReaderIsStreaming,
    bool EncoderIsStreaming, int Width, int Height, int RowsWritten, bool MetadataCarried)
{
    /// <summary>The peak engine-managed working set the run reserved; 0 when the run did not measure one. When the
    /// caller supplied <see cref="ExecuteOptions.Workspace"/> through the transcode options, this is that account's
    /// peak, which includes reservations made before the run.</summary>
    public long PeakWorkingBytes { get; init; }

    /// <summary>The compiler's upper-bound working-set prediction for the run; 0 when no compiled plan was involved.</summary>
    public long PredictedWorkingBytes { get; init; }

    /// <summary>Every layer streamed: the plan, the decoder and the encoder.</summary>
    public bool FullyStreamed => Mode == ProcessMode.Streamed && ReaderIsStreaming && EncoderIsStreaming;
}

public sealed partial class Image
{
    /// <summary>
    /// Whether the decode options need the whole input before a row can be produced: a codec-negotiated scale is
    /// chosen from the header, and a row reader has to be opened with its size already settled.
    /// </summary>
    internal static bool RequiresWholeInput(LoadOptions load) =>
        load.PreferNativeScale && (load.ScaleDenominator ?? 1) > 1;

    /// <summary>
    /// Runs a caller-built stage chain: decode, wrap the rows through each stage, encode. The output size must be
    /// known up front because the encoder is opened before the first row is pulled.
    /// </summary>
    internal static ProcessReport ProcessChain(Stream source, Stream destination, ImageFormat format,
        Func<int, int, (int Width, int Height)> sizeOf, Func<IRowSource, IRowSource> chain, ProcessOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sizeOf);
        ArgumentNullException.ThrowIfNull(chain);
        if (format == ImageFormat.Unknown)
            throw new ArgumentException("Unknown is not an encodable format.", nameof(format));

        options ??= ProcessOptions.Default;
        var load = options.Load ?? LoadOptions.Default;
        var save = options.Save ?? SaveOptions.Default;

        var start = source.CanSeek ? source.Position : 0;

        var (working, rowStream, headerMetadata) = ResolveFormat(source, start, load, options.PreserveMetadata,
            cancellationToken);
        if (working == WorkingFormat.Rgba32F)
            throw new NotSupportedException(
                "Float32 samples have no row reader: a streamed transcode cannot decode them. Load as Rgba32F and "
                + "encode explicitly, or ask for Rgba8 / Rgba16 in LoadOptions.Format.");

        var decodeOptions = new DecodeOptions
        {
            Region = load.Region,
            ScaleDenominator = load.ScaleDenominator ?? 1,
            ApplyOrientation = load.ApplyOrientation,
        };

        var pixelFormat = working.ToPixelFormat();
        using var reader = ImageCodecs.OpenRowReader(rowStream, pixelFormat, decodeOptions, leaveOpen: true,
            cancellationToken);

        if ((long)reader.Width * reader.Height > load.MaxPixels)
            throw new InvalidOperationException(
                $"The decoded image would be {reader.Width}x{reader.Height} = {(long)reader.Width * reader.Height} "
                + $"pixels, above the {load.MaxPixels} pixel limit.");

        var (outputWidth, outputHeight) = sizeOf(reader.Width, reader.Height);

        // The encoder session is opened only after the chain's output size is known: creating one and abandoning it
        // uncommitted would leave whatever it had already written in the caller's stream.
        var encodeOptions = BuildEncodeOptions(format, save);
        using var session = ImageCodecs.CreateEncoder(format, destination, encodeOptions, leaveOpen: true);
        // Colour encoding always travels (it is in the header the reader already parsed). Metadata blocks do not,
        // unless the caller asked for them or supplied their own: reading them means walking the whole file.
        var metadata = save.Metadata ?? headerMetadata;
        var frame = session.AddFrame(outputWidth, outputHeight, pixelFormat, reader.Info.ColorEncoding,
            metadata ?? ImageMetadata.Empty, null);

        var sourceRows = new CodecRowSource(reader, working, cancellationToken);
        var sink = new CodecRowSink(frame, cancellationToken);

        // A caller-supplied stage chain (crop / pad / flip / plan / resize, in order). Its output size was settled
        // before the encoder was opened, so the frame already has it.
        var chained = chain(sourceRows);
        int writtenRows;
        try
        {
            writtenRows = Pipeline.Copy(chained, sink, cancellationToken, options.Execute);
        }
        finally
        {
            (chained as IDisposable)?.Dispose();
        }

        var report = new ProcessReport(ProcessMode.Streamed, StreamRefusal.None, reader.IsStreaming,
            session.IsStreaming, outputWidth, outputHeight, writtenRows, metadata is not null);
        session.Commit(cancellationToken);
        return report;
    }

    /// <summary>
    /// The working format to decode into, and the stream the row reader should read.
    /// <para>Header-only by default: a seekable source is sniffed at its first kilobytes (a TIFF through its
    /// directory, which the codec reads without decoding), a forward-only one replays what was sniffed.
    /// <paramref name="preserveMetadata"/> asks the codec for the file's metadata blocks as well, which the codec
    /// documents as a whole-file read — the caller's explicit trade, not the default.</para>
    /// </summary>
    internal static (WorkingFormat Format, Stream Rows, ImageMetadata? Metadata) ResolveFormat(Stream source,
        long start, LoadOptions load, bool preserveMetadata, CancellationToken cancellationToken)
    {
        // The only path that may walk the whole file, and only when the caller asked for metadata.
        if (preserveMetadata && source.CanSeek)
        {
            WorkingFormat probed;
            ImageMetadata metadata;
            using (var session = ImageDecoder.Open(source, leaveOpen: true, forwardOnly: false))
            {
                probed = load.Format ?? WorkingFormatExtensions.FromSampleFormat(session.GetFrame(0).Info.SampleFormat);
                metadata = session.Metadata;
            }

            source.Position = start;
            return (probed, source, metadata);
        }

        if (load.Format is { } requested)
        {
            if (source.CanSeek)
                source.Position = start;
            return (requested, source, null);
        }

        var seekable = source.CanSeek;
        var prefix = seekable
            ? ReadPrefix(source, start, 4096, cancellationToken)
            : ReadPrefixForward(source, 4096, cancellationToken);

        if (TiffRegionSource.LooksLikeTiff(prefix))
        {
            // A TIFF's directory may sit after the pixels, so no prefix can tell its depth; the layout read can, for
            // a handful of seeks. It needs the header at absolute position 0, like every TIFF offset.
            if (!seekable || start != 0)
                throw new NotSupportedException(
                    "A TIFF can only be sniffed from a seekable source whose header is at position 0: its offsets are "
                    + "absolute. Name the working format in LoadOptions.Format to stream it from elsewhere.");

            var layout = TiffReader.ReadLayout(source, 0);
            source.Position = start;
            var format = load.Format ?? (layout.BitsPerSample == 16 ? WorkingFormat.Rgba16 : WorkingFormat.Rgba8);
            return (format, source, null);
        }

        if (!ImageCodecs.TryReadInfo(prefix, out var info))
        {
            prefix = seekable
                ? ReadPrefix(source, start, 1 << 20, cancellationToken)
                : ReadPrefixForward(source, 1 << 20, cancellationToken, prefix);
            if (!ImageCodecs.TryReadInfo(prefix, out info))
                throw new NotSupportedException(
                    "The header did not fit in the 1 MiB this path buffers to sniff. Name the working format in "
                    + "LoadOptions.Format to stream it without a sniff.");
        }

        return (WorkingFormatExtensions.FromSampleFormat(info.SampleFormat),
            seekable ? source : new PrefixedStream(prefix, source), null);
    }

    /// <summary>Reads up to <paramref name="length"/> bytes from a forward-only stream, keeping the bytes of an
    /// earlier read so the caller can grow the sniff without losing them.</summary>
    private static byte[] ReadPrefixForward(Stream source, int length, CancellationToken cancellationToken,
        byte[]? head = null)
    {
        var buffer = new byte[Math.Max(length, head?.Length ?? 0)];
        head?.CopyTo(buffer, 0);
        var filled = head?.Length ?? 0;
        while (filled < length)
        {
            var read = source.Read(buffer, filled, length - filled);
            if (read <= 0)
                break;
            filled += read;
            cancellationToken.ThrowIfCancellationRequested();
        }

        return filled == buffer.Length ? buffer : buffer[..filled];
    }

    /// <summary>Reads up to <paramref name="length"/> bytes from the source's current position and, when it can seek,
    /// puts the position back so the reader that follows sees the image from its first byte.</summary>
    private static byte[] ReadPrefix(Stream source, long start, int length, CancellationToken cancellationToken)
    {
        var available = source.CanSeek && source.Length > start
            ? (int)Math.Min(source.Length - start, length)
            : length;
        var buffer = new byte[Math.Max(4, available)];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = source.Read(buffer, filled, buffer.Length - filled);
            if (read <= 0)
                break;
            filled += read;
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (source.CanSeek)
            source.Position = start;

        return filled == buffer.Length ? buffer : buffer[..filled];
    }

    /// <summary>The codec's row reader as a <see cref="IRowSource"/>, so the plan can pull bands from it.</summary>
    private sealed class CodecRowSource(IImageRowReader reader, WorkingFormat format,
        CancellationToken cancellationToken) : IRowSource
    {
        public int Width => reader.Width;

        public int Height => reader.Height;

        public WorkingFormat Format => format;

        public int ReadRows(Span<byte> destination, int stride, int maxRows) =>
            reader.ReadRows(destination, maxRows, stride, cancellationToken);
    }

    /// <summary>The encoder session's frame as a <see cref="IRowSink"/>: rows go out as they are finished.</summary>
    private sealed class CodecRowSink(StreamFrameEncoder frame, CancellationToken cancellationToken) : IRowSink
    {
        public void WriteRows(ReadOnlySpan<byte> rows, int stride, int rowCount) =>
            frame.WriteRows(rows, rowCount, stride, cancellationToken);
    }

    /// <summary>
    /// A forward-only stream that serves bytes already read for sniffing, then continues with the real stream.
    /// Needed because <c>OpenRowReader</c> re-reads the header from position 0 and a request body cannot go back.
    /// </summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private int _consumed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_consumed < prefix.Length)
            {
                var take = Math.Min(buffer.Length, prefix.Length - _consumed);
                prefix.Span.Slice(_consumed, take).CopyTo(buffer);
                _consumed += take;
                return take;
            }

            return inner.Read(buffer);
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 1 ? one[0] : -1;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
