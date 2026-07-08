using System.IO.Pipelines;

namespace Spangle;

public interface ISenderContext
{
    /// <summary>
    /// The input pipe of this sender. The upstream stage (a receiver or a spinner)
    /// writes its output stream here; what flows through depends on the pairing
    /// (e.g. MPEG-2 TS packets, or MediaFrame records for senders that mux themselves).
    /// </summary>
    public PipeWriter Intake { get; }

    /// <summary>
    /// Descriptive information about the source of this stream
    /// (stream name, codecs, video dimensions). Wired by <c>LiveContext</c>.
    /// </summary>
    public IReceiverContext? SourceInfo { get; set; }
}

public interface ISenderContext<out TSelf> : ISenderContext where TSelf : ISenderContext<TSelf>
{
}
