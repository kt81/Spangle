namespace Spangle.Rtmp.Chunk;

internal partial class ChunkReader
{
    private class BodyParser : IChunkProcessor
    {
        public ValueTask ReadAndNext(ChunkReader context, CancellationToken ct)
        {
            switch (context._chunk.MessageHeader.TypeId)
            {
                case MessageType.CommandAmf0:
                    // context.Next<>();
                    throw new NotImplementedException();
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal static class NetConnectionCommands
{
    public const string Connect      = "connect";
    public const string Call         = "call";
    public const string Close        = "close";
    public const string CreateStream = "createStream";
}
