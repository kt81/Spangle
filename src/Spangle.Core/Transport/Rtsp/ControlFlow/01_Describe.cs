using Spangle.Transport.Rtsp.Sdp;
using ZLogger;

namespace Spangle.Transport.Rtsp.ControlFlow;

internal sealed partial class RtspControlFlow
{
    /// <summary>
    /// DESCRIBE: fetches the SDP media description. We ask for SDP explicitly and
    /// reject anything else — the receiver acts only on SDP.
    /// </summary>
    private async ValueTask DescribeAsync(CancellationToken ct)
    {
        RtspMessage response = await SendAsync("DESCRIBE", baseUri,
            static request => request.Headers["Accept"] = "application/sdp", ct).ConfigureAwait(false);

        string? contentType = response.Header("Content-Type");
        if (contentType is not null && !contentType.Contains("application/sdp", StringComparison.OrdinalIgnoreCase))
        {
            throw new RtspProtocolException($"DESCRIBE returned `{contentType}`, not application/sdp");
        }
        if (response.Body.Length == 0)
        {
            throw new RtspProtocolException("DESCRIBE returned an empty body");
        }

        _sdp = SdpSession.Parse(response.Body);
        int usable = _sdp.Media.Count(static m => m.Kind is SdpMediaKind.Video or SdpMediaKind.Audio);
        if (usable == 0)
        {
            throw new RtspProtocolException("The SDP describes no video or audio track");
        }
        s_logger.ZLogInformation($"RTSP DESCRIBE ok; {usable} usable track(s)");
    }
}
