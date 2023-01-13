using System.IO.Pipelines;

namespace Spangle;

public interface IReceiver<in TContext> where TContext : IReceiverContext, new()
{
    ValueTask BeginReadAsync(string id, Stream duplexStream, CancellationToken ct = default) =>
        BeginReadAsync(id, duplexStream, duplexStream, ct);

    ValueTask BeginReadAsync(string id, Stream reader, Stream writer, CancellationToken ct = default)
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

        return BeginReadAsync(id, readPipe, writePipe, ct);
    }

    ValueTask BeginReadAsync(string id, PipeReader reader, PipeWriter writer, CancellationToken ct = default) =>
        BeginReadAsync(new TContext { Id = id, Reader = reader, Writer = writer });

    ValueTask BeginReadAsync(TContext context);
}
