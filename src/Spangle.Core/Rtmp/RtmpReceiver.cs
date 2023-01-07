// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.Handshake;
using ValueTaskSupplement;
using ZLogger;

namespace Spangle.Rtmp;

public class RtmpReceiver : IAsyncDisposable
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger        _logger;

    public RtmpReceiver(IDuplexPipe duplexPipe, ILoggerFactory? loggerFactory = null): this(duplexPipe.Input, duplexPipe.Output, loggerFactory)
    {
    }

    public RtmpReceiver(PipeReader reader, PipeWriter writer, ILoggerFactory? loggerFactory = null)
    {
        _reader = reader;
        _writer = writer;
        _loggerFactory = loggerFactory ?? new NullLoggerFactory();
        _logger = _loggerFactory.CreateLogger<RtmpReceiver>();
    }
    
    public RtmpReceiver(Stream readerWriter, ILoggerFactory? loggerFactory = null): this(readerWriter, readerWriter, loggerFactory)
    {
    }
    public RtmpReceiver(Stream reader, Stream writer, ILoggerFactory? loggerFactory = null)
    {
        if (!reader.CanRead)
        {
            throw new ArgumentException(null, nameof(reader));
        }
        if (!writer.CanWrite)
        {
            throw new ArgumentException(null, nameof(writer));
        }
        
        _reader = PipeReader.Create(reader);
        _writer = PipeWriter.Create(writer);
        _loggerFactory = loggerFactory ?? new NullLoggerFactory();
        _logger = _loggerFactory.CreateLogger<RtmpReceiver>();
    }

    public async ValueTask BeginReadAsync(CancellationToken ct = default)
    {
        _logger.ZLogDebug("Begin to handshake");
        var handshake = new HandshakeHandler(_reader, _writer, _loggerFactory.CreateLogger<HandshakeHandler>());
        await handshake.DoHandshakeAsync(ct);
        _logger.ZLogDebug("Handshake done");
        
        var chunk = new ChunkReader(_reader, _writer, _loggerFactory.CreateLogger<ChunkReader>());
        _logger.ZLogDebug("Begin to read chunk");
        await chunk.ReadAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        var t1 = _reader.CompleteAsync();
        var t2 = _writer.CompleteAsync();
        return ValueTaskEx.WhenAll(t1, t2);
    }

    ~RtmpReceiver()
    {
        _reader.Complete();
        _writer.Complete();
    }
}
