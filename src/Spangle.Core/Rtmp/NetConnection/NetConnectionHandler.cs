namespace Spangle.Rtmp.NetConnection;

internal class NetConnectionHandler
{
    public static class Commands
    {
        public const string Connect      = "connect";
        public const string Call         = "call";
        public const string Close        = "close";
        public const string CreateStream = "createStream";
    }

    public void Connect(double transactionId, IReadOnlyDictionary<string, object> commandObject, IReadOnlyDictionary<string, object>? optionalUserArgs = null)
    {

    }
}
