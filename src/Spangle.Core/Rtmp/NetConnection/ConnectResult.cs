using Spangle.SourceGenerator.Rtmp;

namespace Spangle.Rtmp.NetConnection;

[Amf0Serializable]
public partial struct ConnectResult
{
    [Amf0Field(0)] public string    CommandName;
    [Amf0Field(1)] public double    TransactionId;
    [Amf0Field(2)] public AmfObject Properties;
    [Amf0Field(3)] public AmfObject Information;

    public static ConnectResult CreateDefault()
    {
        var self = new ConnectResult
        {
            CommandName = "_result",
            TransactionId = 1,
            Properties = new Dictionary<string, object?>(),
            Information = new Dictionary<string, object?>(),
        };
        return self;
    }
}
