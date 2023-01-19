using System.IO.Pipelines;

namespace Spangle;

public static class ReceiverExtensions
{
    public static ValueTask BeginReadAsync<TContext>(
        this IReceiver<TContext> receiver,
        string id, Stream duplexStream,
        CancellationToken ct = default)
        where TContext : IReceiverContext<TContext> =>
        BeginReadAsync(receiver, id, duplexStream, duplexStream, ct);

    public static ValueTask BeginReadAsync<TContext>(
        this IReceiver<TContext> receiver,
        string id,
        Stream reader,
        Stream writer,
        CancellationToken ct = default)
        where TContext : IReceiverContext<TContext>
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
        where TContext : IReceiverContext<TContext> =>
        BeginReadAsync(receiver, id, duplexPipe.Input, duplexPipe.Output, ct);

    public static ValueTask BeginReadAsync<TContext>(
        this IReceiver<TContext> receiver,
        string id,
        PipeReader reader,
        PipeWriter writer,
        CancellationToken ct = default)
        where TContext : IReceiverContext<TContext> =>
        receiver.StartAsync(TContext.CreateInstance(id, reader, writer, ct));
}
