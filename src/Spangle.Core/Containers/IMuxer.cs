namespace Spangle.Containers;

public interface IMuxer<TInputContext, TOutputContext>
    where TInputContext : IReceiverContext<TInputContext>
    where TOutputContext : ISenderContext<TOutputContext>
{

}
