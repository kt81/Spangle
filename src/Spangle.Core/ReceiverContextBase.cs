using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Spangle.Logging;

namespace Spangle;

public abstract class ReceiverContextBase<TSelf>(PipeReader reader, PipeWriter writer, CancellationToken ct)
    : IReceiverContext
    where TSelf : ReceiverContextBase<TSelf>
{
    public abstract string Id { get; }
    public abstract EndPoint EndPoint { get; }

    private VideoCodec? _videoCodec;

    public VideoCodec? VideoCodec
    {
        get { return _videoCodec; }
        set
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(VideoCodec));
            }
            if (_videoCodec == value)
            {
                return;
            }
            _videoCodec = value;
            VideoCodecSet?.Invoke(value.Value);
        }
    }

    public AudioCodec? AudioCodec { get; set; }

    public PipeReader RemoteReader { get; } = reader;
    public PipeWriter RemoteWriter { get; } = writer;

    public PipeWriter? VideoOutlet { get; set; }
    public PipeWriter? AudioOutlet { get; set; }

    public CancellationToken CancellationToken { get; set; } = ct;

    public abstract bool IsCompleted { get; }
    public event Action<VideoCodec>? VideoCodecSet;
    public event Action<AudioCodec>? AudioCodecSet;

    public abstract ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource);

    public override string ToString()
    {
        return ZString.Format("{0}({1} from {2})", GetType().Name, Id, EndPoint.ToString());
    }

    #region Logging support

    protected static readonly ILogger<TSelf> Logger;

    static ReceiverContextBase()
    {
        Logger = SpangleLogManager.GetLogger<TSelf>();
    }

    #endregion
}
