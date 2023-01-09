namespace Spangle.Rtmp.Chunk;

internal partial class ChunkReader
{
    /// <summary>
    /// ChunkProcessor interface for each chunk parts
    /// </summary>
    /// <remarks>
    /// The implemented classes MUST NOT directly map a structure to the pipe buffer. Consume used buffer every time in read.
    /// </remarks>
    private interface IChunkProcessor
    {
        /// <summary>
        /// Read buffer using current state processor and set next state
        /// </summary>
        /// <returns>Next index of the buffer</returns>
        public ValueTask ReadAndNext(Rtmp.Chunk.ChunkReader context, CancellationToken ct);
    }
}
