using System.IO.Pipelines;

namespace Spangle.Protocols.SRT;

public sealed class SRTReceiverContext : ReceiverContextBase<SRTReceiverContext>,
    IReceiverContext<SRTReceiverContext>
{
    public SRTReceiverContext(string id, PipeReader reader, PipeWriter writer, CancellationToken ct) : base(id, reader, writer, ct)
    {
    }

    public static new SRTReceiverContext CreateInstance(string id, PipeReader reader, PipeWriter writer,
        CancellationToken ct = default)
    {
        return new SRTReceiverContext(id, reader, writer, ct);
    }


    public override bool IsCompleted { get; }
}
