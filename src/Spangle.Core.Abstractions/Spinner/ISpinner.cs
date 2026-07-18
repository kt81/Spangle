using System.IO.Pipelines;

namespace Spangle.Spinner;

public interface ISpinner
{
    /// <summary>
    /// Writer to pass to the upstream
    /// </summary>
    PipeWriter Intake { get; }

    /// <summary>
    /// Writer that is given by the downstream
    /// </summary>
    PipeWriter Outlet { get; set; }

    /// <summary>
    /// Spin the spinner
    /// </summary>
    /// <returns></returns>
    ValueTask SpinAsync();

    /// <summary>
    /// Fire and forget the spinning. <paramref name="onFaulted"/>, when given, runs if the spin
    /// ends by throwing — the hook a host uses to end a session whose pipeline has died rather than
    /// let it sit alive but silent. Normal completion (the intake finished) does not invoke it.
    /// </summary>
    void BeginSpin(Action<Exception>? onFaulted = null);
}
