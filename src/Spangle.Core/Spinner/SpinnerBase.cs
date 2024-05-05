using System.Buffers;
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
    public PipeWriter Outlet { protected get; set; }
    protected PipeReader IntakeReader { get; }
    public abstract ValueTask SpinAsync();

    public void BeginSpin()
    {
        SpinAsync().SafeFireAndForget(LogException);
    }

    protected static ILogger<T> Logger = SpangleLogManager.GetLogger<T>();

    protected static void LogException(Exception e)
    {
        Logger.ZLogError($"{e.Message}: {e.StackTrace}");
    }

    public SpinnerBase(PipeWriter anotherIntake, CancellationToken ct)
    {
        var opt = new PipeOptions(useSynchronizationContext: false);
        var pipe = new Pipe(opt);
        Intake = pipe.Writer;
        IntakeReader = pipe.Reader;
        Outlet = anotherIntake;
        CancellationToken = ct;
    }
}
