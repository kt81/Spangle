using Spangle.Transport.HLS;
using Spangle.Transport.Rtmp;

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
