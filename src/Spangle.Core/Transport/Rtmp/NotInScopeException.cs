namespace Spangle.Transport.Rtmp;

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
}
