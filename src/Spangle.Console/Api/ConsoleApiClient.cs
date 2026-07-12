using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Spangle.Console.Api;

// Client-side mirrors of the management API DTOs. Kept by hand on purpose:
// the API is the compatibility boundary, not a shared assembly.

public sealed record StreamStats(
    string StreamKey,
    string StreamName,
    string Protocol,
    string RemoteEndPoint,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    string? VideoCodec,
    string? AudioCodec,
    uint Width,
    uint Height,
    bool IsAudioOnly,
    long BytesReceived,
    long IngestBitrateBps,
    long PlaylistRequests,
    double PlaylistRequestsPerMinute,
    int ActiveWaiters);

public sealed record LogRecord(
    DateTimeOffset Timestamp,
    int Level,
    string Category,
    string Message,
    string? Exception);

public sealed record ServerInfo(
    string Version,
    string Runtime,
    string Os,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    long WorkingSetBytes,
    long GcHeapBytes,
    bool RtmpEnabled,
    int RtmpPort,
    bool SrtEnabled,
    int SrtPort,
    bool RtspEnabled,
    int RtspSourceCount,
    bool RtspListenEnabled,
    int RtspListenPort,
    int HttpPort,
    string SegmentFormat,
    string Storage,
    bool LowLatency,
    bool TsPassthrough);

/// <summary>
/// Typed client for <c>/api/manage</c>: plain JSON GETs plus a small SSE reader
/// (no EventSource interop — the response stream is parsed in C#).
/// </summary>
public sealed class ConsoleApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>Bearer token when the server demands one; held in memory only.</summary>
    public string? Token { get; set; }

    /// <summary>Set when the last request bounced with 401 — the UI shows the token bar.</summary>
    public bool Unauthorized { get; private set; }

    public Task<IReadOnlyList<StreamStats>?> GetStreamsAsync(CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<StreamStats>>("api/manage/streams", ct);

    public Task<ServerInfo?> GetServerAsync(CancellationToken ct) =>
        GetJsonAsync<ServerInfo>("api/manage/server", ct);

    public Task<IReadOnlyList<LogRecord>?> GetLogsAsync(int take, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<LogRecord>>(
            string.Create(CultureInfo.InvariantCulture, $"api/manage/logs?take={take}"), ct);

    public async Task<bool> KickAsync(string streamKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"api/manage/streams/{Uri.EscapeDataString(streamKey)}");
        using HttpResponseMessage response = await SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> InjectMetadataAsync(string streamKey, string name, string value, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/manage/streams/{Uri.EscapeDataString(streamKey)}/metadata");
        request.Content = JsonContent.Create(new { name, value }, options: s_json);
        using HttpResponseMessage response = await SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Follows a server-sent-event endpoint, yielding one deserialized payload per event.</summary>
    public async IAsyncEnumerable<T> SubscribeAsync<T>(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.SetBrowserResponseStreamingEnabled(true);
        using HttpResponseMessage response =
            await SendAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            if (line is null)
            {
                yield break; // server closed the stream
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan(5).TrimStart());
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                var item = JsonSerializer.Deserialize<T>(data.ToString(), s_json);
                data.Clear();
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using HttpResponseMessage response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return await response.Content.ReadFromJsonAsync<T>(s_json, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        if (!string.IsNullOrEmpty(Token))
        {
            request.Headers.Authorization = new("Bearer", Token);
        }
        HttpResponseMessage response = await http.SendAsync(request, completion, ct);
        Unauthorized = response.StatusCode == HttpStatusCode.Unauthorized;
        return response;
    }
}
