namespace Spangle.Transport.Rtsp.ControlFlow;

internal sealed partial class RtspControlFlow
{
    /// <summary>
    /// Refreshes the session so the server does not time it out between media packets.
    /// Prefers GET_PARAMETER, but falls back to OPTIONS when the server never
    /// advertised GET_PARAMETER in its OPTIONS Public header (or the dialect forces it).
    /// </summary>
    public async ValueTask SendKeepAliveAsync(CancellationToken ct)
    {
        bool serverHasGetParameter =
            _serverPublicMethods?.Contains("GET_PARAMETER", StringComparison.OrdinalIgnoreCase) == true;
        string method = dialect.KeepAliveMethod(serverHasGetParameter);
        await SendAsync(method, baseUri, decorate: null, ct).ConfigureAwait(false);
    }

    /// <summary>Best-effort TEARDOWN on shutdown; failures are ignored (the socket is closing anyway).</summary>
    public async ValueTask TeardownAsync(CancellationToken ct)
    {
        if (_sessionId is null)
        {
            return;
        }
        try
        {
            await SendAsync("TEARDOWN", baseUri, decorate: null, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is RtspProtocolException or IOException or OperationCanceledException)
        {
            // the server may have already dropped us; nothing to do
        }
    }
}
