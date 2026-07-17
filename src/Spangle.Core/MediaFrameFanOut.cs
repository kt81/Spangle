using System.IO.Pipelines;

namespace Spangle;

/// <summary>
/// Copies one MediaFrame stream to several terminal intakes, so a session can feed more than one
/// sender — HLS and MOQT from the same ingest. A byte copier, deliberately: it never parses frames,
/// because it never drops any. Backpressure is coupled — the slowest output paces them all — which
/// is the v1 trade: the outputs Spangle ships drain promptly (the MOQT sender drops frames itself
/// while nobody is subscribed), and frame-aware per-output dropping can replace this pump without
/// changing anything around it.
/// <para>
/// An output that completes its reader (its sender finished or died) is dropped and the rest
/// continue — one dead egress must not end the broadcast. When the intake completes, every
/// remaining output is completed, which is how end-of-stream propagates to each sender.
/// </para>
/// </summary>
internal sealed class MediaFrameFanOut
{
    private readonly Pipe _pipe = new(new PipeOptions(useSynchronizationContext: false));
    private readonly PipeWriter?[] _outputs;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Creates the fan-out and starts its pump. <paramref name="outputs"/> are terminal intakes.</summary>
    public MediaFrameFanOut(IReadOnlyList<PipeWriter> outputs, CancellationToken cancellationToken)
    {
        _outputs = [.. outputs];
        _cancellationToken = cancellationToken;
        // Owned by the pump itself; it ends when the intake completes or the session is cancelled.
        _ = Task.Run(PumpAsync, CancellationToken.None);
    }

    /// <summary>Where the pipeline writes; what the receiver's MediaOutlet ultimately points at.</summary>
    public PipeWriter Intake => _pipe.Writer;

    private async Task PumpAsync()
    {
        PipeReader reader = _pipe.Reader;
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(_cancellationToken).ConfigureAwait(false);
                foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                {
                    await CopyToOutputsAsync(segment).ConfigureAwait(false);
                }

                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // session shutdown; fall through to completing the outputs so the senders drain
        }
        finally
        {
            for (var i = 0; i < _outputs.Length; i++)
            {
                if (_outputs[i] is { } output)
                {
                    _outputs[i] = null;
                    await CompleteQuietlyAsync(output).ConfigureAwait(false);
                }
            }

            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask CopyToOutputsAsync(ReadOnlyMemory<byte> segment)
    {
        for (var i = 0; i < _outputs.Length; i++)
        {
            if (_outputs[i] is not { } output)
            {
                continue;
            }

            try
            {
                FlushResult flushed = await output.WriteAsync(segment, _cancellationToken).ConfigureAwait(false);
                if (flushed.IsCompleted)
                {
                    // its reader is done with us — the sender behind it finished or died
                    _outputs[i] = null;
                    await CompleteQuietlyAsync(output).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // a write to a completed pipe throws; same meaning as IsCompleted above
                _outputs[i] = null;
            }
        }
    }

    private static async ValueTask CompleteQuietlyAsync(PipeWriter output)
    {
        try
        {
            await output.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // completing an already-completed writer is not worth failing the pump over
        }
    }
}
