namespace Spangle.Transport.Rtmp;

[Serializable]
public class NotInScopeException : NotSupportedException
{
    public IReceiverContext? ReceiverContext { get; }

    public NotInScopeException()
    {
    }

    public NotInScopeException(string message)
        : base(message)
    {
    }

    public NotInScopeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NotInScopeException(string message, IReceiverContext? receiverContext)
        : base(message)
    {
        ReceiverContext = receiverContext;
    }

    public NotInScopeException(IReceiverContext receiverContext)
    {
        ReceiverContext = receiverContext;
    }
}
