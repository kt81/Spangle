using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Spangle.Codecs.Id3;
using ZLogger;

namespace Spangle.Spinner;

/// <summary>
/// Routes server-injected timed metadata to the right live session:
/// injectors register themselves under their stream key once media flows,
/// and the host's API calls <see cref="TryInject"/>.
/// </summary>
public sealed class TimedMetadataHub
{
    private readonly ConcurrentDictionary<string, TimedMetadataInjector> _injectors = new(StringComparer.Ordinal);

    /// <summary>
    /// Queues one metadata event onto the live session publishing under
    /// <paramref name="streamKey"/>. False when no such session is live
    /// (or its event queue is shutting down).
    /// </summary>
    public bool TryInject(string streamKey, string name, string value) =>
        _injectors.TryGetValue(streamKey, out TimedMetadataInjector? injector)
        && injector.TryEnqueue(name, value);

    internal void Register(string streamKey, TimedMetadataInjector injector) =>
        _injectors[streamKey] = injector;

    internal void Unregister(string streamKey, TimedMetadataInjector injector) =>
        // only remove our own registration (a successor session may have taken the key)
        ((ICollection<KeyValuePair<string, TimedMetadataInjector>>)_injectors)
        .Remove(new KeyValuePair<string, TimedMetadataInjector>(streamKey, injector));
}

/// <summary>
/// A pass-through spinner with a side door: metadata injected from outside the
/// pipeline (an HTTP API, typically) becomes a timed ID3 frame stamped with the
/// media timeline position the stream has just reached. Injection needs no
/// pipeline-internal API — the spinner observes every frame anyway, so it knows
/// both the current timestamp and the stream key.
/// </summary>
public sealed class TimedMetadataInjector : SpinnerBase<TimedMetadataInjector>
{
    private readonly IReceiverContext _context;
    private readonly TimedMetadataHub _hub;
    private readonly Channel<(string Name, string Value)> _events;

    public TimedMetadataInjector(IReceiverContext context, TimedMetadataHub hub, CancellationToken ct)
        : base(ct)
    {
        _context = context;
        _hub = hub;
        _events = Channel.CreateBounded<(string, string)>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    }

    internal bool TryEnqueue(string name, string value) => _events.Writer.TryWrite((name, value));

    public override async ValueTask SpinAsync()
    {
        string? registeredKey = null;
        try
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                var result = await IntakeReader.ReadAtLeastAsync(MediaFrameHeader.Size, CancellationToken).ConfigureAwait(false);
                if (result.Buffer.Length < MediaFrameHeader.Size)
                {
                    break; // intake completed
                }
                var headerBuff = result.Buffer.Slice(0, MediaFrameHeader.Size);
                var header = MediaFrameHeader.Read(headerBuff);
                IntakeReader.AdvanceTo(headerBuff.End);

                if (header.Length < 0)
                {
                    throw new InvalidDataException($"Broken media frame length: {header.Length}");
                }

                result = await IntakeReader.ReadAtLeastAsync(header.Length, CancellationToken).ConfigureAwait(false);
                if (result.Buffer.Length < header.Length)
                {
                    break;
                }
                var payload = result.Buffer.Slice(0, header.Length);

                // pass the frame through untouched
                MediaFrameHeader.Write(Outlet, header.Kind, header.Flags, header.Codec,
                    header.CompositionTime, header.Length, header.Timestamp);
                var buff = Outlet.GetSpan(header.Length);
                payload.CopyTo(buff);
                Outlet.Advance(header.Length);
                IntakeReader.AdvanceTo(payload.End);

                // media is flowing, so the stream key is resolvable now
                if (registeredKey is null)
                {
                    registeredKey = StreamKeys.Sanitize(_context.StreamName);
                    _hub.Register(registeredKey, this);
                }

                // stamp queued events with the timeline position we just passed
                while (_events.Reader.TryRead(out (string Name, string Value) e))
                {
                    byte[] tag = Id3Tag.BuildTxxx(e.Name, e.Value);
                    MediaFrameHeader.Write(Outlet, MediaFrameKind.Data, MediaFrameFlags.None,
                        (uint)DataCodec.Id3, 0, tag.Length, header.Timestamp);
                    var tagBuff = Outlet.GetSpan(tag.Length);
                    tag.CopyTo(tagBuff);
                    Outlet.Advance(tag.Length);
                    Logger.ZLogDebug($"Injected metadata `{e.Name}` at tick {header.Timestamp} (90 kHz)");
                }

                await Outlet.FlushAsync(CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            _events.Writer.TryComplete();
            if (registeredKey is not null)
            {
                _hub.Unregister(registeredKey, this);
            }
            await IntakeReader.CompleteAsync().ConfigureAwait(false);
            await Outlet.CompleteAsync().ConfigureAwait(false);
        }
    }
}
