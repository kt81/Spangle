using System.Net;
using Cysharp.Text;
using Spangle.Net.Transport.SRT;

namespace Spangle.Transport.SRT;

public sealed class SRTReceiverContext : ReceiverContextBase<SRTReceiverContext>
{
    private readonly SRTClient _client;

    public override string Id { get; }
    public override bool IsCompleted => _client.IsCompleted;
    public override ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource) => throw new NotImplementedException();

    public override EndPoint EndPoint => _client.RemoteEndPoint;

    public SRTReceiverContext(SRTClient client, CancellationToken ct)
        : base(client.Pipe.Input, client.Pipe.Output, ct)
    {
        // The sender's streamid is the SRT counterpart of an RTMP stream key;
        // fall back to the socket handle only for id-less senders.
        Id = client.StreamId.Length > 0
            ? client.StreamId
            : ZString.Format("SRT_{0}", client.PeerHandle);
        _client = client;
    }
}
