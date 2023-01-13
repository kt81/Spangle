using System.IO.Pipelines;

namespace Spangle;

public interface IReceiverContext
{
    public string Id { get; init; }
    public PipeReader Reader { get; init; }
    public PipeWriter Writer { get; init; }

    // public PipeWriter Video { get; }
    // public PipeWriter Audio { get; }
}
