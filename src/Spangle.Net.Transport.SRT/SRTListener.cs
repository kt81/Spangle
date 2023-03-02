using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SrtSharp;
using static SrtSharp.srt;

namespace Spangle.Net.Transport.SRT;

[SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
public sealed class SRTListener : IDisposable
{
    private static readonly StaticFinalizeHandle s_finalizeHandle    = StaticFinalizeHandle.Instance;
    private static readonly int                  s_socketAddressSize = Marshal.SizeOf<sockaddr_in>();

    private readonly IPEndPoint _serverSocketEP;
    private readonly SRTSOCKET  _handle;

    private bool _active;
    private bool _disposed;

    private CancellationTokenSource _tokenSource = new();

    static SRTListener()
    {
        srt_startup();
    }

    public SRTListener(IPEndPoint localEP)
    {
        ArgumentNullException.ThrowIfNull(localEP);
        _serverSocketEP = localEP;
        _handle = srt_create_socket();
        if (_handle == SRT_ERROR)
        {
            throw new SRTException(srt_getlasterror_str());
        }
    }

    public unsafe void Start(int backlog = (int)SocketOptionName.MaxConnections)
    {
        if (_active)
        {
            return;
        }

        IntPtr pSockAddrIn = Marshal.AllocCoTaskMem(s_socketAddressSize);
        try
        {
            ref var addr = ref MemoryMarshal.AsRef<sockaddr_in>(new Span<byte>(pSockAddrIn.ToPointer(), s_socketAddressSize));
            addr.sin_family = AF_INET;
            addr.sin_port = (ushort)IPAddress.HostToNetworkOrder((short)_serverSocketEP.Port);
#pragma warning disable CS0618
            addr.sin_addr = (uint)_serverSocketEP.Address.Address;
#pragma warning restore CS0618
            addr.sin_zero = 0L;

            SWIGTYPE_p_sockaddr socketAddress = new SWIGTYPE_p_sockaddr(pSockAddrIn, false);
            int result = srt_bind(_handle, socketAddress, s_socketAddressSize);
            ThrowHelper.ThrowIfError(result);
            result = srt_listen(_handle, backlog);
            ThrowHelper.ThrowIfError(result);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pSockAddrIn);
        }

        _active = true;
    }

    public async ValueTask<SRTClient> AcceptSRTClientAsync()
    {
        if (!_active)
        {
            throw new InvalidOperationException("SRTListener has not Start-ed.");
        }

        IntPtr pPeerAddr = IntPtr.Zero;
        IntPtr pPeerAddrSize = IntPtr.Zero;
        try
        {
            pPeerAddr = Marshal.AllocCoTaskMem(s_socketAddressSize);
            var peerAddress = new SWIGTYPE_p_sockaddr(pPeerAddr, false);
            pPeerAddrSize = Marshal.AllocCoTaskMem(Marshal.SizeOf<int>());
            var peerAddressSize = new SWIGTYPE_p_int(pPeerAddrSize, false);
            SRTSOCKET peerHandle = srt_accept(_handle, peerAddress, peerAddressSize);
            if (peerHandle == SRT_INVALID_SOCK)
            {
                throw new SRTException(srt_getlasterror_str());
            }

            return new SRTClient(peerHandle, _tokenSource.Token, pPeerAddr, pPeerAddrSize);
        }
        catch
        {
            if (pPeerAddr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pPeerAddr);
            }

            if (pPeerAddrSize != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pPeerAddrSize);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_handle >= 0)
        {
            srt_close(_handle);
            // DO NOT do srt_cleanup() here
        }

        GC.SuppressFinalize(this);
        _disposed = true;
    }

    ~SRTListener()
    {
        Dispose();
    }

    private sealed class StaticFinalizeHandle
    {
        public static StaticFinalizeHandle Instance { get; } = new();

        private StaticFinalizeHandle()
        {
        }

        ~StaticFinalizeHandle()
        {
            srt_cleanup();
        }
    }
}

[SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
public class SRTClient : IDisposable
{
    private bool _disposed = false;

    private readonly SRTSOCKET           _peerHandle;
    private readonly ICollection<IntPtr> _relatedHandlesToBeFree;
    private readonly SRTPipe             _pipe;

    public IDuplexPipe Pipe => _pipe;

    internal SRTClient(SRTSOCKET peerHandle, CancellationToken cancellationToken,
        params IntPtr[] relatedHandlesToBeFree)
    {
        _pipe = new SRTPipe(peerHandle, cancellationToken);
        _peerHandle = peerHandle;
        _relatedHandlesToBeFree = relatedHandlesToBeFree;
    }

    private void ReleaseUnmanagedResources()
    {
        foreach (IntPtr p in _relatedHandlesToBeFree)
        {
            Marshal.FreeCoTaskMem(p);
        }
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
