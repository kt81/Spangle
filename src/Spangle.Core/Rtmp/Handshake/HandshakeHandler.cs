using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Spangle.IO;
using ValueTaskSupplement;
using ZLogger;

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
    private          HandshakeState _state = HandshakeState.Uninitialized;
    private readonly PipeReader     _reader;
    private readonly PipeWriter     _writer;
    private readonly ILogger        _logger;

    private static readonly int s_sizeOfC0S0 = Marshal.SizeOf<C0S0>();
    private static readonly int s_sizeOfC1S1 = Marshal.SizeOf<C1S1>();
    private static readonly int s_sizeOfC2S2 = Marshal.SizeOf<C2S2>();

    private HandshakeState State
    {
        // get => _state;
        set
        {
            _logger.ZLogDebug("State Changed: {0} -> {1}", _state, value);
            _state = value;
        }
    }

    public HandshakeHandler(PipeReader reader, PipeWriter writer, ILogger logger) 
    {
        _reader = reader;
        _writer = writer;
        _logger = logger;
    }

    public async ValueTask DoHandshakeAsync(CancellationToken ct)
    {
        ReadOnlySequence<byte> buff;
        ReadResult res;
        
        // Deal C0S0
        (buff, res) = await _reader.ReadExactlyAsync(s_sizeOfC0S0, ct);
        VerifyC0(buff);
        _reader.AdvanceTo(buff.End);
        _logger.ZLogTrace("AdvanceTo {0} => {1}", res.Buffer.Start.GetInteger(), buff.End.GetInteger());
        var s0 = new C0S0(RtmpVersion.Rtmp3);
        SendMessage(ref s0);
        
        // Deal C1S1
        var tC1 =  _reader.ReadExactlyAsync(s_sizeOfC1S1, ct);
        var s1 = new C1S1(NowMs());
        SendMessage(ref s1);
        ((buff, res), _) = await ValueTaskEx.WhenAll(tC1, _writer.FlushAsync(ct));
        State = HandshakeState.VersionSent;
        
        // Deal C2S2
        VerifyC1AndSendS2(buff);
        await _writer.FlushAsync(ct);
        State = HandshakeState.AckSent;
        _reader.AdvanceTo(buff.End);
        _logger.ZLogTrace("AdvanceTo {0} => {1}", res.Buffer.Start.GetInteger(), buff.End.GetInteger());
        (buff, res) = await _reader.ReadExactlyAsync(s_sizeOfC2S2, ct);
        VerifyC2(buff, ref s1);
        
        // Mark the buffer up to end of C2 has been consumed
        _reader.AdvanceTo(buff.End);
        _logger.ZLogTrace("AdvanceTo {0} => {1}", res.Buffer.Start.GetInteger(), buff.End.GetInteger());
        
        // Done!!
        State = HandshakeState.HandshakeDone;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref readonly TMessage AsRefOrCopy<TMessage>(in ReadOnlySequence<byte> buff) where TMessage : unmanaged
    {
        if (buff.IsSingleSegment)
        {
            _logger.ZLogTrace("{0} is SingleSegment", typeof(TMessage));
            return ref MemoryMarshal.AsRef<TMessage>(buff.FirstSpan);
        }

        _logger.ZLogTrace("{0} has multiple segments", typeof(TMessage));
        Span<byte> copied = new byte[(int)buff.Length];
        buff.CopyTo(copied);
        return ref MemoryMarshal.AsRef<TMessage>(copied);
    }

    private void VerifyC0(in ReadOnlySequence<byte> buff)
    {
        ref readonly var c0 = ref AsRefOrCopy<C0S0>(buff);
        if (c0.RtmpVersion != RtmpVersion.Rtmp3)
        {
            throw new Exception();
        }
    }

    private void VerifyC1AndSendS2(in ReadOnlySequence<byte> buff)
    {
        ref readonly var c1 = ref AsRefOrCopy<C1S1>(buff);
        var s2 = new C2S2(in c1, NowMs());
        SendMessage(ref s2);
    }

    private void VerifyC2(in ReadOnlySequence<byte> buff, ref C1S1 s1)
    {
        ref readonly var c2 = ref AsRefOrCopy<C2S2>(buff);
        if (c2.RandomEchoSpan.SequenceEqual(s1.RandomSpan))
        {
            return;
        }

        _logger.ZLogError("RandomEcho is mismatched. Orig:{0}... Echo:{1}...", 
            BitConverter.ToString(s1.RandomSpan[..10].ToArray()),
            BitConverter.ToString(c2.RandomEchoSpan[..10].ToArray()));
        throw new Exception("Not match");
    }

    private void SendMessage<T>(ref T message) where T : unmanaged
    {
        var length = Marshal.SizeOf(message);
        var buff = _writer.GetMemory(length);

        MemoryMarshal.Cast<T, byte>(MemoryMarshal.CreateSpan(ref message, 1)).CopyTo(buff.Span);
        _writer.Advance(length);
    }

    private static uint NowMs()
    {
        return (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private enum HandshakeState
    {
        Uninitialized = 0,
        VersionSent,
        AckSent,
        HandshakeDone,
    }
}
