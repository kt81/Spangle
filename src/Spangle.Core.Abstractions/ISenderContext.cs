namespace Spangle;

public interface ISenderContext
{

}
public interface ISenderContext<out TSelf> : ISenderContext where TSelf : ISenderContext<TSelf>
{

}
