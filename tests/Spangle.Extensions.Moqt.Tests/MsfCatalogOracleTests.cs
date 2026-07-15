using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// Our catalog, read by somebody else's MSF implementation — moq-playa's <c>@moqt/msf</c>, written
/// against draft-ietf-moq-msf-00 by other people and shipped in a browser player.
/// <para>
/// Every other thing we put on the wire is checked against the reference relay, which parses it and
/// hands it back. The catalog cannot be: MSF §5 makes its payload opaque to relays, so the relay
/// will forward a document no player on earth can read without complaint. The first parser that ever
/// judges a catalog is the consumer's, which makes an independent consumer the only oracle there is
/// for this document — and this is it.
/// </para>
/// <para>
/// Gated on <c>MOQ_PLAYA_DIR</c> (a moq-playa checkout with <c>pnpm --filter @moqt/msf build</c>
/// run in it) and, if node is not on PATH, <c>MOQ_NODE</c>. An ordinary test run skips it, the same
/// arrangement <see cref="MoxygenInteropTests"/> uses for the live relay.
/// </para>
/// </summary>
public class MsfCatalogOracleTests
{
    private readonly ITestOutputHelper _output;

    public MsfCatalogOracleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task OurCatalog_IsAcceptedByAnIndependentMsfParser()
    {
        if (Skipped(out string playaDir, out string node))
        {
            return;
        }

        MsfCatalog catalog = new()
        {
            // What a player is given is a relay and a namespace, so the tracks here declare none and
            // inherit "vc" from the catalog track — the arrangement the oracle is asked to resolve.
            Draft = MsfDraft.Draft00,
            GeneratedAt = 1_746_104_606_044,
            Tracks =
            [
                new MsfTrack
                {
                    Name = "video0",
                    Packaging = MsfPackaging.Loc,
                    IsLive = true,
                    Role = MsfTrackRole.Video,
                    RenderGroup = 1,
                    TargetLatency = 2_000,
                    Codec = "avc1.64001f",
                    Width = 640,
                    Height = 360,
                    Framerate = 30,
                    Timescale = 90_000,
                    Bitrate = 1_500_000,
                },
                new MsfTrack
                {
                    Name = "audio0",
                    Packaging = MsfPackaging.Loc,
                    IsLive = true,
                    Role = MsfTrackRole.Audio,
                    RenderGroup = 1,
                    TargetLatency = 2_000,
                    Codec = "opus",
                    SampleRate = 48_000,
                    ChannelConfig = "2",
                    Language = "ja",
                },
            ],
        };

        JsonElement verdict = await AskTheOracleAsync(node, playaDir, catalog, catalogNamespace: "vc");

        verdict.GetProperty("ok").GetBoolean().Should().BeTrue(
            "an MSF parser accepts our catalog: {0}",
            verdict.TryGetProperty("error", out JsonElement error) ? error.GetString() : "(no reason given)");

        // Their constants, not ours: if these disagreed, we would be publishing a well-formed
        // catalog on a track nobody subscribes to.
        verdict.GetProperty("catalogTrackName").GetString().Should().Be(MoqCatalogTrack.TrackName);
        verdict.GetProperty("msfVersion").GetInt32().Should().Be(1);

        // And what it made of it — a parse that succeeds while dropping the codec would still leave
        // a player with nothing to build a decoder from.
        JsonElement tracks = verdict.GetProperty("catalog").GetProperty("tracks");
        tracks.GetArrayLength().Should().Be(2);
        tracks[0].GetProperty("name").GetString().Should().Be("video0");
        tracks[0].GetProperty("codec").GetString().Should().Be("avc1.64001f");
        tracks[0].GetProperty("packaging").GetString().Should().Be("loc");
        tracks[0].GetProperty("namespace").GetString().Should().Be("vc", "their parser applies the same inheritance rule");
        tracks[1].GetProperty("name").GetString().Should().Be("audio0");
        tracks[1].GetProperty("samplerate").GetInt32().Should().Be(48_000);
    }

    /// <summary>
    /// The other half of the claim: that the same parser rejects an MSF-01 document. If it took the
    /// String version too, the draft choice would be a distinction without a difference and the
    /// default could be the current spec instead of the one consumers read.
    /// </summary>
    [Fact]
    public async Task AnMsf01Catalog_IsRejectedByThatSameParser()
    {
        if (Skipped(out string playaDir, out string node))
        {
            return;
        }

        MsfCatalog catalog = new()
        {
            Draft = MsfDraft.Draft01,
            Tracks = [new MsfTrack { Name = "video0", Packaging = MsfPackaging.Loc, IsLive = true }],
        };

        JsonElement verdict = await AskTheOracleAsync(node, playaDir, catalog, catalogNamespace: "vc");

        verdict.GetProperty("ok").GetBoolean().Should().BeFalse(
            "an MSF-00 parser type-checks the version field, so the String spelling is unreadable to it");
        _output.WriteLine($"the -00 parser on our -01 document: {verdict.GetProperty("error").GetString()}");
    }

    private async Task<JsonElement> AskTheOracleAsync(string node, string playaDir, MsfCatalog catalog,
        string catalogNamespace)
    {
        string catalogPath = Path.Combine(Path.GetTempPath(), $"spangle-catalog-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(catalogPath, catalog.ToJsonUtf8());
        _output.WriteLine($"catalog: {await File.ReadAllTextAsync(catalogPath)}");

        try
        {
            string script = Path.Combine(AppContext.BaseDirectory, "Oracle", "msf-oracle.mjs");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(node, [script, playaDir, catalogPath, catalogNamespace])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.Start();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (string.IsNullOrWhiteSpace(stdout))
            {
                throw new InvalidOperationException($"The oracle printed nothing (exit {process.ExitCode}): {stderr}");
            }

            _output.WriteLine($"oracle: {stdout.Trim()}");
            return JsonDocument.Parse(stdout).RootElement.Clone();
        }
        finally
        {
            File.Delete(catalogPath);
        }
    }

    private bool Skipped(out string playaDir, out string node)
    {
        playaDir = Environment.GetEnvironmentVariable("MOQ_PLAYA_DIR") ?? string.Empty;
        node = Environment.GetEnvironmentVariable("MOQ_NODE") ?? "node";

        if (string.IsNullOrWhiteSpace(playaDir))
        {
            _output.WriteLine("MOQ_PLAYA_DIR not set; skipping the MSF catalog oracle.");
            return true;
        }

        if (!File.Exists(Path.Combine(playaDir, "packages", "msf", "dist", "index.js")))
        {
            _output.WriteLine($"@moqt/msf is not built in {playaDir}; run: pnpm --filter @moqt/msf build. Skipping.");
            return true;
        }

        return false;
    }
}
