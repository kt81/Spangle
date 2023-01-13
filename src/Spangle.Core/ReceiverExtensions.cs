using System.IO.Pipelines;

namespace Spangle;

public static class ReceiverExtensions
{
    public static ValueTask BeginReadAsync<TContext>(
        this IReceiver<TContext> receiver,
        string id, Stream duplexStream,
        CancellationToken ct = default)
        where TContext : IReceiverContext, new() =>
        BeginReadAsync(receiver, id, duplexStream, duplexStream, ct);

    public static ValueTask BeginReadAsync<TContext>(
        this IReceiver<TContext> receiver,
        string id,
        Stream reader,
        Stream writer,
        CancellationToken ct = default)
        where TContext : IReceiverContext, new()
    {
        if (!reader.CanRead)
        {
            throw new ArgumentException("Not a readable stream.", nameof(reader));
        }

        if (!writer.CanWrite)
        {
            throw new ArgumentException("Not a writable stream.", nameof(writer));
        }

        var readPipe = PipeReader.Create(reader, new StreamPipeReaderOptions(leaveOpen: true));
        var writePipe = PipeWriter.Create(writer, new StreamPipeWriterOptions(leaveOpen: true));

        return BeginReadAsync(receiver, id, readPipe, writePipe, ct);
    }

    public static ValueTask BeginReadAsync<TContext>(
        this IReceiver<TContext> receiver,
        string id,
        IDuplexPipe duplexPipe,
        CancellationToken ct = default)
        where TContext : IReceiverContext, new() =>
        BeginReadAsync(receiver, id, duplexPipe.Input, duplexPipe.Output, ct);

    public static ValueTask BeginReadAsync<TContext>(
        this IReceiver<TContext> receiver,
        string id,
        PipeReader reader,
        PipeWriter writer,
        CancellationToken ct = default)
        where TContext : IReceiverContext, new() =>
        receiver.BeginReadAsync(new TContext { Id = id, Reader = reader, Writer = writer, CancellationToken = ct });
}
