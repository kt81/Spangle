using Spangle.SourceGenerator.Rtmp;

namespace Spangle.Rtmp.NetConnection;

[Amf0Serializable]
internal partial struct CommonResult
{
    [Amf0Field(0)]
    public string CommandName;
    [Amf0Field(1)]
    public double TransactionId;
    [Amf0Field(2)]
    public IReadOnlyDictionary<string, object?>? Properties;
}
