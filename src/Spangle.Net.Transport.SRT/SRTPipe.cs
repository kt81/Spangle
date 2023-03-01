using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using static SrtSharp.srt;

namespace Spangle.Net.Transport.SRT;

[SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
internal sealed class SRTPipe : IDuplexPipe, IDisposable
{
    private const int BufferSize = 4096;

    private readonly Pipe _receivePipe;
    private readonly Pipe _sendPipe;

    private readonly SRTSOCKET _peerHandle;
    private readonly byte[]    _readBuffer;
    private readonly byte[]    _writeBuffer;

    private readonly CancellationToken _cancellationToken;

    public PipeReader Input => _receivePipe.Reader;
    public PipeWriter Output => _sendPipe.Writer;

    private bool _disposed;

    public SRTPipe(SRTSOCKET peerHandle, CancellationToken cancellationToken = default)
    {
        _peerHandle = peerHandle;
        _readBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        _writeBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        _cancellationToken = cancellationToken;

        var receivePipeOptions = new PipeOptions(useSynchronizationContext: false);
        var sendPipeOptions = new PipeOptions(useSynchronizationContext: false);

        _receivePipe = new Pipe(receivePipeOptions);
        _sendPipe = new Pipe(sendPipeOptions);

        receivePipeOptions.ReaderScheduler.Schedule(
            async obj => await (obj as SRTPipe)!.ReadFromSRT().ConfigureAwait(false), this);
        sendPipeOptions.WriterScheduler.Schedule(
            async obj => await (obj as SRTPipe)!.WriteToSRT().ConfigureAwait(false), this);
    }

    public void Reset()
    {
        _receivePipe.Reset();
        _sendPipe.Reset();
    }

    private async ValueTask ReadFromSRT()
    {
        var writer = _receivePipe.Writer;
        while (!_disposed)
        {
            int size = srt_recvmsg(_peerHandle, _readBuffer, BufferSize);
            if (size == SRT_ERROR)
            {
                await writer.CompleteAsync(new SRTException(srt_getlasterror_str())).ConfigureAwait(false);
                return;
            }

            var result = await writer.WriteAsync(new ReadOnlyMemory<byte>(_readBuffer)[..size], _cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
            {
                return;
            }
        }
    }

    private async ValueTask WriteToSRT()
    {
        var reader = _sendPipe.Reader;
        while (!_disposed)
        {
            var readResult = await reader.ReadAsync(_cancellationToken).ConfigureAwait(false);
            if (readResult.IsCanceled || readResult.IsCompleted)
            {
                return;
            }

            var buff = readResult.Buffer;
            if (buff.Length > BufferSize)
            {
                buff = buff.Slice(buff.Start, BufferSize);
            }
            buff.CopyTo(_writeBuffer);
            reader.AdvanceTo(buff.End);
            int writeResult = srt_send(_peerHandle, _writeBuffer, (int)buff.Length);
            if (writeResult == SRT_ERROR)
            {
                await reader.CompleteAsync(new SRTException(srt_getlasterror_str())).ConfigureAwait(false);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _receivePipe.Reader.Complete();
        _receivePipe.Writer.Complete();

        _sendPipe.Reader.Complete();
        _sendPipe.Writer.Complete();

        ArrayPool<byte>.Shared.Return(_readBuffer);
        ArrayPool<byte>.Shared.Return(_writeBuffer);

        _disposed = true;
    }
}
