using System.Buffers;
using System.Text.Json;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// Which draft of the MSF catalog syntax a document is written in.
/// <para>
/// The two drafts describe the same catalog: every field this type writes is named and interpreted
/// identically in both. They disagree on exactly one thing — the JSON type of the
/// <c>version</c> field — which is enough to make a document unreadable by a parser expecting the
/// other, so it has to be chosen rather than assumed. The divergence between the drafts is real but
/// it lives in delta updates, initialization data and CMSF, none of which this implementation
/// carries yet; when it does, that is the point to consider splitting the model in two the way LOC
/// is split (see <see cref="Loc01Properties"/>), because that is where the drafts actually differ in
/// meaning rather than in spelling.
/// </para>
/// </summary>
public enum MsfDraft
{
    /// <summary>
    /// <b>draft-ietf-moq-msf-00</b> — <c>version</c> is the Number <c>1</c>. This is what every MSF
    /// consumer that exists today reads (moq-playa's <c>@moqt/msf</c> is written against -00 and
    /// rejects any other type), so it is the default: a catalog nothing can parse advertises
    /// nothing.
    /// </summary>
    Draft00,

    /// <summary>
    /// <b>draft-ietf-moq-msf-01</b> (§5.1.1) — <c>version</c> is the String <c>"1"</c>. The current
    /// specification, and where consumers will land; written here so we already conform when they
    /// do, but no implementation reads it yet.
    /// </summary>
    Draft01,
}

/// <summary>
/// The <c>packaging</c> values (MSF-01 §5.2.4) this implementation can publish — the field that
/// tells a subscriber how to read a track's objects.
/// </summary>
public static class MsfPackaging
{
    /// <summary>LOC: one encoded frame per object, metadata in the object's Properties.</summary>
    public const string Loc = "loc";

    /// <summary>CMSF: one CMAF chunk per object (draft-ietf-moq-cmsf §3.5.1).</summary>
    public const string Cmaf = "cmaf";
}

/// <summary>The <c>role</c> values MSF reserves (MSF-01 §5.2.6). Custom roles are allowed beside them.</summary>
public static class MsfTrackRole
{
    /// <summary>A video track.</summary>
    public const string Video = "video";

    /// <summary>An audio track.</summary>
    public const string Audio = "audio";

    /// <summary>Captions.</summary>
    public const string Caption = "caption";

    /// <summary>Subtitles.</summary>
    public const string Subtitle = "subtitle";
}

/// <summary>
/// One track's entry in an <see cref="MsfCatalog"/> (MSF-01 §5.2) — what a subscriber needs in order
/// to decide whether it wants the track and how to decode it once it has it.
/// <para>
/// <see cref="Name"/>, <see cref="Packaging"/> and <see cref="IsLive"/> are required; every other
/// field is optional and is left out of the JSON when null. A consumer ignores fields it does not
/// know, so an unset field costs a consumer nothing — but <see cref="Codec"/> is the exception worth
/// naming: LOC deliberately has no media type of its own, which makes this the only place a
/// subscriber can learn what decoder to build.
/// </para>
/// </summary>
public sealed record MsfTrack
{
    /// <summary>The MOQT Track Name (§5.2.3). Unique within its namespace.</summary>
    public required string Name { get; init; }

    /// <summary>How the track's objects are packaged (§5.2.4) — see <see cref="MsfPackaging"/>.</summary>
    public required string Packaging { get; init; }

    /// <summary>Whether the track is live (§5.2.7), i.e. still being produced.</summary>
    public required bool IsLive { get; init; }

    /// <summary>
    /// The MOQT Track Namespace (§5.2.2). When null the track inherits the catalog track's own
    /// namespace, which is the usual case: the tracks live beside the catalog that lists them.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>What the track is for (§5.2.6) — see <see cref="MsfTrackRole"/>.</summary>
    public string? Role { get; init; }

    /// <summary>A human-readable label (§5.2.10).</summary>
    public string? Label { get; init; }

    /// <summary>The BCP 47 language tag (§5.2.32).</summary>
    public string? Language { get; init; }

    /// <summary>Target end-to-end latency in milliseconds (§5.2.8). Only meaningful on a live track.</summary>
    public int? TargetLatency { get; init; }

    /// <summary>Tracks with the same render group are rendered together (§5.2.11), e.g. this audio with that video.</summary>
    public int? RenderGroup { get; init; }

    /// <summary>Tracks in the same alternate group are interchangeable qualities of one source (§5.2.12).</summary>
    public int? AltGroup { get; init; }

    /// <summary>Names of tracks this one depends on (§5.2.14).</summary>
    public IReadOnlyList<string>? Depends { get; init; }

    /// <summary>The track's temporal layer (§5.2.16).</summary>
    public int? TemporalId { get; init; }

    /// <summary>The track's spatial layer (§5.2.17).</summary>
    public int? SpatialId { get; init; }

    /// <summary>The WebCodecs codec string (§5.2.18), e.g. <c>avc1.64001f</c> or <c>opus</c>.</summary>
    public string? Codec { get; init; }

    /// <summary>The track's MIME type (§5.2.19).</summary>
    public string? MimeType { get; init; }

    /// <summary>Frames per second (§5.2.20).</summary>
    public double? Framerate { get; init; }

    /// <summary>Time units per second (§5.2.21) — the units the track's timestamps are counted in.</summary>
    public ulong? Timescale { get; init; }

    /// <summary>Maximum bitrate in bits per second (§5.2.22).</summary>
    public ulong? Bitrate { get; init; }

    /// <summary>Encoded width in pixels (§5.2.26).</summary>
    public uint? Width { get; init; }

    /// <summary>Encoded height in pixels (§5.2.27).</summary>
    public uint? Height { get; init; }

    /// <summary>Audio sample rate in Hz (§5.2.28).</summary>
    public uint? SampleRate { get; init; }

    /// <summary>The channel configuration (§5.2.29), e.g. <c>"2"</c> for stereo.</summary>
    public string? ChannelConfig { get; init; }

    /// <summary>The track's total duration in milliseconds (§5.2.35). Only meaningful when not live.</summary>
    public double? TrackDuration { get; init; }

    /// <summary>
    /// The track's initialization data, Base64 encoded — what WebCodecs calls the decoder's
    /// <c>description</c>: an AudioSpecificConfig for AAC, an avcC record for H.264.
    /// <para>
    /// LOC video does not need this (the config rides on the keyframes as a property), but LOC audio
    /// has no config property at all in -01, so for AAC this is the only place a subscriber can
    /// learn how to build the decoder.
    /// </para>
    /// <para>
    /// <b>MSF-00 only.</b> This is the -00 shape (§5.1.20); -01 replaced it with a root
    /// <c>initDataList</c> that tracks reference by <c>initRef</c>, which is not implemented —
    /// writing an MSF-01 catalog carrying this throws rather than emit a field that draft does not
    /// define.
    /// </para>
    /// </summary>
    public string? InitData { get; init; }
}

/// <summary>
/// An MSF catalog — the JSON document that tells a subscriber which tracks a publisher is producing
/// and what is in them (draft-ietf-moq-msf, §5). It is published as its own MOQT track, and it is
/// what makes everything else discoverable: LOC names no codec and MOQT names no track, so without
/// a catalog a subscriber that has not been told out of band what to ask for has nothing to go on.
/// See <see cref="MoqCatalogTrack"/> for publishing one.
/// <para>
/// A relay never reads this. §5 makes the catalog payload opaque to relays — unlike LOC's
/// Properties, which relays parse and re-encode per draft — so nothing translates the document on
/// the way to a subscriber. The bytes written here are the bytes it parses, which is why
/// <see cref="MsfDraft"/> is a decision and not a detail.
/// </para>
/// <para>
/// Scope: independent catalogs only. Delta updates (§5.3), initialization data (§5.1.7), publish
/// tracks (§5.1.5), variable substitution (§5.4) and catalog compression (§5.5) are not implemented.
/// A publisher that only ever republishes a complete catalog is conformant — deltas are an
/// optimization — and LOC needs no initialization data, since the decoder configuration rides on the
/// frames themselves (see <see cref="Loc03Properties.VideoConfig"/>).
/// </para>
/// </summary>
public sealed record MsfCatalog
{
    /// <summary>The tracks being produced (§5.1.4). Required, and names must be unique per namespace.</summary>
    public required IReadOnlyList<MsfTrack> Tracks { get; init; }

    /// <summary>The syntax to write, and the syntax a parsed document was read from.</summary>
    public MsfDraft Draft { get; init; } = MsfDraft.Draft00;

    /// <summary>
    /// When this catalog was generated, in milliseconds since the Unix epoch (§5.1.2). Null omits
    /// the field.
    /// </summary>
    public long? GeneratedAt { get; init; }

    /// <summary>
    /// A commitment that the broadcast is over: no track will gain content and no track will be
    /// added (§5.1.3). The field is written only when true — the spec forbids publishing it as
    /// false — and, once published, must never be withdrawn.
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>The catalog's JSON, UTF-8 encoded — the exact bytes of the MOQT object's payload.</summary>
    public byte[] ToJsonUtf8()
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteTo(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes the catalog's JSON into <paramref name="buffer"/>, validating it first.</summary>
    public void WriteTo(IBufferWriter<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Validate();

        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();

        // §5.1.1: the one field whose JSON type the drafts disagree on.
        if (Draft == MsfDraft.Draft00)
        {
            writer.WriteNumber("version"u8, 1);
        }
        else
        {
            writer.WriteString("version"u8, "1");
        }

        if (GeneratedAt is { } generatedAt)
        {
            writer.WriteNumber("generatedAt"u8, generatedAt);
        }

        if (IsComplete)
        {
            writer.WriteBoolean("isComplete"u8, value: true);
        }

        writer.WriteStartArray("tracks"u8);
        foreach (MsfTrack track in Tracks)
        {
            WriteTrack(writer, track);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteTrack(Utf8JsonWriter writer, MsfTrack track)
    {
        writer.WriteStartObject();
        writer.WriteString("name"u8, track.Name);
        WriteIfPresent(writer, "namespace"u8, track.Namespace);
        writer.WriteString("packaging"u8, track.Packaging);
        writer.WriteBoolean("isLive"u8, track.IsLive);
        WriteIfPresent(writer, "targetLatency"u8, track.TargetLatency);
        WriteIfPresent(writer, "role"u8, track.Role);
        WriteIfPresent(writer, "label"u8, track.Label);
        WriteIfPresent(writer, "lang"u8, track.Language);
        WriteIfPresent(writer, "renderGroup"u8, track.RenderGroup);
        WriteIfPresent(writer, "altGroup"u8, track.AltGroup);

        if (track.Depends is { Count: > 0 } depends)
        {
            writer.WriteStartArray("depends"u8);
            foreach (string dependency in depends)
            {
                writer.WriteStringValue(dependency);
            }

            writer.WriteEndArray();
        }

        WriteIfPresent(writer, "temporalId"u8, track.TemporalId);
        WriteIfPresent(writer, "spatialId"u8, track.SpatialId);
        WriteIfPresent(writer, "codec"u8, track.Codec);
        WriteIfPresent(writer, "mimeType"u8, track.MimeType);
        WriteIfPresent(writer, "framerate"u8, track.Framerate);
        WriteIfPresent(writer, "timescale"u8, track.Timescale);
        WriteIfPresent(writer, "bitrate"u8, track.Bitrate);
        WriteIfPresent(writer, "width"u8, track.Width);
        WriteIfPresent(writer, "height"u8, track.Height);
        WriteIfPresent(writer, "samplerate"u8, track.SampleRate);
        WriteIfPresent(writer, "channelConfig"u8, track.ChannelConfig);
        WriteIfPresent(writer, "trackDuration"u8, track.TrackDuration);
        WriteIfPresent(writer, "initData"u8, track.InitData);
        writer.WriteEndObject();
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, ReadOnlySpan<byte> name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, ReadOnlySpan<byte> name, uint? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, ReadOnlySpan<byte> name, ulong? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, ReadOnlySpan<byte> name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    /// <summary>
    /// Checks the rules a catalog must satisfy before it goes on the wire. These are the
    /// specification's MUSTs, and a consumer enforces them — moq-playa's parser throws on each of
    /// them — so an invalid catalog is not a lenient-parser near-miss but a broadcast no one can
    /// watch. Called by <see cref="WriteTo"/>.
    /// </summary>
    private void Validate()
    {
        if (Tracks.Count == 0)
        {
            throw new InvalidOperationException("A catalog must list at least one track (§5.1.4).");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (MsfTrack track in Tracks)
        {
            if (string.IsNullOrEmpty(track.Name))
            {
                throw new InvalidOperationException("Every track needs a name (§5.2.3).");
            }

            if (string.IsNullOrEmpty(track.Packaging))
            {
                throw new InvalidOperationException($"Track '{track.Name}' needs a packaging (§5.2.4).");
            }

            // §5.2.3: unique per namespace. A null namespace is not "no namespace" but "the
            // catalog's own", so all the nulls are one namespace and collide with each other.
            if (!names.Add((track.Namespace ?? string.Empty) + "\0" + track.Name))
            {
                throw new InvalidOperationException(
                    $"Track name '{track.Name}' appears twice in the same namespace (§5.2.3).");
            }

            // §5.2.8 and §5.2.35: each of these fields is meaningless against the other liveness,
            // and MUST NOT be sent there.
            if (!track.IsLive && track.TargetLatency is not null)
            {
                throw new InvalidOperationException(
                    $"Track '{track.Name}' is not live, so it must not carry a target latency (§5.2.8).");
            }

            if (track.IsLive && track.TrackDuration is not null)
            {
                throw new InvalidOperationException(
                    $"Track '{track.Name}' is live, so its duration is not yet known and must not be sent (§5.2.35).");
            }

            // §5.1.20 is an MSF-00 field. MSF-01 moved initialization data to a root initDataList,
            // which is not implemented — so rather than write a field -01 does not define (and a -01
            // parser would ignore, leaving the subscriber unable to decode), say so.
            if (track.InitData is not null && Draft != MsfDraft.Draft00)
            {
                throw new InvalidOperationException(
                    $"Track '{track.Name}' carries initData, which only MSF-00 defines; MSF-01's initDataList is not implemented.");
            }
        }
    }

    /// <summary>
    /// Parses a catalog object's payload. <paramref name="catalogNamespace"/> is the namespace of
    /// the catalog track itself, which tracks that declare none inherit (§5.2.2) — pass it and every
    /// returned track carries a namespace; omit it and a track's namespace may be null.
    /// </summary>
    /// <exception cref="InvalidDataException">The payload is not a catalog this implementation understands.</exception>
    public static MsfCatalog Parse(ReadOnlySpan<byte> json, string? catalogNamespace = null)
    {
        var reader = new Utf8JsonReader(json);
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A catalog is a JSON object (§5).");
        }

        if (root.TryGetProperty("deltaUpdate", out _))
        {
            throw new InvalidDataException("This is a delta update; only independent catalogs are supported (§5.3).");
        }

        MsfDraft draft = ReadVersion(root);

        if (!root.TryGetProperty("tracks", out JsonElement tracks) || tracks.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("A catalog must hold a tracks array (§5.1.4).");
        }

        var parsed = new List<MsfTrack>(tracks.GetArrayLength());
        foreach (JsonElement track in tracks.EnumerateArray())
        {
            parsed.Add(ReadTrack(track, catalogNamespace));
        }

        return new MsfCatalog
        {
            Draft = draft,
            Tracks = parsed,
            GeneratedAt = root.TryGetProperty("generatedAt", out JsonElement generatedAt)
                          && generatedAt.ValueKind == JsonValueKind.Number
                ? generatedAt.GetInt64()
                : null,
            IsComplete = root.TryGetProperty("isComplete", out JsonElement complete)
                         && complete.ValueKind == JsonValueKind.True,
        };
    }

    // §5.1.1: the version's JSON type is what distinguishes the drafts, so reading it is also how we
    // learn which one we are holding. A parser must not attempt a version it does not understand,
    // and both drafts so far are version 1 in their respective spellings.
    private static MsfDraft ReadVersion(JsonElement root)
    {
        if (!root.TryGetProperty("version", out JsonElement version))
        {
            throw new InvalidDataException("A catalog must declare its version (§5.1.1).");
        }

        switch (version.ValueKind)
        {
            case JsonValueKind.Number when version.GetInt32() == 1:
                return MsfDraft.Draft00;
            case JsonValueKind.String when version.GetString() is "1" or "draft-01":
                return MsfDraft.Draft01;
            default:
                throw new InvalidDataException(
                    $"Unsupported catalog version {version} (§5.1.1).");
        }
    }

    private static MsfTrack ReadTrack(JsonElement track, string? catalogNamespace)
    {
        if (track.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Every entry of the tracks array is an object (§5.2.1).");
        }

        string name = RequiredString(track, "name", "§5.2.3");
        string packaging = RequiredString(track, "packaging", "§5.2.4");
        if (!track.TryGetProperty("isLive", out JsonElement live)
            || live.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Track '{name}' must declare isLive (§5.2.7).");
        }

        return new MsfTrack
        {
            Name = name,
            Packaging = packaging,
            IsLive = live.ValueKind == JsonValueKind.True,
            Namespace = OptionalString(track, "namespace") ?? catalogNamespace,
            Role = OptionalString(track, "role"),
            Label = OptionalString(track, "label"),
            Language = OptionalString(track, "lang"),
            TargetLatency = OptionalNumber(track, "targetLatency") is { } latency ? (int)latency : null,
            RenderGroup = OptionalNumber(track, "renderGroup") is { } render ? (int)render : null,
            AltGroup = OptionalNumber(track, "altGroup") is { } alt ? (int)alt : null,
            Depends = ReadDepends(track),
            TemporalId = OptionalNumber(track, "temporalId") is { } temporal ? (int)temporal : null,
            SpatialId = OptionalNumber(track, "spatialId") is { } spatial ? (int)spatial : null,
            Codec = OptionalString(track, "codec"),
            MimeType = OptionalString(track, "mimeType"),
            Framerate = OptionalNumber(track, "framerate"),
            Timescale = OptionalNumber(track, "timescale") is { } timescale ? (ulong)timescale : null,
            Bitrate = OptionalNumber(track, "bitrate") is { } bitrate ? (ulong)bitrate : null,
            Width = OptionalNumber(track, "width") is { } width ? (uint)width : null,
            Height = OptionalNumber(track, "height") is { } height ? (uint)height : null,
            SampleRate = OptionalNumber(track, "samplerate") is { } sampleRate ? (uint)sampleRate : null,
            ChannelConfig = OptionalString(track, "channelConfig"),
            TrackDuration = OptionalNumber(track, "trackDuration"),
            InitData = OptionalString(track, "initData"),
        };
    }

    private static List<string>? ReadDepends(JsonElement track)
    {
        if (!track.TryGetProperty("depends", out JsonElement depends) || depends.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>(depends.GetArrayLength());
        foreach (JsonElement dependency in depends.EnumerateArray())
        {
            if (dependency.GetString() is { } value)
            {
                names.Add(value);
            }
        }

        return names;
    }

    private static string RequiredString(JsonElement track, string field, string section)
    {
        if (track.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        throw new InvalidDataException($"A track must carry a string {field} ({section}).");
    }

    private static string? OptionalString(JsonElement track, string field) =>
        track.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? OptionalNumber(JsonElement track, string field) =>
        track.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
