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
    /// <summary>
    /// The stream ID which indicates Control Stream
    /// </summary>
    private const uint ControlStreamId = 0;

    /// <summary>
    /// The chunk stream ID which indicates Control Chunk Stream
    /// </summary>
    private const uint ControlChunkStreamId = 2;

    private static readonly IReadOnlyDictionary<Type, IChunkProcessor> s_processors;
    private Dictionary<uint, Chunk> _chunks = new();

    private IChunkProcessor? _currentProcess;

    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ILogger _logger;

    private Chunk _chunk;

    private uint _maxChunkSize = 128;

    static ChunkReader()
    {
        var d = new Dictionary<Type, IChunkProcessor>
        {
            [typeof(BasicHeaderProcessor)] = new BasicHeaderProcessor(),
            [typeof(MessageHeaderProcessor)] = new MessageHeaderProcessor(),
            [typeof(SetChunkSize)] = new SetChunkSize(), // MessageType 1
            [typeof(CommandAmf0)] = new CommandAmf0(), // MessageType 20
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

    private static void EnsureValidProtocolControlMessage(ChunkReader context)
    {
        if (context._chunk.MessageHeader.StreamId == ControlStreamId /* &&
            context._chunk.BasicHeader.ChunkStreamId == ControlChunkStreamId */)
        {
            return;
        }

        context._logger.ZLogError("Invalid streamId({0}) or chunkStreamId({1}) for Protocol Control Message",
            context._chunk.MessageHeader.StreamId, context._chunk.BasicHeader.ChunkStreamId);
        throw new Exception();
    }

    private static void DispatchNextByMessageHeader(ChunkReader context)
    {
        switch (context._chunk.MessageHeader.TypeId)
        {
            case MessageType.SetChunkSize:
                context.Next<SetChunkSize>();
                break;
            case MessageType.CommandAmf0:
                context.Next<CommandAmf0>();
                break;
            default:
                throw new NotImplementedException($"The processor of [{context._chunk.MessageHeader.TypeId}] is not implemented.");
        }
    }
}
