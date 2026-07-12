using ZLogger;

namespace Spangle.Transport.Rtsp.ControlFlow;

internal sealed partial class RtspControlFlow
{
    /// <summary>
    /// OPTIONS: confirms the server is alive and reveals its supported methods
    /// (the Public header), which decides whether GET_PARAMETER keepalive is available.
    /// </summary>
    private async ValueTask OptionsAsync(CancellationToken ct)
    {
        RtspMessage response = await SendAsync("OPTIONS", baseUri, decorate: null, ct).ConfigureAwait(false);
        _serverPublicMethods = response.Header("Public");
        s_logger.ZLogDebug($"RTSP OPTIONS ok; server methods: {_serverPublicMethods ?? "(none advertised)"}");
    }
}
