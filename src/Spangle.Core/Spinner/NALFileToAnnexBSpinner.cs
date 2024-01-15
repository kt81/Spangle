using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Interop;
using Spangle.Logging;

namespace Spangle.Spinner;

public class NALFileToAnnexBSpinner : ISpinner<INALFileFormatSpinnerIntakeAdapter, INALAnnexBSpinnerOutletAdapter>
{
    private readonly INALFileFormatSpinnerIntakeAdapter _spinnerIntake;
    private readonly INALAnnexBSpinnerOutletAdapter     _spinnerOutlet;
    private readonly CancellationToken                  _ct;

    private bool _hasSentAUD = false;
    private bool _hasSentPES = false;

    private static ILogger<NALFileToAnnexBSpinner> s_logger = SpangleLogManager.GetLogger<NALFileToAnnexBSpinner>();

    public NALFileToAnnexBSpinner(INALFileFormatSpinnerIntakeAdapter spinnerIntake, INALAnnexBSpinnerOutletAdapter spinnerOutlet, CancellationToken ct)
    {
        _spinnerIntake = spinnerIntake;
        _spinnerOutlet = spinnerOutlet;
        _ct = ct;
    }

    public async ValueTask SpinAsync()
    {
        // TODO implement
        while (!_ct.IsCancellationRequested)
        {
            var result = await _spinnerIntake.VideoReader.ReadAsync(_ct);
            var buff = result.Buffer;
            while (buff.Length > 0)
            {
                _spinnerOutlet.VideoWriter.Write(buff.FirstSpan);
                buff = buff.Slice(buff.FirstSpan.Length);
            }

            _spinnerIntake.VideoReader.AdvanceTo(buff.End);
            await _spinnerOutlet.VideoWriter.FlushAsync(_ct);
            // TODO implement correctly
        }

    }
}

public interface INALFileFormatSpinnerIntakeAdapter : ISpinnerIntakeAdapter
{
}
public interface INALAnnexBSpinnerOutletAdapter : ISpinnerOutletAdapter
{
}
