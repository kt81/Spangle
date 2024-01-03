using Spangle.Protocols.HLS;
using Spangle.Protocols.Rtmp;

namespace Spangle.Mux;

public class FlvToTSMuxer(RtmpReceiverContext inputContext, HLSSenderContext outputContext)
    : IMuxer<RtmpReceiverContext, HLSSenderContext>
{
    private readonly RtmpReceiverContext _inputContext  = inputContext;
    private readonly HLSSenderContext    _outputContext = outputContext;

    public void Convert()
    {
        var input = _inputContext.GetStreamOrError();

    }
}
