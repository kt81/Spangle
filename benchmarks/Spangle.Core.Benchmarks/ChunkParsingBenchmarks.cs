using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using BenchmarkDotNet.Attributes;
using Spangle.Transport.Rtmp;
using Spangle.Transport.Rtmp.ReadState;

namespace Spangle.Benchmarks;

/// <summary>
/// Measures the RTMP receive core: parsing and assembling an interleaved
/// audio/video chunk stream via <see cref="ReadChunkHeader.TryReadChunk"/>.
/// The session buffer mimics a typical publisher: 4KB video messages every 33ms
/// interleaved with 300B audio messages, split at the negotiated chunk size.
/// </summary>
[MemoryDiagnoser]
public class ChunkParsingBenchmarks
{
    private const int VideoMessageSize = 4096;
    private const int AudioMessageSize = 300;
    private const int MessagePairs = 500;

    [Params(128, 4096)]
    public uint ChunkSize { get; set; }

    private byte[] _session = [];
    private RtmpReceiverContext _context = null!;

    public long SessionBytes => _session.LongLength;

    [GlobalSetup]
    public void Setup()
    {
        var pipe = new Pipe();
        _context = new RtmpReceiverContext(pipe.Reader, pipe.Writer, new IPEndPoint(IPAddress.Loopback, 1), default)
        {
            ChunkSize = ChunkSize,
        };

        var ms = new MemoryStream();
        WriteFmt0Header(ms, csid: 6, timestamp: 0, length: VideoMessageSize, typeId: 9, streamId: 1);
        WriteMessagePayload(ms, csid: 6, VideoMessageSize);
        WriteFmt0Header(ms, csid: 4, timestamp: 0, length: AudioMessageSize, typeId: 8, streamId: 1);
        WriteMessagePayload(ms, csid: 4, AudioMessageSize);

        for (var i = 0; i < MessagePairs; i++)
        {
            WriteFmt1Header(ms, csid: 6, delta: 33, length: VideoMessageSize, typeId: 9);
            WriteMessagePayload(ms, csid: 6, VideoMessageSize);
            WriteFmt1Header(ms, csid: 4, delta: 23, length: AudioMessageSize, typeId: 8);
            WriteMessagePayload(ms, csid: 4, AudioMessageSize);
        }

        _session = ms.ToArray();
    }

    /// <summary>Parses the whole synthetic session (~2.2 MB) once</summary>
    [Benchmark]
    public int ParseSession()
    {
        var buffer = new ReadOnlySequence<byte>(_session);
        var messages = 0;
        while (ReadChunkHeader.TryReadChunk(_context, ref buffer, out var completed))
        {
            if (completed is not null)
            {
                messages++;
            }
        }
        return messages;
    }

    private void WriteMessagePayload(MemoryStream ms, byte csid, int length)
    {
        // First chunk carries min(length, ChunkSize); each continuation is prefixed with a Fmt3 basic header
        var remaining = length;
        var first = true;
        while (remaining > 0)
        {
            if (!first)
            {
                ms.WriteByte((byte)(0xC0 | csid)); // Fmt3 continuation
            }
            int chunk = Math.Min(remaining, (int)ChunkSize);
            for (var i = 0; i < chunk; i++)
            {
                ms.WriteByte((byte)i);
            }
            remaining -= chunk;
            first = false;
        }
    }

    private static void WriteFmt0Header(MemoryStream ms, byte csid, uint timestamp, int length, byte typeId, uint streamId)
    {
        ms.WriteByte(csid); // Fmt0
        WriteUInt24(ms, timestamp);
        WriteUInt24(ms, (uint)length);
        ms.WriteByte(typeId);
        ms.WriteByte((byte)streamId); // little-endian stream id
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0);
    }

    private static void WriteFmt1Header(MemoryStream ms, byte csid, uint delta, int length, byte typeId)
    {
        ms.WriteByte((byte)(0x40 | csid)); // Fmt1
        WriteUInt24(ms, delta);
        WriteUInt24(ms, (uint)length);
        ms.WriteByte(typeId);
    }

    private static void WriteUInt24(MemoryStream ms, uint value)
    {
        ms.WriteByte((byte)(value >> 16));
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)value);
    }
}
