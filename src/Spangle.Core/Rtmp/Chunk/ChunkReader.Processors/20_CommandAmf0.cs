using System.Buffers;
using Spangle.IO;

using static Spangle.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Rtmp.Chunk;

internal partial class ChunkReader
{
    private class CommandAmf0 : IChunkProcessor
    {
        public async ValueTask ReadAndNext(ChunkReader context, CancellationToken ct)
        {
            var reader = context._reader;
            (ReadOnlySequence<byte> buff, _) =
                await reader.ReadExactlyAsync((int)context._chunk.MessageHeader.Length, ct);
            EnsureValidProtocolControlMessage(context);

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
                    context._netConnection.Connect(transactionId, commandObject, optionalArguments);
                    break;
                default:
                    throw new NotImplementedException($"NetConnection.{command} is not implemented.");
            }

            await context._writer.FlushAsync(ct);

            context.Next<BasicHeaderProcessor>();
        }
    }
}
