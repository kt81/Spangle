namespace Spangle.Transport.Rtsp;

/// <summary>An RTSP handshake failed in a way that will not resolve by retrying the same request.</summary>
public sealed class RtspProtocolException : Exception
{
    public RtspProtocolException() { }
    public RtspProtocolException(string message) : base(message) { }
    public RtspProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
