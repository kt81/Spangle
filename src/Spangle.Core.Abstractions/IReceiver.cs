namespace Spangle;

/// <summary>
/// Common interface of instances receiving streaming
/// </summary>
/// <typeparam name="TContext"></typeparam>
public interface IReceiver<in TContext> where TContext : IReceiverContext
{
    ValueTask StartAsync(TContext context);
}
