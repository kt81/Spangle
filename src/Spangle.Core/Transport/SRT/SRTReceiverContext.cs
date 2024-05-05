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
        Id = ZString.Format("SRT_{0}", client.PeerHandle);
        _client = client;
    }
}
