using System.Buffers;
using System.IO.Pipelines;

namespace Spangle;

public interface ISenderContext
{
    public PipeWriter VideoIntake { get; set; }
    public PipeWriter AudioIntake { get; set; }

    public VideoCodec VideoCodec { get; set; }
    public AudioCodec AudioCodec { get; set; }

}
public interface ISenderContext<out TSelf> : ISenderContext where TSelf : ISenderContext<TSelf>
{

}
