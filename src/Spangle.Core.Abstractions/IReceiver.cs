using System.IO.Pipelines;

namespace Spangle;

/// <summary>
/// Common interface of instances receiving streaming
/// </summary>
/// <typeparam name="TContext"></typeparam>
public interface IReceiver<in TContext> where TContext : IReceiverContext<TContext>
{
    ValueTask BeginReadAsync(TContext context);
}
