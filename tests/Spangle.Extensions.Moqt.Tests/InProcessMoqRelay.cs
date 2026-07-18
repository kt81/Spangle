using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Threading.Channels;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Messages;
using Spangle.Net.Moqt.Wire;
using Spangle.Net.Transport.Quic;
using Spangle.Net.Transport.Quic.InMemory;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// A scriptable MOQT relay over the in-memory transport — the peer the
/// <see cref="MoqSender"/>/<see cref="MoqRelayConnection"/> state machine is exercised against.
/// It accepts connections, performs the SETUP handshake, answers PUBLISH_NAMESPACE (with
/// REQUEST_OK, or REQUEST_ERROR when scripted via <see cref="FailNextAnnounce"/>), can SUBSCRIBE
/// to the publisher's tracks like a real relay would, and records every object it receives.
/// One accept loop per connection routes every incoming stream, so request streams and subgroup
/// streams never race for the same acceptor.
/// </summary>
internal sealed class InProcessMoqRelay : IAsyncDisposable
{
    private static readonly SslApplicationProtocol Alpn = new(MoqtConstants.Alpn);

    private readonly IQuicServer _server;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _acceptLoop;
    private readonly Channel<RelaySession> _sessions = Channel.CreateUnbounded<RelaySession>();
    private readonly ConcurrentQueue<bool> _scriptedAnnounceFailures = new();
    private readonly List<RelaySession> _handedOut = [];

    private InProcessMoqRelay(IQuicServer server)
    {
        _server = server;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    /// <summary>Starts listening on <paramref name="endPoint"/> of <paramref name="transport"/>.</summary>
    internal static async Task<InProcessMoqRelay> StartAsync(InMemoryQuicTransport transport, IPEndPoint endPoint,
        CancellationToken ct)
    {
        IQuicServer server = await transport.ListenAsync(new QuicServerOptions
        {
            ListenEndPoint = endPoint,
            ApplicationProtocols = [Alpn],
        }, ct);
        return new InProcessMoqRelay(server);
    }

    /// <summary>The next PUBLISH_NAMESPACE this relay receives is answered with REQUEST_ERROR.</summary>
    internal void FailNextAnnounce() => _scriptedAnnounceFailures.Enqueue(true);

    /// <summary>Waits for the next publisher connection to complete its SETUP handshake.</summary>
    internal async Task<RelaySession> AcceptSessionAsync(CancellationToken ct)
    {
        RelaySession session = await _sessions.Reader.ReadAsync(ct);
        _handedOut.Add(session);
        return session;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IQuicConnection connection = await _server.AcceptConnectionAsync(ct);
                _ = Task.Run(() => HandleConnectionAsync(connection, ct), CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // the relay is shutting down, or the server was disposed under it; either ends the loop
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The session is owned by whoever dequeues it; DisposeAsync drains the queue and disposes every entry.")]
    private async Task HandleConnectionAsync(IQuicConnection connection, CancellationToken ct)
    {
        MoqSession session;
        try
        {
            session = await MoqSession.AcceptAsync(connection, new SetupMessage(), ct);
        }
        catch (Exception)
        {
            // the publisher hung up mid-SETUP; that connection never becomes a session
            await connection.DisposeAsync();
            return;
        }

        var relaySession = new RelaySession(connection, session, NextAnnounceVerdict, ct);
        if (!_sessions.Writer.TryWrite(relaySession))
        {
            await relaySession.DisposeAsync(); // the relay is already shutting down
        }
    }

    // True = answer REQUEST_OK. The failure script is relay-wide, consumed one entry per announce.
    private bool NextAnnounceVerdict() => !_scriptedAnnounceFailures.TryDequeue(out _);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _server.DisposeAsync();
        try
        {
#pragma warning disable VSTHRD003 // our own accept loop, ended by the cancellation above
            await _acceptLoop;
#pragma warning restore VSTHRD003
        }
        catch (Exception)
        {
            // the loop ends by cancellation or by the server's disposal; both are expected here
        }

        _sessions.Writer.TryComplete();
        while (_sessions.Reader.TryRead(out RelaySession? pending))
        {
            _handedOut.Add(pending);
        }

        foreach (RelaySession session in _handedOut)
        {
            await session.DisposeAsync();
        }

        _lifetime.Dispose();
    }
}

/// <summary>
/// One publisher connection as the relay sees it: the SETUP is done, the announce verdict is
/// observable, tracks can be subscribed to, and every received object is recorded with the Track
/// Alias its subgroup stream carried. <see cref="KillAsync"/> is the scripted relay death — the
/// session is torn down under the publisher, which is how a dead relay announces itself to it.
/// </summary>
internal sealed class RelaySession : IAsyncDisposable
{
    private readonly IQuicConnection _connection;
    private readonly MoqSession _session;
    private readonly Task _routeLoop;
    private readonly TaskCompletionSource<bool> _announceAnswered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<(ulong Alias, MoqObject Object)> _objects =
        Channel.CreateUnbounded<(ulong, MoqObject)>();
    private ulong _nextRequestId = 1; // server-initiated request IDs are odd (draft-18 §10.1)

    internal RelaySession(IQuicConnection connection, MoqSession session, Func<bool> announceVerdict,
        CancellationToken ct)
    {
        _connection = connection;
        _session = session;
        _routeLoop = Task.Run(() => RouteLoopAsync(announceVerdict, ct), CancellationToken.None);
    }

    /// <summary>Every object received on this connection, tagged with its subgroup's Track Alias.</summary>
    internal ChannelReader<(ulong Alias, MoqObject Object)> Objects => _objects.Reader;

    /// <summary>
    /// Completes when this connection's PUBLISH_NAMESPACE has been answered: true for REQUEST_OK,
    /// false for a scripted REQUEST_ERROR.
    /// </summary>
    internal Task<bool> WaitForAnnounceAsync(CancellationToken ct)
    {
        // Our own TCS, completed by our own route loop — not the foreign-task hazard VSTHRD003 means.
#pragma warning disable VSTHRD003
        return _announceAnswered.Task.WaitAsync(ct);
#pragma warning restore VSTHRD003
    }

    /// <summary>
    /// Subscribes to <paramref name="track"/> the way a relay with a viewer would: SUBSCRIBE on a
    /// fresh request stream, SUBSCRIBE_OK awaited on the same stream. Returns the assigned alias.
    /// </summary>
    internal async Task<ulong> SubscribeAsync(FullTrackName track, CancellationToken ct)
    {
        IQuicStream request = await _connection.OpenStreamAsync(QuicStreamDirection.Bidirectional, ct);
        ulong requestId = _nextRequestId;
        _nextRequestId += 2;

        // The mandatory SUBSCRIPTION_FILTER (draft-18 §10.7), set to Largest Object (live edge).
        var filterValue = new ArrayBufferWriter<byte>();
        new MoqWriter(filterValue).WriteVarInt(2);
        MoqKeyValuePair filter = MoqKeyValuePair.FromBytes(0x21, filterValue.WrittenSpan);

        var payload = new ArrayBufferWriter<byte>();
        new SubscribeMessage(requestId, track, [filter]).EncodePayload(new MoqWriter(payload));
        var frame = new ArrayBufferWriter<byte>();
        ControlMessage.Write(frame, MoqControlMessageType.Subscribe, payload.WrittenSpan);
        await request.WriteAsync(frame.WrittenMemory, completeWrites: false, ct);

        (ulong type, byte[] okPayload) = await ControlMessage.ReadAsync(request, ct);
        if (type != MoqControlMessageType.SubscribeOk)
        {
            throw new MoqProtocolException($"Expected SUBSCRIBE_OK after SUBSCRIBE, got 0x{type:X}.");
        }

        return SubscribeOkMessage.DecodePayload(okPayload).TrackAlias;
    }

    /// <summary>
    /// Kills the relay side of the connection. The publisher's demux loop ends with it, which is
    /// the only way it learns the relay died.
    /// </summary>
    internal ValueTask KillAsync() => _session.DisposeAsync();

    private async Task RouteLoopAsync(Func<bool> announceVerdict, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                MoqIncomingStream incoming = await MoqStreamRouter.AcceptAsync(_connection, ct);
                switch (incoming)
                {
                    case MoqRequestStream { MessageType: MoqControlMessageType.PublishNamespace } announce:
                        await AnswerAnnounceAsync(announce, announceVerdict(), ct);
                        break;
                    case MoqSubgroupStream subgroup:
                        _ = Task.Run(() => DrainSubgroupAsync(subgroup, ct), CancellationToken.None);
                        break;
                    default:
                        // Not part of these tests, but the stream holds inbound-stream credit.
                        await incoming.Stream.DisposeAsync();
                        break;
                }
            }
        }
        catch (Exception)
        {
            // the publisher hung up, or the relay is shutting down; either ends this session
        }
        finally
        {
            _objects.Writer.TryComplete();
        }
    }

    private async Task AnswerAnnounceAsync(MoqRequestStream announce, bool ok, CancellationToken ct)
    {
        var payload = new ArrayBufferWriter<byte>();
        ulong type;
        if (ok)
        {
            new RequestOkMessage().EncodePayload(new MoqWriter(payload));
            type = MoqControlMessageType.RequestOk;
        }
        else
        {
            new RequestErrorMessage(errorCode: 1, retryInterval: 0, "scripted failure")
                .EncodePayload(new MoqWriter(payload));
            type = MoqControlMessageType.RequestError;
        }

        var frame = new ArrayBufferWriter<byte>();
        ControlMessage.Write(frame, type, payload.WrittenSpan);
        // The announcement stream stays open for the life of the announcement; the publisher holds it.
        await announce.Stream.WriteAsync(frame.WrittenMemory, completeWrites: false, ct);
        _announceAnswered.TrySetResult(ok);
    }

    private async Task DrainSubgroupAsync(MoqSubgroupStream subgroup, CancellationToken ct)
    {
        try
        {
            ulong alias = subgroup.Reader.Header.TrackAlias;
            while (await subgroup.Reader.ReadObjectAsync(ct) is { } moqObject)
            {
                _objects.Writer.TryWrite((alias, moqObject));
            }
        }
        catch (Exception)
        {
            // a group abandoned mid-stream is part of what these tests exercise
        }
        finally
        {
            await subgroup.Stream.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await KillAsync();
        try
        {
#pragma warning disable VSTHRD003 // our own route loop, ended by the session's death above
            await _routeLoop;
#pragma warning restore VSTHRD003
        }
        catch (Exception)
        {
            // the loop ends when the connection dies, which KillAsync just arranged
        }

        await _connection.DisposeAsync();
    }
}
