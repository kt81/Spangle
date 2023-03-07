using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SrtSharp;
using static SrtSharp.srt;

namespace Spangle.Net.Transport.SRT;

[SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
public sealed class SRTListener : IDisposable
{
    // [SuppressMessage("ReSharper", "InconsistentNaming")] private const int SRT_ERROR = -1;

    // ReSharper disable once UnusedMember.Local
    private static readonly StaticFinalizeHandle s_finalizeHandle = StaticFinalizeHandle.Instance;

    private static readonly int s_socketAddressSize = Marshal.SizeOf<sockaddr_in>();

    // private static readonly IntPtr          s_pFalsy            = Marshal.AllocCoTaskMem(sizeof(int));
    // private static readonly SWIGTYPE_p_void s_falsyInt;
    // private static readonly IntPtr          s_pEventsForAccept = Marshal.AllocCoTaskMem(sizeof(int));
    // private static readonly SWIGTYPE_p_int  s_eventsForAccept;

    private readonly IPEndPoint _serverSocketEP;
    private readonly SRTSOCKET  _handle;

    private bool _active;
    private bool _disposed;

    private readonly CancellationTokenSource _tokenSource = new();

    static SRTListener()
    {
        srt_startup();
        // s_falsyInt = new SWIGTYPE_p_void(s_pFalsy, false);
        // s_eventsForAccept = new SWIGTYPE_p_int(s_pEventsForAccept, false);
        // unsafe
        // {
        //     // map values for pointer
        //     ref int falsy = ref MemoryMarshal.AsRef<int>(new Span<byte>(s_pFalsy.ToPointer(), sizeof(int)));
        //     falsy = 0;
        //     ref int events = ref MemoryMarshal.AsRef<int>(new Span<byte>(s_pEventsForAccept.ToPointer(), sizeof(int)));
        //     // ReSharper disable once BitwiseOperatorOnEnumWithoutFlags
        //     events = (int)(SRT_EPOLL_OPT.SRT_EPOLL_IN | SRT_EPOLL_OPT.SRT_EPOLL_ERR);
        // }
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

        // non-blocking? 🤔
        // if (SRT_ERROR == srt_setsockflag(_handle, SRT_SOCKOPT.SRTO_RCVSYN, s_falsyInt, sizeof(int))
        //     || SRT_ERROR == srt_setsockflag(_handle, SRT_SOCKOPT.SRTO_SNDSYN, s_falsyInt, sizeof(int)))
        // {
        //     throw new SRTException(srt_getlasterror_str());
        // }
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
            ref var addr =
                ref MemoryMarshal.AsRef<sockaddr_in>(new Span<byte>(pSockAddrIn.ToPointer(), s_socketAddressSize));
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

    public ValueTask<SRTClient> AcceptSRTClientAsync()
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

            var ep = ConvertToEndPoint(pPeerAddr);

            return ValueTask.FromResult(new SRTClient(peerHandle, ep, _tokenSource.Token));
        }
        finally
        {
            if (pPeerAddr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pPeerAddr);
            }

            if (pPeerAddrSize != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pPeerAddrSize);
            }
        }
    }

    private static unsafe IPEndPoint ConvertToEndPoint(IntPtr pPeerAddr)
    {
        ref readonly sockaddr_in addr =
            ref MemoryMarshal.AsRef<sockaddr_in>(new ReadOnlySpan<byte>(pPeerAddr.ToPointer(), s_socketAddressSize));
        return new IPEndPoint(addr.sin_addr, addr.sin_port);
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
            // Marshal.FreeCoTaskMem(s_pFalsy);
            // Marshal.FreeCoTaskMem(s_pEventsForAccept);
            srt_cleanup();
        }
    }
}
