using System.IO.Pipelines;

namespace Spangle.Spinner;

public class NALFileToAnnexBSpinner : ISpinner<INALFileFormatSpinnerIntakeAdapter, INALAnnexBSpinnerOutletAdapter>
{
    private readonly INALFileFormatSpinnerIntakeAdapter _spinnerIntake;
    private readonly INALAnnexBSpinnerOutletAdapter    _spinnerOutlet;

    private bool _hasSentAUD = false;
    private bool _hasSentPES = false;

    public NALFileToAnnexBSpinner(INALFileFormatSpinnerIntakeAdapter spinnerIntake, INALAnnexBSpinnerOutletAdapter spinnerOutlet)
    {
        _spinnerIntake = spinnerIntake;
        _spinnerOutlet = spinnerOutlet;
    }

    public ValueTask SpinAsync()
    {
        // TODO implement
        // _input.VideoInput.

        return ValueTask.CompletedTask;
    }
}

public interface INALFileFormatSpinnerIntakeAdapter : ISpinnerIntakeAdapter
{
}
public interface INALAnnexBSpinnerOutletAdapter : ISpinnerOutletAdapter
{
}
