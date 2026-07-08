namespace Spangle;

public interface ISender<in TContext> where TContext : ISenderContext<TContext>
{
    /// <summary>
    /// Consumes the context's intake pipe and delivers the stream until it completes.
    /// </summary>
    ValueTask StartAsync(TContext context);
}
