using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography.X509Certificates;

namespace Spangle.Extensions.Kestrel;

public class SpangleMediaServerOptions
{
    /// <summary>
    /// The path of the section to bind in the setting file
    /// </summary>
    public const string SectionPath = "Spangle";

    public RtmpOptions Rtmp { get; set; } = new();
    public SrtOptions Srt { get; set; } = new();
    public HlsOptions Hls { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
    public ManagementOptions Management { get; set; } = new();
    public PublishOptions Publish { get; set; } = new();
}

/// <summary>
/// Publish authorization policy. The default stays allow-all + last-wins; listing
/// stream names here switches the built-in authorizer to an exact-match allowlist.
/// A custom <see cref="IPublishAuthorizer"/> registered in DI always wins over both.
/// </summary>
public class PublishOptions
{
    /// <summary>
    /// Stream names (RTMP publish names / SRT streamids) allowed to publish.
    /// Empty keeps the allow-all policy.
    /// </summary>
    public IList<string> AllowedStreamNames { get; } = [];
}

/// <summary>
/// The management surface (web console + control API). It listens on its own
/// port so operational endpoints never share the public delivery port.
/// </summary>
public class ManagementOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, 65535)] public int Port { get; set; } = 8081;

    /// <summary>
    /// Address the management endpoint binds to. Loopback by default; binding
    /// wider (e.g. "0.0.0.0") requires <see cref="Token"/> to be set.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Bearer token required on every management request when set
    /// (<c>Authorization: Bearer ...</c>). Mandatory for non-loopback binds.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// TLS for the management port. Without it the Bearer token travels in
    /// cleartext, so anything beyond loopback should turn this on.
    /// </summary>
    public TlsOptions Tls { get; set; } = new();
}

/// <summary>
/// TLS for one listener. Plaintext until <see cref="Enabled"/> is set, then
/// <see cref="CertificatePath"/> is a PKCS#12/PFX file (with
/// <see cref="CertificatePassword"/> if the file has one) — or a PEM
/// certificate when <see cref="KeyPath"/> points at the PEM private key.
/// </summary>
public class TlsOptions
{
    public bool Enabled { get; set; }

    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    public string? KeyPath { get; set; }

    internal X509Certificate2 LoadCertificate()
    {
        if (string.IsNullOrEmpty(CertificatePath))
        {
            throw new InvalidOperationException("Tls.CertificatePath is required when Tls.Enabled");
        }
        return string.IsNullOrEmpty(KeyPath)
            ? X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, CertificatePassword)
            : X509Certificate2.CreateFromPemFile(CertificatePath, KeyPath);
    }
}

public class RtmpOptions : MediaProtocolOptions
{
    [Range(1025, 65535)] public int Port { get; set; } = 1935;

    /// <summary>
    /// RTMP cannot declare "no video is coming", so a session with audio but no video
    /// codec after this many milliseconds is treated as audio-only (radio publishers).
    /// 0 disables the fallback.
    /// </summary>
    [Range(0, 60_000)] public int AudioOnlyFallbackMs { get; set; } = 3000;

    /// <summary>
    /// Converts AMF0 data events (onTextData, cue points, ...) into timed ID3
    /// metadata carried in the HLS output. Adds one spinner hop to the pipeline.
    /// </summary>
    public bool TimedMetadata { get; set; } = true;

    /// <summary>
    /// Announced to publishers as WindowAcknowledgementSize / SetPeerBandwidth (bytes)
    /// during the connect sequence.
    /// </summary>
    [Range(1, uint.MaxValue)] public uint Bandwidth { get; set; } = 1_500_000;

    /// <summary>RTMPS: TLS on the RTMP listener (publishers connect with rtmps://)</summary>
    public TlsOptions Tls { get; set; } = new();
}

public class SrtOptions : MediaProtocolOptions
{
    [Range(1025, 65535)] public int Port { get; set; } = 9998;

    /// <summary>
    /// Pre-shared passphrase (10-79 bytes) for SRT encryption; senders presenting a
    /// different passphrase are rejected. Null accepts unencrypted connections only.
    /// </summary>
    public string? Passphrase { get; set; }
}

public class HttpOptions
{
    /// <summary>Port for HTTP delivery (HLS files and the test player)</summary>
    [Range(1, 65535)] public int Port { get; set; } = 8080;

    /// <summary>
    /// Enables POST /api/streams/{key}/metadata: injects timed ID3 metadata into a
    /// live session. Set <see cref="MetadataInjectionToken"/> to require a Bearer
    /// token; without one, protect the endpoint at the network level instead.
    /// </summary>
    public bool MetadataInjection { get; set; } = true;

    /// <summary>
    /// Bearer token required on metadata injection requests when set
    /// (<c>Authorization: Bearer ...</c>). The endpoint lives on the public
    /// delivery port, so set this anywhere the port is reachable by viewers.
    /// </summary>
    public string? MetadataInjectionToken { get; set; }

    /// <summary>HTTPS for the delivery port (HLS/DASH and the test player)</summary>
    public TlsOptions Tls { get; set; } = new();
}

public class HlsOptions : MediaProtocolOptions
{
    /// <summary>Segment container: "TS" (MPEG-2 TS) or "fMP4" (CMAF)</summary>
    public string SegmentFormat { get; set; } = "TS";

    /// <summary>
    /// Output backend: "Memory" (default; the live window is served from process
    /// memory, nothing touches disk) or "File" (segments persist under
    /// <see cref="OutputDirectory"/> as an archive, like before)
    /// </summary>
    public string Storage { get; set; } = "Memory";

    /// <summary>Directory where segments and playlists are written (File storage)</summary>
    public string OutputDirectory { get; set; } = "hls-out";

    /// <summary>HTTP path prefix the HLS files are served under</summary>
    public string RequestPath { get; set; } = "/hls";

    /// <summary>Minimum segment duration in seconds; segments are cut at the first keyframe after this</summary>
    [Range(0.5, 60.0)] public double TargetSegmentDuration { get; set; } = 2.0;

    /// <summary>
    /// Segments kept in the live playlist. Larger windows give viewers more rewind
    /// and make LL-HLS delta updates (?_HLS_skip=YES) actually skip something.
    /// </summary>
    [Range(3, 3600)] public int PlaylistWindow { get; set; } = 6;

    /// <summary>Enables LL-HLS partial segments and blocking playlist reload (fMP4 only)</summary>
    public bool LowLatency { get; set; }

    /// <summary>
    /// Re-segments SRT-ingested TS packets as-is for TS output (half the container
    /// work, byte-faithful to the source). Disable to force the demux+remux path,
    /// e.g. when MediaFrame spinner plugins must run on SRT sessions.
    /// </summary>
    public bool TsPassthrough { get; set; } = true;

    /// <summary>Target duration of LL-HLS partial segments in seconds</summary>
    [Range(0.1, 5.0)] public double PartTargetDuration { get; set; } = 0.5;

    /// <summary>
    /// How long an ended stream's final window stays servable from memory storage,
    /// in seconds, before it is freed. 0 keeps every ended stream until the same
    /// key publishes again — memory then grows with the number of distinct stream
    /// keys ever published. File storage is an archive and is never cleaned.
    /// </summary>
    [Range(0, 604_800)] public int EndedStreamTtlSeconds { get; set; } = 300;
}

public abstract class MediaProtocolOptions
{
    public bool Enabled { get; set; }
}
