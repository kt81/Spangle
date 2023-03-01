using System.Runtime.Serialization;

namespace Spangle.Net.Transport.SRT;

public class SRTException : InvalidOperationException
{
    public SRTException()
    {
    }

    protected SRTException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }

    public SRTException(string? message) : base(message)
    {
    }

    public SRTException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
