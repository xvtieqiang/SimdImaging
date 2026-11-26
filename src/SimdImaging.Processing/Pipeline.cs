using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>
/// The row-copy destination and its workspace reservation. It can be rented before an encoder is opened so that
/// allocation or budget refusal cannot leave a partial output stream.
/// </summary>
internal sealed class PipelineTransferBuffer : IDisposable
{
    private WorkingBuffer? _buffer;
    private IDisposable? _lease;

    internal PipelineTransferBuffer(WorkingBuffer buffer, IDisposable? lease, int rows)
    {
        _buffer = buffer;
        _lease = lease;
        Rows = rows;
    }

    internal int Rows { get; }

    internal PixelView View =>
        (_buffer ?? throw new ObjectDisposedException(nameof(PipelineTransferBuffer))).View;

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = null;
        try
        {
            buffer?.Dispose();
        }
        finally
        {
            _lease?.Dispose();
            _lease = null;
        }
    }
}

/// <summary>
/// The one driver every row pipeline ends with: pull rows from a source, hand them to a sink in bands. Stages
/// compose as sources (plan → resize → plan), so the driver has nothing to know about them.
/// </summary>
internal static class Pipeline
{
    /// <summary>Rows per read; the source decides its own internal band, this is only the transfer size.</summary>
    internal const int TransferRows = 256;

    /// <summary>Reserves and rents the final row-copy buffer, before any destination encoder is opened.</summary>
    internal static PipelineTransferBuffer RentTransferBuffer(IRowSource source, ExecuteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new ExecuteOptions();

        var rows = Math.Min(TransferRows, source.Height);
        var bytes = checked((long)source.Width * rows * source.Format.BytesPerPixel());
        if (options.MaxWorkingBytes > 0 && bytes > options.MaxWorkingBytes)
            throw new InvalidOperationException(
                $"The pipeline transfer buffer needs {bytes} bytes, above " +
                $"ExecuteOptions.MaxWorkingBytes = {options.MaxWorkingBytes}.");

        var lease = options.Workspace?.Reserve(bytes, "pipeline transfer buffer");
        WorkingBuffer? buffer = null;
        try
        {
            buffer = WorkingBuffer.Rent(source.Width, rows, source.Format);
            var transfer = new PipelineTransferBuffer(buffer, lease, rows);
            buffer = null;
            return transfer;
        }
        catch
        {
            buffer?.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    /// <summary>Drains <paramref name="source"/> into <paramref name="sink"/>; returns the rows copied.</summary>
    public static int Copy(IRowSource source, IRowSink sink, CancellationToken cancellationToken,
        ExecuteOptions? options = null)
    {
        using var transfer = RentTransferBuffer(source, options);
        return Copy(source, sink, cancellationToken, transfer);
    }

    /// <summary>Drains rows using a transfer buffer the caller already reserved and rented.</summary>
    internal static int Copy(IRowSource source, IRowSink sink, CancellationToken cancellationToken,
        PipelineTransferBuffer transfer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(transfer);

        var bandRows = transfer.Rows;
        var view = transfer.View;
        if (view.Width != source.Width || view.Format != source.Format ||
            bandRows != Math.Min(TransferRows, source.Height))
            throw new ArgumentException("The pipeline transfer buffer does not match the row source.", nameof(transfer));

        var copied = 0;
        while (copied < source.Height)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = Math.Min(bandRows, source.Height - copied);
            var slice = view.Slice(new PixelRect(0, 0, source.Width, want));
            var read = source.ReadRows(slice.Span, slice.Stride, want);
            if (read <= 0)
            {
                // A source that stopped because the caller cancelled must surface as a cancellation, not as a
                // truncated stream.
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidDataException(
                    $"The row source ended after {copied} of {source.Height} rows.");
            }

            sink.WriteRows(slice.Span, slice.Stride, read);
            copied += read;
        }

        return copied;
    }
}
