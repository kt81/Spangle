using Spangle.SourceGenerator.Rtmp;

namespace Spangle.Rtmp.NetConnection;

[Amf0Serializable]
public partial struct CreateStreamResult
{
    [Amf0Field(0)] public string     CommandName;
    [Amf0Field(1)] public double     TransactionId;
    [Amf0Field(2)] public AmfObject? Properties;
    [Amf0Field(3)] public object?    StreamId;

    public static CreateStreamResult Create(double transactionId, object? streamId)
    {
        var self = new CreateStreamResult
        {
            CommandName = "_result", TransactionId = transactionId, Properties = null, StreamId = streamId,
        };
        return self;
    }
}
