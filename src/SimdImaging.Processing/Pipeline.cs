using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>
/// The one driver every row pipeline ends with: pull rows from a source, hand them to a sink in bands. Stages
/// compose as sources (plan → resize → plan), so the driver has nothing to know about them.
/// </summary>
internal static class Pipeline
{
    /// <summary>Rows per read; the source decides its own internal band, this is only the transfer size.</summary>
    private const int ReadRows = 256;

    /// <summary>Drains <paramref name="source"/> into <paramref name="sink"/>; returns the rows copied.</summary>
    public static int Copy(IRowSource source, IRowSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        var bandRows = Math.Min(ReadRows, source.Height);
        using var buffer = WorkingBuffer.Rent(source.Width, bandRows, source.Format);
        var view = buffer.View;
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
