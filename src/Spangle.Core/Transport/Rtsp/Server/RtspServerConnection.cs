using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;

namespace Spangle.Transport.Rtsp.Server;

/// <summary>
/// The server side of the RTSP/TCP wire (this server accepts a push). One read loop
/// demultiplexes client <em>requests</em> (OPTIONS/ANNOUNCE/SETUP/RECORD/TEARDOWN)
/// and — once RECORD started — the `$`-framed interleaved RTP/RTCP the client sends.
/// Requests are answered through <see cref="RtspResponse"/>; media goes to
/// <see cref="OnInterleaved"/>.
/// </summary>
internal sealed class RtspServerConnection(PipeReader reader, PipeWriter writer) : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public void Dispose() => _writeLock.Dispose();

    /// <summary>Handles one client request and returns the response to send.</summary>
    public Func<RtspMessage, ValueTask<RtspResponse>>? OnRequest { get; set; }

    /// <summary>Called for every interleaved frame: (channel, payload).</summary>
    public Func<int, ReadOnlySequence<byte>, ValueTask>? OnInterleaved { get; set; }

    /// <summary>Reads until the peer closes or the token fires; returns the bytes consumed.</summary>
    public async ValueTask<long> RunAsync(CancellationToken ct)
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
            var progress = true;
            while (progress && buff.Length > 0)
            {
                progress = false;
                if (buff.FirstSpan[0] == (byte)'$')
                {
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
                    progress = true;
                }
                else if (TryParseRequest(ref buff, out RtspMessage? request))
                {
                    await DispatchAsync(request).ConfigureAwait(false);
                    progress = true;
                }
            }

            totalBytes += result.Buffer.Length - buff.Length;
            reader.AdvanceTo(buff.Start, result.Buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }
        return totalBytes;
    }

    private async ValueTask DispatchAsync(RtspMessage request)
    {
        RtspResponse response = OnRequest is { } handler
            ? await handler(request).ConfigureAwait(false)
            : RtspResponse.Status(500, "Internal Server Error");
        await WriteResponseAsync(request.CSeq, response).ConfigureAwait(false);
    }

    private async ValueTask WriteResponseAsync(int cseq, RtspResponse response)
    {
        var sb = new StringBuilder(256);
        sb.Append(CultureInfo.InvariantCulture, $"RTSP/1.0 {response.StatusCode} {response.ReasonPhrase}\r\n");
        if (cseq >= 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"CSeq: {cseq}\r\n");
        }
        foreach ((string name, string value) in response.Headers)
        {
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        sb.Append("\r\n");

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            writer.Write(Encoding.ASCII.GetBytes(sb.ToString()));
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static (byte Channel, int Length) ReadInterleavedHead(in ReadOnlySequence<byte> buff)
    {
        Span<byte> head = stackalloc byte[4];
        buff.Slice(0, 4).CopyTo(head);
        return (head[1], (head[2] << 8) | head[3]);
    }

    private static bool TryParseRequest(ref ReadOnlySequence<byte> buff, [NotNullWhen(true)] out RtspMessage? request)
    {
        request = null;
        long headEnd = FindDoubleCrlf(buff);
        if (headEnd < 0)
        {
            return false;
        }

        byte[] head = buff.Slice(0, headEnd).ToArray();
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
            return false;
        }
        if (bodyLength > 0)
        {
            parsed.Body = buff.Slice(bodyStart, bodyLength).ToArray();
        }
        buff = buff.Slice(bodyStart + bodyLength);
        request = parsed;
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
}

/// <summary>A response the server sends to a client request.</summary>
internal sealed class RtspResponse
{
    public int StatusCode { get; private init; } = 200;
    public string ReasonPhrase { get; private init; } = "OK";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static RtspResponse Ok() => new();

    public static RtspResponse Status(int code, string reason) => new() { StatusCode = code, ReasonPhrase = reason };

    public RtspResponse With(string name, string value)
    {
        Headers[name] = value;
        return this;
    }
}
