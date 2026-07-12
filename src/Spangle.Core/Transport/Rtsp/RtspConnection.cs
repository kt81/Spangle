using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.Rtsp;

/// <summary>
/// The RTSP/TCP wire: writes client requests and demultiplexes everything the server
/// sends back — responses (matched to their request by CSeq), server-initiated
/// requests, and `$`-framed interleaved binary (RTP/RTCP) once PLAY started.
/// One read loop serves both the handshake and the streaming phase, so late
/// responses (keepalives) and early media never confuse each other.
/// </summary>
internal sealed class RtspConnection(PipeReader reader, PipeWriter writer) : IDisposable
{
    private static readonly ILogger<RtspConnection> s_logger = SpangleLogManager.GetLogger<RtspConnection>();

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<RtspMessage>> _pending = new();
    private int _cseq;

    /// <summary>Called for every interleaved frame: (channel, payload).</summary>
    public Func<int, ReadOnlySequence<byte>, ValueTask>? OnInterleaved { get; set; }

    /// <summary>Sends one request and awaits its response (matched by CSeq).</summary>
    public async ValueTask<RtspMessage> ExchangeAsync(RtspRequest request, CancellationToken ct)
    {
        request.CSeq = Interlocked.Increment(ref _cseq);
        var tcs = new TaskCompletionSource<RtspMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.CSeq] = tcs;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                request.WriteTo(writer);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.CSeq, out _);
        }
    }

    /// <summary>
    /// Reads until the peer closes or the token fires. Responses complete their
    /// pending exchange; server-initiated requests get a 200 (they are keepalive
    /// probes in practice); interleaved frames go to <see cref="OnInterleaved"/>.
    /// Returns the total transport bytes consumed.
    /// </summary>
    public async ValueTask<long> RunReadLoopAsync(CancellationToken ct)
    {
        long totalBytes = 0;
        while (true)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ReadOnlySequence<byte> buff = result.Buffer;
            var consumedAny = true;
            while (consumedAny && buff.Length > 0)
            {
                consumedAny = false;
                if (buff.FirstSpan[0] == (byte)'$')
                {
                    // interleaved: '$' + channel + 16-bit big-endian length + payload
                    if (buff.Length < 4)
                    {
                        break;
                    }
                    (byte channel, int length) = ReadInterleavedHead(buff);
                    if (buff.Length < 4 + length)
                    {
                        break;
                    }
                    if (OnInterleaved is { } handler)
                    {
                        await handler(channel, buff.Slice(4, length)).ConfigureAwait(false);
                    }
                    buff = buff.Slice(4 + length);
                    consumedAny = true;
                }
                else if (TryParseTextMessage(ref buff, out RtspMessage? message))
                {
                    await DispatchAsync(message, ct).ConfigureAwait(false);
                    consumedAny = true;
                }
            }

            totalBytes += result.Buffer.Length - buff.Length;
            reader.AdvanceTo(buff.Start, result.Buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        // the connection is gone; nothing pending can ever complete
        foreach (TaskCompletionSource<RtspMessage> tcs in _pending.Values)
        {
            tcs.TrySetException(new IOException("The RTSP connection closed before the response arrived"));
        }
        return totalBytes;
    }

    private static (byte Channel, int Length) ReadInterleavedHead(in ReadOnlySequence<byte> buff)
    {
        Span<byte> head = stackalloc byte[4];
        buff.Slice(0, 4).CopyTo(head);
        return (head[1], (head[2] << 8) | head[3]);
    }

    private static bool TryParseTextMessage(ref ReadOnlySequence<byte> buff, [NotNullWhen(true)] out RtspMessage? message)
    {
        message = null;
        long headEnd = FindDoubleCrlf(buff);
        if (headEnd < 0)
        {
            return false;
        }

        ReadOnlySequence<byte> headSeq = buff.Slice(0, headEnd);
        byte[] head = headSeq.ToArray();
        RtspMessage parsed = RtspMessage.ParseHead(head);

        long bodyStart = headEnd + 4;
        var bodyLength = 0;
        if (parsed.Header("Content-Length") is { } lengthText
            && int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int declared))
        {
            bodyLength = declared;
        }
        if (buff.Length < bodyStart + bodyLength)
        {
            return false; // body not complete yet
        }
        if (bodyLength > 0)
        {
            parsed.Body = buff.Slice(bodyStart, bodyLength).ToArray();
        }
        buff = buff.Slice(bodyStart + bodyLength);
        message = parsed;
        return true;
    }

    private static long FindDoubleCrlf(in ReadOnlySequence<byte> buff)
    {
        var sr = new SequenceReader<byte>(buff);
        Span<byte> probe = stackalloc byte[4];
        while (sr.TryAdvanceTo((byte)'\r', advancePastDelimiter: false))
        {
            if (sr.Remaining < 4)
            {
                return -1;
            }
            buff.Slice(sr.Position, 4).CopyTo(probe);
            if (probe[1] == (byte)'\n' && probe[2] == (byte)'\r' && probe[3] == (byte)'\n')
            {
                return sr.Consumed;
            }
            sr.Advance(1);
        }
        return -1;
    }

    private async ValueTask DispatchAsync(RtspMessage message, CancellationToken ct)
    {
        if (!message.IsRequest)
        {
            if (_pending.TryGetValue(message.CSeq, out TaskCompletionSource<RtspMessage>? tcs))
            {
                tcs.TrySetResult(message);
            }
            else
            {
                s_logger.ZLogWarning($"Unmatched RTSP response (CSeq {message.CSeq}, status {message.StatusCode})");
            }
            return;
        }

        // server-initiated request (GET_PARAMETER/OPTIONS liveness probes): answer 200
        s_logger.ZLogDebug($"Server request {message.Method}; answering 200");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string reply = message.CSeq >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"RTSP/1.0 200 OK\r\nCSeq: {message.CSeq}\r\n\r\n")
                : "RTSP/1.0 200 OK\r\n\r\n";
            writer.Write(System.Text.Encoding.ASCII.GetBytes(reply));
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
