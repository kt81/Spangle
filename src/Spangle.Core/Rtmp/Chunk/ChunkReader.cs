using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Spangle.Rtmp.Chunk;

/// <summary>
/// ChunkReader
/// </summary>
/// <remarks>
/// </remarks>
internal partial class ChunkReader 
{
    private static readonly IReadOnlyDictionary<Type, IChunkProcessor> s_processors;
    
    private IChunkProcessor? _currentProcess;
    
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ILogger    _logger;
    
    private Chunk _chunk;

    static ChunkReader()
    {
        var d = new Dictionary<Type, IChunkProcessor>
        {
            [typeof(BasicHeaderProcessor)] = new BasicHeaderProcessor(),
            [typeof(MessageHeaderProcessor)] = new MessageHeaderProcessor(),
            [typeof(BodyParser)] = new BodyParser(),
        };
        s_processors = d.AsReadOnly();
    }

    public ChunkReader(PipeReader reader, PipeWriter writer, ILogger logger)
    {
        _reader = reader;
        _writer = writer;
        _logger = logger;
    }

    public async ValueTask<Chunk> ReadAsync(CancellationToken ct = default)
    {
        // Initialize all states
        _currentProcess = s_processors[typeof(BasicHeaderProcessor)];
        
        while (_currentProcess != null)
        {
            ct.ThrowIfCancellationRequested();
            await _currentProcess.ReadAndNext(this, ct);
        }

        return _chunk;
    }

    private void Next<TProcessor>() where TProcessor : IChunkProcessor
    {
        _currentProcess = s_processors[typeof(TProcessor)];
        _logger.ZLogTrace("State changed => {0}", typeof(TProcessor).Name);
    }
    
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
        public ValueTask ReadAndNext(ChunkReader context, CancellationToken ct);
    }
}
