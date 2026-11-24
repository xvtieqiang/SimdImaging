using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tiff;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>What <c>Image.ProcessTo</c> asks of the decode, the plan and the encode.</summary>
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

/// <summary>How <c>Image.ProcessTo</c> produced its output.</summary>
public enum ProcessMode : byte
{
    /// <summary>The plan ran over decoded rows: the source was never held whole.</summary>
    Streamed = 0,

    /// <summary>The plan cannot stream, so the image was decoded, processed and encoded in full.</summary>
    Materialized,
}

/// <summary>What <c>Image.ProcessTo</c> did, and how much of it was actually streamed.</summary>
/// <remarks>
/// The two <c>IsStreaming</c> flags are separate from <see cref="Mode"/> on purpose. A codec that cannot decode a
/// variant as its input arrives (progressive JPEG, interlaced PNG, a scaled decode) buffers the whole image behind
/// the row API: the plan still streams, the decode did not. Reporting one boolean would make that case
/// indistinguishable from a real stream.
/// </remarks>
/// <param name="Mode">Whether the plan ran over rows or over a whole image.</param>
/// <param name="Refusal">Why the plan was materialised; <see cref="StreamRefusal.None"/> when it streamed.</param>
/// <param name="ReaderIsStreaming">The decoder produced rows as the input arrived.</param>
/// <param name="EncoderIsStreaming">The encoder wrote the file as rows arrived (false for WebP lossless, palette
/// quantisation, or a TIFF writer whose stream cannot seek).</param>
/// <param name="Width">Output width.</param>
/// <param name="Height">Output height.</param>
/// <param name="RowsWritten">Rows written to the encoder.</param>
/// <param name="MetadataCarried">True when metadata blocks reached the encoder — from the source
/// (<see cref="ProcessOptions.PreserveMetadata"/>) or from <see cref="ProcessOptions.Save"/>. False is the default
/// for a streamed run and is reported rather than left to be discovered.</param>
public readonly record struct ProcessReport(ProcessMode Mode, StreamRefusal Refusal, bool ReaderIsStreaming,
    bool EncoderIsStreaming, int Width, int Height, int RowsWritten, bool MetadataCarried)
{
    /// <summary>Every layer streamed: the plan, the decoder and the encoder.</summary>
    public bool FullyStreamed => Mode == ProcessMode.Streamed && ReaderIsStreaming && EncoderIsStreaming;
}

public sealed partial class Image
{
    /// <summary>
    /// Decodes <paramref name="sourcePath"/>, runs <paramref name="plan"/> over the rows and encodes the result to
    /// <paramref name="destinationPath"/>, holding one band of rows instead of the whole image.
    /// <para>When the plan cannot stream (see <see cref="ProcessingPlan.CanStream"/>) the file is decoded,
    /// processed and encoded whole, and <see cref="ProcessReport.Refusal"/> says why. Bytes are the same either
    /// way for the same pixels and options; the streamed path does not carry metadata stored after the pixels
    /// unless <see cref="ProcessOptions.Save"/> supplies it.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="NotSupportedException">The extension names no writable format, or the source is float and
    /// has no row projection.</exception>
    /// <exception cref="InvalidDataException">The file is not a recognised image.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; a partial
    /// destination file is left behind, exactly as an interrupted <c>Save</c> would.</exception>
    internal static ProcessReport ProcessTo(string sourcePath, ProcessingPlan plan, string destinationPath,
        ProcessOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destinationPath);

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        return ProcessTo(input, plan, output, FormatFromExtension(Path.GetExtension(destinationPath)), options,
            cancellationToken);
    }

    /// <summary>
    /// The stream form of <see cref="ProcessTo(string, ProcessingPlan, string, ProcessOptions?, CancellationToken)"/>.
    /// The source is read from its current position and left open; it need not seek. The destination is written
    /// from its current position and left open.
    /// <para>When the plan cannot stream, a seekable source is rewound and processed whole; a forward-only source
    /// cannot be re-read, so that combination throws instead of silently producing a different result.</para>
    /// </summary>
    /// <param name="source">The encoded image.</param>
    /// <param name="plan">The same-size steps to apply.</param>
    /// <param name="destination">Where the encoded result is written.</param>
    /// <param name="format">The container to write.</param>
    /// <param name="options">Decode, plan and encode options.</param>
    /// <param name="cancellationToken">Checked between bands and before the encode is committed.</param>
    /// <exception cref="ArgumentException"><paramref name="format"/> is <see cref="ImageFormat.Unknown"/>.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a recognised image.</exception>
    /// <exception cref="NotSupportedException">Float samples (no row reader), a non-seekable source whose header
    /// does not fit the sniff budget (name it in <see cref="ProcessOptions.Load"/>), a non-seekable source with a
    /// plan that cannot stream, or a format this build cannot write.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal static ProcessReport ProcessTo(Stream source, ProcessingPlan plan, Stream destination,
        ImageFormat format, ProcessOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ProcessCore(source, destination, format, options, plan, resizeWidth: null, resizeHeight: null,
            resampler: default, planAfter: null, chain: null, chainSize: null, cancellationToken);
    }

    /// <summary>
    /// Decodes <paramref name="source"/>, resizes it to <paramref name="width"/> × <paramref name="height"/> as the
    /// rows arrive and encodes the result: the first geometric operator to run inside the streaming model, holding a
    /// ring of horizontally resized rows and a staging chunk instead of the image.
    /// <para>Byte-identical to <c>Load → Resize</c> (same weights, same premultiplied floats, same convolution).
    /// A plan that composes a resize with other steps is M18 (§6.7); this entry is one resize and nothing else.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="InvalidOperationException">The ring the vertical pass needs would exceed
    /// <see cref="ExecuteOptions.MaxWorkingBytes"/> (or the 24 MiB default) — the resize is refused rather than
    /// silently allocating it.</exception>
    internal static ProcessReport ResizeTo(Stream source, Stream destination, ImageFormat format, int width,
        int height, Resampler resampler = Resampler.Bicubic, ProcessOptions? options = null,
        ProcessingPlan? plan = null, ProcessingPlan? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return ProcessCore(source, destination, format, options, plan, width, height, resampler, after, chain: null,
            chainSize: null, cancellationToken);
    }

    /// <summary>The path form of <c>ResizeTo</c>: the container comes from the destination's extension, and
    /// <paramref name="plan"/> (when given) runs before the resize, in the same pass.</summary>
    internal static ProcessReport ResizeTo(string sourcePath, string destinationPath, int width, int height,
        Resampler resampler = Resampler.Bicubic, ProcessOptions? options = null, ProcessingPlan? plan = null,
        ProcessingPlan? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        return ResizeTo(input, output, FormatFromExtension(Path.GetExtension(destinationPath)), width, height,
            resampler, options, plan, after, cancellationToken);
    }

    /// <summary>
    /// Runs a caller-built stage chain: decode, wrap the rows through each stage, encode. The output size must be
    /// known up front because the encoder is opened before the first row is pulled.
    /// </summary>
    internal static ProcessReport ProcessChain(Stream source, Stream destination, ImageFormat format,
        Func<int, int, (int Width, int Height)> sizeOf, Func<IRowSource, IRowSource> chain, ProcessOptions? options,
        CancellationToken cancellationToken) =>
        ProcessCore(source, destination, format, options, plan: null, resizeWidth: null, resizeHeight: null,
            resampler: default, planAfter: null, chain, sizeOf, cancellationToken);

    /// <summary>The shared body: decode, run the plan or the single resize, encode. Exactly one of the two is set.</summary>
    private static ProcessReport ProcessCore(Stream source, Stream destination, ImageFormat format,
        ProcessOptions? options, ProcessingPlan? plan, int? resizeWidth, int? resizeHeight, Resampler resampler,
        ProcessingPlan? planAfter, Func<IRowSource, IRowSource>? chain,
        Func<int, int, (int Width, int Height)>? chainSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (format == ImageFormat.Unknown)
            throw new ArgumentException("Unknown is not an encodable format.", nameof(format));

        options ??= ProcessOptions.Default;
        var load = options.Load ?? LoadOptions.Default;
        var save = options.Save ?? SaveOptions.Default;

        var start = source.CanSeek ? source.Position : 0;

        // PreferNativeScale negotiates the output size from the whole header before decoding; a row reader has to be
        // opened with its format and size already settled, so this one goes through the whole-image path — which is
        // exactly the Load the caller asked for.
        var refusal = plan is not null && load.PreferNativeScale && (load.ScaleDenominator ?? 1) > 1
            ? StreamRefusal.DecodeNeedsWholeInput
            : plan?.CanStream() ?? StreamRefusal.None;
        if (refusal == StreamRefusal.None)
            refusal = planAfter?.CanStream() ?? StreamRefusal.None;
        if (refusal != StreamRefusal.None)
            return Materialize(source, start, plan, destination, format, options, refusal, resizeWidth, resizeHeight,
                resampler, planAfter, cancellationToken);

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

        int outputWidth;
        int outputHeight;
        if (chainSize is not null)
        {
            (outputWidth, outputHeight) = chainSize(reader.Width, reader.Height);
        }
        else if (plan is not null && plan.HasResize)
        {
            if (resizeWidth is not null)
                throw new ArgumentException(
                    "The plan already ends with a resize; giving ResizeTo a size as well would resize twice.",
                    nameof(plan));
            (outputWidth, outputHeight) = plan.OutputSize(reader.Width, reader.Height);
        }
        else
        {
            outputWidth = resizeWidth ?? reader.Width;
            outputHeight = resizeHeight ?? reader.Height;
        }

        // The encoder session is opened only after the plan (or the resize) is known to be possible: creating one and
        // abandoning it uncommitted would leave whatever it had already written in the caller's stream.
        var encodeOptions = BuildEncodeOptions(format, save);
        using var session = ImageCodecs.CreateEncoder(format, destination, encodeOptions, leaveOpen: true);
        // Colour encoding always travels (it is in the header the reader already parsed). Metadata blocks do not,
        // unless the caller asked for them or supplied their own: reading them means walking the whole file.
        var metadata = save.Metadata ?? headerMetadata;
        var frame = session.AddFrame(outputWidth, outputHeight, pixelFormat, reader.Info.ColorEncoding,
            metadata ?? ImageMetadata.Empty, null);

        var sourceRows = new CodecRowSource(reader, working, cancellationToken);
        var sink = new CodecRowSink(frame, cancellationToken);

        StreamResult result;
        if (chain is not null)
        {
            // A caller-supplied stage chain (crop / pad / flip / plan / resize, in order). Its output size was settled
            // before the encoder was opened, so the frame already has it.
            var chained = chain(sourceRows);
            try
            {
                result = new StreamResult(StreamRefusal.None, Pipeline.Copy(chained, sink, cancellationToken), 0, 1);
            }
            finally
            {
                (chained as IDisposable)?.Dispose();
            }
        }
        else if (resizeWidth is { } targetWidth)
        {
            // plan → resize → plan is a chain of pull sources: each stage asks the one before it for rows. A missing
            // plan is simply a stage that is not there.
            using var prefix = plan is null ? null : new PlanRowSource(plan, sourceRows, options.Execute);
            var resizeInput = prefix is null ? sourceRows : (IRowSource)prefix;
            using var resized = new ResizeRowSource(resizeInput, targetWidth, outputHeight, resampler,
                options.Execute);
            using var tail = planAfter is null ? null : new PlanRowSource(planAfter, resized, options.Execute);
            var writtenRows = Pipeline.Copy(tail ?? (IRowSource)resized, sink, cancellationToken);
            result = new StreamResult(StreamRefusal.None, writtenRows, prefix?.Bands ?? 0, 1);
        }
        else
        {
            result = plan!.TryExecuteTo(sourceRows, sink, options.Execute);
            if (!result.Succeeded)
                throw new InvalidOperationException(
                    $"The plan reported '{result.Refusal}' at step '{result.RefusingStep}' after CanStream said it "
                    + "could stream. That is a bug in the capability check, not in the caller.");
        }

        var report = new ProcessReport(ProcessMode.Streamed, StreamRefusal.None, reader.IsStreaming,
            session.IsStreaming, outputWidth, outputHeight, result.RowsWritten, metadata is not null);
        session.Commit(cancellationToken);
        return report;
    }

    /// <summary>Decodes, runs the plan and encodes, because the plan cannot stream.</summary>
    private static ProcessReport Materialize(Stream source, long start, ProcessingPlan? plan,
        Stream destination, ImageFormat format, ProcessOptions options, StreamRefusal refusal, int? resizeWidth,
        int? resizeHeight, Resampler resampler, ProcessingPlan? planAfter, CancellationToken cancellationToken)
    {
        if (!source.CanSeek)
            throw new NotSupportedException(
                $"This plan cannot stream ({refusal}), and the source stream cannot be re-read, so it would have to "
                + "be decoded whole — which a forward-only stream cannot do after its header. Use a seekable source, "
                + "or Image.Load + Image.Encode.");

        source.Position = start;
        using var image = Load(source, options.Load, cancellationToken);
        using var filtered = plan is null ? null : image.ApplyWithOptions(plan, options.Execute ?? new ExecuteOptions());
        var current = filtered ?? image;
        using var resized = resizeWidth is { } width
            ? current.Pipe().Resize(width, resizeHeight!.Value, resampler).ToImage()
            : null;
        var resizedOrCurrent = resized ?? current;
        using var tail = planAfter is null
            ? null
            : resizedOrCurrent.ApplyWithOptions(planAfter, options.Execute ?? new ExecuteOptions());
        var output = tail ?? resizedOrCurrent;
        var bytes = Encode(output, format, options.Save, cancellationToken);
        destination.Write(bytes, 0, bytes.Length);

        return new ProcessReport(ProcessMode.Materialized, refusal, ReaderIsStreaming: false,
            EncoderIsStreaming: false, output.Width, output.Height, output.Height, MetadataCarried: true);
    }

    /// <summary>
    /// The working format to decode into, and the stream the row reader should read.
    /// <para>Header-only by default: a seekable source is sniffed at its first kilobytes (a TIFF through its
    /// directory, which the codec reads without decoding), a forward-only one replays what was sniffed.
    /// <paramref name="preserveMetadata"/> asks the codec for the file's metadata blocks as well, which the codec
    /// documents as a whole-file read — the caller's explicit trade, not the default.</para>
    /// </summary>
    private static (WorkingFormat Format, Stream Rows, ImageMetadata? Metadata) ResolveFormat(Stream source,
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
