using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using SrtSharp;

namespace Spangle.Net.Transport.SRT;

[SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
public class SRTClient : IDisposable
{
    private bool _disposed;

    private readonly SRTSOCKET _peerHandle;
    private readonly SRTPipe   _pipe;
    public EndPoint RemoteEndPoint { get; }

    public IDuplexPipe Pipe => _pipe;

    internal SRTClient(SRTSOCKET peerHandle, EndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        _pipe = new SRTPipe(peerHandle, cancellationToken);
        _peerHandle = peerHandle;
        RemoteEndPoint = remoteEndPoint;
    }

    private void ReleaseUnmanagedResources()
    {
        srt.srt_close(_peerHandle);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        ReleaseUnmanagedResources();
        if (disposing)
        {
            _pipe.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~SRTClient() => Dispose(false);
}
