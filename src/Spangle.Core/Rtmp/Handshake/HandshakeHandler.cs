using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Spangle.IO;
using Spangle.IO.Interop;
using Spangle.Rtmp.Logging;
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
    private static readonly ILogger<HandshakeHandler> s_logger = SpangleLogManager.GetLogger<HandshakeHandler>();

    private static readonly int s_sizeOfC0S0 = Marshal.SizeOf<C0S0>();
    private static readonly int s_sizeOfC1S1 = Marshal.SizeOf<C1S1>();
    private static readonly int s_sizeOfC2S2 = Marshal.SizeOf<C2S2>();

    [SuppressMessage("ReSharper", "JoinDeclarationAndInitializer")]
    public static async ValueTask DoHandshakeAsync(RtmpReceiverContext receiverContext)
    {
        ReadOnlySequence<byte> buff;
        ReadResult res;

        var reader = receiverContext.Reader;
        var writer = receiverContext.Writer;
        var ct = receiverContext.CancellationToken;

        // Deal C0S0
        (buff, res) = await reader.ReadExactlyAsync(s_sizeOfC0S0, ct);
        VerifyC0(buff);
        reader.AdvanceTo(buff.End);
        s_logger.ZLogTrace("AdvanceTo {0} => {1}", res.Buffer.Start.GetInteger(), buff.End.GetInteger());
        var s0 = new C0S0(RtmpVersion.Rtmp3);
        SendMessage(writer, ref s0);

        // Deal C1S1
        var tC1 = reader.ReadExactlyAsync(s_sizeOfC1S1, ct);
        var s1 = new C1S1(NowMs());
        SendMessage(writer, ref s1);
        ((buff, res), _) = await ValueTaskEx.WhenAll(tC1, writer.FlushAsync(ct));
        ChangeState(receiverContext, HandshakeState.VersionSent);

        // Deal C2S2
        VerifyC1AndSendS2(writer, buff);
        await writer.FlushAsync(ct);
        ChangeState(receiverContext, HandshakeState.AckSent);
        reader.AdvanceTo(buff.End);
        s_logger.ZLogTrace("AdvanceTo {0} => {1}", res.Buffer.Start.GetInteger(), buff.End.GetInteger());
        (buff, res) = await reader.ReadExactlyAsync(s_sizeOfC2S2, ct);
        VerifyC2(buff, ref s1);

        // Mark the buffer up to end of C2 has been consumed
        reader.AdvanceTo(buff.End);
        s_logger.ZLogTrace("AdvanceTo {0} => {1}", res.Buffer.Start.GetInteger(), buff.End.GetInteger());

        // Done!!
        ChangeState(receiverContext, HandshakeState.HandshakeDone);
    }

    private static void VerifyC0(in ReadOnlySequence<byte> buff)
    {
        ref readonly var c0 = ref BufferMarshal.AsRefOrCopy<C0S0>(buff);
        if (c0.RtmpVersion == RtmpVersion.Rtmp3)
        {
            return;
        }

        s_logger.ZLogError("Unsupported rtmp version: {0}", c0.RtmpVersion);
        throw new Exception();
    }

    private static void VerifyC1AndSendS2(PipeWriter writer, in ReadOnlySequence<byte> buff)
    {
        ref readonly var c1 = ref BufferMarshal.AsRefOrCopy<C1S1>(buff);
        var s2 = new C2S2(in c1, NowMs());
        SendMessage(writer, ref s2);
    }

    private static void VerifyC2(in ReadOnlySequence<byte> buff, ref C1S1 s1)
    {
        ref readonly var c2 = ref BufferMarshal.AsRefOrCopy<C2S2>(buff);
        if (c2.RandomEchoSpan.SequenceEqual(s1.RandomSpan))
        {
            return;
        }

        s_logger.ZLogError("RandomEcho is mismatched. Orig:{0}... Echo:{1}...",
            BitConverter.ToString(s1.RandomSpan[..10].ToArray()),
            BitConverter.ToString(c2.RandomEchoSpan[..10].ToArray()));
        throw new Exception("Not match");
    }

    private static void SendMessage<T>(PipeWriter writer, ref T message) where T : unmanaged
    {
        int length = Marshal.SizeOf(message);
        var buff = writer.GetMemory(length);

        MemoryMarshal.Cast<T, byte>(MemoryMarshal.CreateSpan(ref message, 1)).CopyTo(buff.Span);
        writer.Advance(length);
    }

    private static uint NowMs()
    {
        return (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ChangeState(RtmpReceiverContext receiverContext, HandshakeState newState)
    {
        s_logger.ZLogTrace("HandshakeState changed => {0}", newState);
        receiverContext.HandshakeState = newState;
    }
}
