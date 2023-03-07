using System.Runtime.Serialization;

namespace Spangle.Protocols.Rtmp;

[Serializable]
public class NotInScopeException : NotSupportedException
{
    public IReceiverContext? ReceiverContext { get; }

    public NotInScopeException()
    {
    }

    public NotInScopeException(string message, IReceiverContext? receiverContext = null)
        : base(message)
    {
        ReceiverContext = receiverContext;
    }

    public NotInScopeException(IReceiverContext receiverContext)
    {
        ReceiverContext = receiverContext;
    }

    protected NotInScopeException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}
