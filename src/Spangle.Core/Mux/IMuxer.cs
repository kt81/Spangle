using System.Threading.Channels;

namespace Spangle.Mux;

public interface IMuxer<TInputContext, TOutputContext>
    where TInputContext : IReceiverContext
    where TOutputContext : ISenderContext<TOutputContext>
{

}
