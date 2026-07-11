namespace Spangle;

/// <summary>
/// A monitoring snapshot of one live publish session, as returned by
/// <see cref="PublishSessionRegistry.ListSessions"/>. Codec fields are null until
/// the corresponding codec has been announced by the publisher.
/// </summary>
public sealed record PublishSessionInfo
{
    /// <summary>Sanitized stream key (the HLS path component)</summary>
    public required string StreamKey { get; init; }

    /// <summary>Stream name as the publisher sent it</summary>
    public required string StreamName { get; init; }

    public required string SessionId { get; init; }

    /// <summary>"RTMP", "SRT", ...</summary>
    public required string Protocol { get; init; }

    public required string RemoteEndPoint { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public VideoCodec? VideoCodec { get; init; }
    public AudioCodec? AudioCodec { get; init; }
    public uint VideoWidth { get; init; }
    public uint VideoHeight { get; init; }
    public bool IsAudioOnly { get; init; }

    /// <summary>Transport bytes received so far; sample twice to derive a bitrate</summary>
    public long BytesReceived { get; init; }
}
