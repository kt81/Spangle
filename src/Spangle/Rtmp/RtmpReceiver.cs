// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.Handshake;

namespace Spangle.Rtmp;

public class RtmpReceiver
{
    private readonly BufferedStream _reader;
    private readonly BufferedStream _writer;

    public RtmpReceiver(Stream readerWriter): this(readerWriter, readerWriter)
    {
    }
    public RtmpReceiver(Stream reader, Stream writer)
    {
        if (!reader.CanRead)
        {
            throw new ArgumentException(null, nameof(reader));
        }
        if (!writer.CanWrite)
        {
            throw new ArgumentException(null, nameof(writer));
        }
        
        // Separate each stream as a separate BufferedStream, even if they are identical
        _reader = EnsureBufferedStream(reader);
        _writer = EnsureBufferedStream(writer);
    }

    private static BufferedStream EnsureBufferedStream(Stream stream)
    {
        if (stream is BufferedStream bf)
        {
            return bf;
        }

        return new BufferedStream(stream);
    }

    public async ValueTask BeginReadAsync(CancellationToken ct = default)
    {
        var handshake = new HandshakeHandler(_reader, _writer);
        await handshake.DoHandshakeAsync();

        var chunk = new ChunkReader(_reader, _writer);
        await chunk.ReadAsync(ct);

    }
}
