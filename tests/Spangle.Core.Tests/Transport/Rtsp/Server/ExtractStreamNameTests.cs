using Spangle.Transport.Rtsp.Server;

namespace Spangle.Tests.Transport.Rtsp.Server;

/// <summary>
/// <see cref="RtspServerControlFlow.ExtractStreamName"/> derives the publish key from
/// the ANNOUNCE URL exactly like an RTMP publish name: the last non-empty path segment,
/// falling back to "stream" for a path-less URL. This keeps the HLS output predictable
/// (/hls/&lt;name&gt;/...) regardless of how deep the client nests the publish path.
/// </summary>
public class ExtractStreamNameTests
{
    [Theory]
    [InlineData("rtsp://h:8554/live/cam", "cam")]
    [InlineData("rtsp://h/cam", "cam")]
    [InlineData("rtsp://h/a/b/c", "c")]
    [InlineData("rtsp://h/", "stream")]
    [InlineData("rtsp://h", "stream")]
    [InlineData("rtsp://h/live/cam/", "cam")]
    public void ReturnsLastPathSegment(string input, string expected) =>
        RtspServerControlFlow.ExtractStreamName(input).Should().Be(expected);
}
