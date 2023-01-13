using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;
using static Spangle.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Rtmp.Chunk.Processor;

internal abstract class CommandAmf0 : IChunkProcessor
{
    public static async ValueTask PerformProcess(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;
        (ReadOnlySequence<byte> buff, _) =
            await reader.ReadExactlyAsync((int)context.MessageHeader.Length, ct);
        IChunkProcessor.EnsureValidProtocolControlMessage(context);

        // Parse command
        string command = ParseString(ref buff);
        double transactionId = ParseNumber(ref buff);
        IReadOnlyDictionary<string, object> commandObject = ParseObject(ref buff);
        IReadOnlyDictionary<string, object>? optionalArguments = null;
        if (0 < buff.Length)
        {
            optionalArguments = ParseObject(ref buff);
        }

        reader.AdvanceTo(buff.End);

        // Dispatch RPC
        switch (command)
        {
            case NetConnection.Commands.Connect:
                context.NetConnection.Value.Connect(transactionId, commandObject, optionalArguments);
                break;
            default:
                throw new NotImplementedException($"NetConnection.{command} is not implemented.");
        }

        await context.Writer.FlushAsync(ct);

        context.SetNext<BasicHeaderProcessor>();
    }
}
