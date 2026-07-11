using System.IO.Pipelines;
using AsyncAwaitBestPractices;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Spinner;

public abstract class SpinnerBase<T> : ISpinner
    where T : SpinnerBase<T>
{
    protected CancellationToken CancellationToken { get; private init; }
    public PipeWriter Intake { get; }
    public PipeWriter Outlet { get; set; }
    protected PipeReader IntakeReader { get; }
    public abstract ValueTask SpinAsync();

    public void BeginSpin()
    {
        SpinAsync().SafeFireAndForget(LogException);
    }

    protected static readonly ILogger<T> Logger = SpangleLogManager.GetLogger<T>();

    protected static void LogException(Exception e)
    {
        Logger.ZLogError($"{e.Message}: {e.StackTrace}");
    }

    protected SpinnerBase(PipeWriter anotherIntake, CancellationToken ct) : this(ct)
    {
        Outlet = anotherIntake;
    }

    /// <summary>
    /// For spinners composed into a <see cref="LiveContext"/> chain: the Outlet is
    /// wired by the context right before <see cref="BeginSpin"/>.
    /// </summary>
    protected SpinnerBase(CancellationToken ct)
    {
        var opt = new PipeOptions(useSynchronizationContext: false);
        var pipe = new Pipe(opt);
        Intake = pipe.Writer;
        IntakeReader = pipe.Reader;
        Outlet = null!; // assigned before the spin starts
        CancellationToken = ct;
    }
}
