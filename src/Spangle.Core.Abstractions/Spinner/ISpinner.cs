using System.IO.Pipelines;

namespace Spangle.Spinner;

public interface ISpinner<TInput, TOutput>
    where TInput : ISpinnerIntakeAdapter
    where TOutput : ISpinnerOutletAdapter
{
    ValueTask SpinAsync();
}

/// <summary>
/// Adapter for spinner intake
///
/// The readers are used to read the data by spinner
/// ReceiverContexts have to implement a marker interface which inherits this interface
/// </summary>
public interface ISpinnerIntakeAdapter
{
    PipeReader VideoReader { get; }
    PipeReader AudioReader { get; }
}

/// <summary>
/// Adapter for spinner outlet
///
/// The writers are used to write the data by spinner
/// SenderContexts have to implement a marker interface which inherits this interface
/// </summary>
public interface ISpinnerOutletAdapter
{
    PipeWriter VideoWriter { get; }
    PipeWriter AudioWriter { get; }
}
