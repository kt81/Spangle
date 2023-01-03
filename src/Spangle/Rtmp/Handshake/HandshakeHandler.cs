using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spangle.Rtmp.Handshake;

/// <summary>
/// Handshake Handler
/// </summary>
/*
 +-------------+                            +-------------+
 |   Client    |        TCP/IP Network      |    Server   |
 +-------------+             |              +-------------+
        |                    |                     |
 Uninitialized               |               Uninitialized
        |           C0       |                     |
        |------------------->|         C0          |
        |                    |-------------------->|
        |           C1       |                     |
        |------------------->|         S0          |
        |                    |<--------------------|
        |                    |         S1          |
  Version sent               |<--------------------|
        |           S0       |                     |
        |<-------------------|                     |
        |           S1       |                     |
        |<-------------------|                Version sent
        |                    |         C1          |
        |                    |-------------------->|
        |           C2       |                     |
        |------------------->|         S2          |
        |                    |<--------------------|
     Ack sent                |                  Ack Sent
        |           S2       |                     |
        |<-------------------|                     |
        |                    |         C2          |
        |                    |-------------------->|
  Handshake Done             |              Handshake Done
        |                    |                     |

           Pictorial Representation of Handshake
 */
internal class HandshakeHandler
{
    private HandshakeState _state = HandshakeState.Uninitialized;
    private readonly BufferedStream _reader;
    private readonly BufferedStream _writer;
    private readonly byte[] _readBuffer = new byte[2000];
    private readonly byte[] _writeBuffer = new byte[2000];

    private readonly CancellationToken _cancellationToken;

    private static readonly C0S0 s_s0 = new(RtmpVersion.Rtmp3);

    public HandshakeState State
    {
        get => _state;
        set
        {
            Debug.WriteLine($"State Changed: {_state} -> {value}");
            _state = value;
        }
    }

    public HandshakeHandler(BufferedStream reader, BufferedStream writer, CancellationToken cancellationToken = default)
    {
        if (!reader.CanRead)
        {
            throw new ArgumentException("Must be readable", nameof(reader));
        }

        if (!writer.CanWrite)
        {
            throw new ArgumentException("Must be writable", nameof(writer));
        }
        _reader = reader;
        _writer = writer;
        _cancellationToken = cancellationToken;
    }

    public async ValueTask DoHandshakeAsync()
    {
        await ReadC0();
        await SendS0();
        var tC1 = ReadC1();
        var tS1 = SendS1();

        var c1 = await tC1;
        var s1 = await tS1;
        await EnsureSent();
        State = HandshakeState.VersionSent;

        var tS2 = SendS2(c1);
        var tC2 = ReadC2(s1);

        await tS2;
        await EnsureSent();
        State = HandshakeState.AckSent;

        await tC2;
        State = HandshakeState.HandshakeDone;
    }

    private Task EnsureSent()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return _writer.FlushAsync(_cancellationToken);
    }

    private async ValueTask<C0S0> ReadC0()
    {
        var c0 = await ReadMessage<C0S0>();
        if (c0.RtmpVersion != RtmpVersion.Rtmp3)
        {
            throw new Exception();
        }

        return c0;
    }

    private async ValueTask<C1S1> ReadC1()
    {
        return await ReadMessage<C1S1>();
    }

    private async ValueTask<C2S2> ReadC2(C1S1 s1)
    {
        var c2 = await ReadMessage<C2S2>();
        if (!c2.RandomEchoSpan.SequenceEqual(s1.RandomSpan))
        {
            throw new Exception();
        }

        return c2;
    }

    private async ValueTask SendS0()
    {
        await SendMessage(s_s0);
    }

    private async ValueTask<C1S1> SendS1()
    {
        var s1 = new C1S1(NowMs());
        await SendMessage(s1);
        return s1;
    }

    private async ValueTask<C2S2> SendS2(C1S1 c1)
    {
        var s2 = new C2S2(c1, NowMs());
        await SendMessage(s2);
        return s2;
    }

    private async ValueTask<T> ReadMessage<T>() where T : struct
    {
        var messageLen = Marshal.SizeOf<T>();
        var pos = 0;
        do
        {
            _cancellationToken.ThrowIfCancellationRequested();
            pos = await _reader.ReadAsync(_readBuffer.AsMemory(pos, messageLen - pos), _cancellationToken);
        } while (pos < messageLen);
        return MemoryMarshal.AsRef<T>(_readBuffer.AsSpan()[..messageLen]);
    }

    private async ValueTask SendMessage<T>(T message) where T : struct
    {
        _cancellationToken.ThrowIfCancellationRequested();
        
        var length = Marshal.SizeOf(message);
        var ptr = Marshal.AllocHGlobal(length);

        Marshal.StructureToPtr(message, ptr, true);
        Marshal.Copy(ptr, _writeBuffer, 0, length);
        Marshal.FreeHGlobal(ptr);

        await _writer.WriteAsync(_writeBuffer.AsMemory(0, length), _cancellationToken);
    }

    private static uint NowMs()
    {
        return (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public enum HandshakeState
    {
        Uninitialized = 0,
        VersionSent,
        AckSent,
        HandshakeDone,
    }
}
