using System.Buffers;
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
    PipeWriter Outlet { set; }

    /// <summary>
    /// Spin the spinner
    /// </summary>
    /// <returns></returns>
    ValueTask SpinAsync();

    /// <summary>
    /// Fire and forget the spinning
    /// </summary>
    void BeginSpin();
}
