using System.Buffers;
using System.Globalization;
using System.Text;

namespace Spangle.Transport.Rtsp;

/// <summary>An outgoing RTSP/1.0 request (this server is the client — pull ingest).</summary>
internal sealed class RtspRequest(string method, string uri)
{
    public string Method { get; } = method;
    public string Uri { get; } = uri;
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int CSeq { get; set; }

    public void WriteTo(IBufferWriter<byte> writer)
    {
        var sb = new StringBuilder(256);
        sb.Append(Method).Append(' ').Append(Uri).Append(" RTSP/1.0\r\n");
        sb.Append("CSeq: ").Append(CSeq.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach ((string name, string value) in Headers)
        {
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        sb.Append("\r\n");
        writer.Write(Encoding.ASCII.GetBytes(sb.ToString()));
    }
}

/// <summary>A parsed RTSP/1.0 response (or a server-initiated request, see <see cref="IsRequest"/>).</summary>
internal sealed class RtspMessage
{
    /// <summary>True when the peer sent a request (e.g. a GET_PARAMETER keepalive probe) rather than a response.</summary>
    public bool IsRequest { get; init; }

    // response form
    public int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = "";

    // request form
    public string Method { get; init; } = "";
    public string Uri { get; init; } = "";

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = [];

    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;

    public int CSeq => int.TryParse(Header("CSeq"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;

    public bool IsSuccess => !IsRequest && StatusCode is >= 200 and < 300;

    /// <summary>Parses the head section (start line + headers); the body is filled in by the reader.</summary>
    public static RtspMessage ParseHead(ReadOnlySpan<byte> head)
    {
        string text = Encoding.ASCII.GetString(head);
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("Empty RTSP message head");
        }

        RtspMessage message;
        string startLine = lines[0];
        if (startLine.StartsWith("RTSP/", StringComparison.Ordinal))
        {
            // RTSP/1.0 200 OK
            string[] parts = startLine.Split(' ', 3);
            if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int status))
            {
                throw new InvalidDataException($"Malformed RTSP status line: `{startLine}`");
            }
            message = new RtspMessage { StatusCode = status, ReasonPhrase = parts.Length > 2 ? parts[2] : "" };
        }
        else
        {
            // GET_PARAMETER rtsp://... RTSP/1.0 (a server-initiated request)
            string[] parts = startLine.Split(' ', 3);
            if (parts.Length < 3 || !parts[2].StartsWith("RTSP/", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Malformed RTSP request line: `{startLine}`");
            }
            message = new RtspMessage { IsRequest = true, Method = parts[0], Uri = parts[1] };
        }

        foreach (string line in lines.AsSpan(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue; // tolerate junk header lines; cameras produce them
            }
            message.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return message;
    }
}
