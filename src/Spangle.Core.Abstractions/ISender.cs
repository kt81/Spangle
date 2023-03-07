using System.IO.Pipelines;

namespace Spangle;

public interface ISender<in TContext> where TContext : ISenderContext<TContext>
{
}
