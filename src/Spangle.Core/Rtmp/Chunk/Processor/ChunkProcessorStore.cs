namespace Spangle.Rtmp.Chunk.Processor;

internal static class ChunkProcessorStore<TProcessor> where TProcessor : IChunkProcessor
{
    // ReSharper disable once StaticMemberInGenericType
    public static readonly RtmpReceiverContext.Processor Process;

    static ChunkProcessorStore() => Process = TProcessor.PerformProcess;
}
