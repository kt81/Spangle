using System.Text;
using Spangle.Transport.DASH;
using Spangle.Transport.HLS;

namespace Spangle.Tests.Transport.HLS;

/// <summary>
/// LL-DASH foundations: memory-backend blobs readable while they grow, and the
/// fixed-duration MPD signaling that makes the in-progress segment addressable.
/// </summary>
public class LiveBlobTests
{
    [Fact]
    public async Task GrowingBlobStreamsChunksAndSeals()
    {
        var stream = (ILiveBlobStreamStorage)new MemoryHLSStorage().GetStream("test");

        stream.AppendBlob("seg.m4s", [1, 2]);
        stream.AppendBlob("seg.m4s", [3]);

        stream.TryOpenLiveBlob("seg.m4s", out LiveBlobReader reader).Should().BeTrue();
        (await reader.ReadNextAsync(CancellationToken.None)).Should().Equal([1, 2]);
        (await reader.ReadNextAsync(CancellationToken.None)).Should().Equal([3]);

        // the next read blocks until the writer appends or completes
        ValueTask<byte[]?> pending = reader.ReadNextAsync(CancellationToken.None);
        pending.IsCompleted.Should().BeFalse();
        stream.AppendBlob("seg.m4s", [4, 5]);
        (await pending).Should().Equal([4, 5]);

        ValueTask<byte[]?> tail = reader.ReadNextAsync(CancellationToken.None);
        stream.CompleteBlob("seg.m4s");
        (await tail).Should().BeNull("completion ends the stream");

        // sealed: a regular blob with the concatenated bytes, no growing handle
        var readable = (IHLSStreamStorage)stream;
        readable.TryReadBlob("seg.m4s", out ReadOnlyMemory<byte> all).Should().BeTrue();
        all.ToArray().Should().Equal([1, 2, 3, 4, 5]);
        stream.TryOpenLiveBlob("seg.m4s", out _).Should().BeFalse();
    }

    [Fact]
    public void LowLatencyMpdUsesFixedDurationArithmetic()
    {
        var storage = new MemoryHLSStorage().GetStream("test");
        var dash = new DashManifest(storage)
        {
            TargetSegmentDuration = 2.0,
            PartTargetDuration = 0.5,
        };
        dash.Tracks.Add(new DashTrack
        {
            MimeType = "video/mp4", Codecs = "avc1.64001F", InitName = "init_v.mp4", SegmentPrefix = "segV",
        });
        var playlist = new HLSPlaylist(storage, "init_v.mp4", partTargetDuration: 0.5, dash: dash)
        {
            SegmentNamePrefix = "segV",
        };
        playlist.AddSegment(playlist.NextSegmentName(".m4s"), 2.0);

        storage.TryReadBlob("manifest.mpd", out ReadOnlyMemory<byte> blob).Should().BeTrue();
        string mpd = Encoding.UTF8.GetString(blob.Span);

        mpd.Should().Contain("duration=\"2000\"");
        mpd.Should().Contain("availabilityTimeOffset=\"1.5\"");
        mpd.Should().Contain("availabilityTimeComplete=\"false\"");
        mpd.Should().Contain("startNumber=\"0\"", "fixed-duration arithmetic anchors at media time zero");
        mpd.Should().NotContain("SegmentTimeline");
        mpd.Should().Contain("UTCTiming");
        mpd.Should().Contain("<Latency");

        // an ended stream goes back to the exact static timeline
        playlist.Complete();
        storage.TryReadBlob("manifest.mpd", out blob).Should().BeTrue();
        mpd = Encoding.UTF8.GetString(blob.Span);
        mpd.Should().Contain("type=\"static\"").And.Contain("SegmentTimeline");
    }
}
