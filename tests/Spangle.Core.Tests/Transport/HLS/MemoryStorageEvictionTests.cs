using Spangle.Transport.HLS;

namespace Spangle.Tests.Transport.HLS;

public class MemoryStorageEvictionTests
{
    [Fact]
    public void IdleEndedStreamIsEvicted()
    {
        var storage = new MemoryHLSStorage();
        IHLSStreamStorage stream = storage.GetStream("ended");
        stream.WriteBlob("seg0.ts", [1, 2, 3]);
        stream.PublishPlaylist("#EXTM3U");

        int evicted = storage.EvictIdleStreams(TimeSpan.Zero, static _ => false);

        evicted.Should().Be(1);
        storage.TryGetStream("ended", out _).Should().BeFalse("the final window must be freed");
    }

    [Fact]
    public void LiveStreamSurvivesEvenWhenIdle()
    {
        var storage = new MemoryHLSStorage();
        storage.GetStream("live").WriteBlob("seg0.ts", [1]);

        int evicted = storage.EvictIdleStreams(TimeSpan.Zero, static key => key == "live");

        evicted.Should().Be(0);
        storage.TryGetStream("live", out _).Should().BeTrue("a publisher still owns the key");
    }

    [Fact]
    public void RecentWriteDefersEviction()
    {
        var storage = new MemoryHLSStorage();
        storage.GetStream("fresh").WriteBlob("seg0.ts", [1]);

        int evicted = storage.EvictIdleStreams(TimeSpan.FromHours(1), static _ => false);

        evicted.Should().Be(0);
        storage.TryGetStream("fresh", out _).Should().BeTrue("the stream wrote within the TTL");
    }

    [Fact]
    public async Task EvictionReleasesBlockedLiveBlobReaders()
    {
        var storage = new MemoryHLSStorage();
        IHLSStreamStorage stream = storage.GetStream("lldash");
        var live = (ILiveBlobStreamStorage)stream;
        live.AppendBlob("seg0.m4s", [1, 2]);
        live.TryOpenLiveBlob("seg0.m4s", out LiveBlobReader reader).Should().BeTrue();
        (await reader.ReadNextAsync(CancellationToken.None)).Should().NotBeNull();

        storage.EvictIdleStreams(TimeSpan.Zero, static _ => false).Should().Be(1);

        // the reader must terminate rather than hang on a freed blob
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await reader.ReadNextAsync(cts.Token)).Should().BeNull();
    }

    [Fact]
    public void RepublishAfterEvictionStartsClean()
    {
        var storage = new MemoryHLSStorage();
        storage.GetStream("re").WriteBlob("seg0.ts", [1]);
        storage.EvictIdleStreams(TimeSpan.Zero, static _ => false);

        IHLSStreamStorage fresh = storage.GetStream("re");
        fresh.TryReadBlob("seg0.ts", out _).Should().BeFalse("eviction freed the old window");
        fresh.Playlist.Should().BeNull();
    }
}
