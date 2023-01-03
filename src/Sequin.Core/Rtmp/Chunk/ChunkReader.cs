using System.Buffers;

namespace Sequin.Rtmp.Chunk;

internal partial class ChunkReader 
{
    private const int ChunkMaxSize = 65536;
    private readonly byte[] _chunkBuffer = ArrayPool<byte>.Shared.Rent(ChunkMaxSize);
    private IChunkProcessor? _currentProcess;
    private static IReadOnlyDictionary<Type, IChunkProcessor> s_Processors;
    private BufferedStream _reader;
    private BufferedStream _writer;
    private int _buffLen = 0;
    private Chunk _chunk = new();

    static ChunkReader()
    {
        var d = new Dictionary<Type, IChunkProcessor>
        {
            [typeof(BasicHeaderProcessor)] = new BasicHeaderProcessor(),
            [typeof(MessageHeaderProcessor)] = new MessageHeaderProcessor(),
            [typeof(BodyParser)] = new BodyParser(),
        };
        s_Processors = d.AsReadOnly();
    }

    public ChunkReader(BufferedStream reader, BufferedStream writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public async ValueTask<Chunk> ReadAsync(CancellationToken ct = default)
    {
        // Initialize all states
        _currentProcess = s_Processors[typeof(BasicHeaderProcessor)];
        Array.Fill(_chunkBuffer, (byte)0);
        
        while (_currentProcess != null)
        {
            ct.ThrowIfCancellationRequested();
            await _currentProcess.ReadAndNext(this, ct);
        }

        return _chunk;
    }

    private IChunkProcessor Next<TProcessor>() where TProcessor : IChunkProcessor
    {
        return _currentProcess = s_Processors[typeof(TProcessor)];
    }
    
    private interface IChunkProcessor
    {
        /// <summary>
        /// Read buffer using current state processor and set next state
        /// </summary>
        /// <returns>Next index of the buffer</returns>
        public ValueTask ReadAndNext(ChunkReader context, CancellationToken ct);
    }
}
