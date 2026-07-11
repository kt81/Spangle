using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using Spangle.Codecs.Id3;
using Spangle.Interop;
using ZLogger;
using static Spangle.Transport.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Spinner;

/// <summary>
/// Turns AMF0 data events (<c>MediaFrameKind.Data</c> / <see cref="DataCodec.Amf0"/>)
/// into timed ID3 tags — the canonical timed-metadata form the HLS outputs carry
/// (ID3-in-PES for TS, ID3-in-emsg for CMAF). The event becomes one TXXX frame:
/// description = the event name, value = the arguments serialized as JSON.
/// Every other frame passes through unchanged.
/// </summary>
public sealed class AmfDataToId3Spinner : SpinnerBase<AmfDataToId3Spinner>
{
    /// <summary>For chain composition: the Outlet is wired by the LiveContext.</summary>
    public AmfDataToId3Spinner(CancellationToken ct) : base(ct)
    {
    }

    public AmfDataToId3Spinner(PipeWriter anotherIntake, CancellationToken ct) : base(anotherIntake, ct)
    {
    }

    public override async ValueTask SpinAsync()
    {
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
                var header = BufferMarshal.AsRefOrCopy<MediaFrameHeader>(headerBuff);
                IntakeReader.AdvanceTo(headerBuff.End);

                if (header.Length < 0)
                {
                    throw new InvalidDataException($"Broken media frame length: {header.Length}");
                }

                result = await IntakeReader.ReadAtLeastAsync(header.Length, CancellationToken).ConfigureAwait(false);
                if (result.Buffer.Length < header.Length)
                {
                    break; // intake completed halfway; drop the partial frame
                }
                var payload = result.Buffer.Slice(0, header.Length);

                if (header.Kind == MediaFrameKind.Data && header.DataCodec == DataCodec.Amf0)
                {
                    TransformToId3(in header, payload);
                }
                else
                {
                    PassThrough(in header, payload);
                }

                IntakeReader.AdvanceTo(payload.End);
                await Outlet.FlushAsync(CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            await IntakeReader.CompleteAsync().ConfigureAwait(false);
            await Outlet.CompleteAsync().ConfigureAwait(false);
        }
    }

    private void PassThrough(in MediaFrameHeader header, in ReadOnlySequence<byte> payload)
    {
        MediaFrameHeader.Write(Outlet, header.Kind, header.Flags, header.Codec,
            header.CompositionTimeMs, header.Length, header.Timestamp);
        var buff = Outlet.GetSpan(header.Length);
        payload.CopyTo(buff);
        Outlet.Advance(header.Length);
    }

    private void TransformToId3(in MediaFrameHeader header, in ReadOnlySequence<byte> payload)
    {
        byte[] tag;
        try
        {
            ReadOnlySequence<byte> buff = payload;
            string eventName = ParseString(ref buff);

            // arguments: one value → itself; several → a JSON array
            var args = new List<object?>();
            while (!buff.IsEmpty)
            {
                args.Add(Parse(ref buff));
            }
            string json = args.Count switch
            {
                0 => "{}",
                1 => JsonSerializer.Serialize(args[0]),
                _ => JsonSerializer.Serialize(args),
            };

            tag = Id3Tag.BuildTxxx(eventName, json);
            Logger.ZLogDebug($"Data event `{eventName}` -> ID3 TXXX ({tag.Length} bytes)");
        }
        catch (Exception e) when (e is InvalidDataException or NotImplementedException)
        {
            Logger.ZLogWarning($"Undecodable AMF0 data event dropped: {e.Message}");
            return;
        }

        MediaFrameHeader.Write(Outlet, MediaFrameKind.Data, MediaFrameFlags.None,
            (uint)DataCodec.Id3, 0, tag.Length, header.Timestamp);
        var outBuff = Outlet.GetSpan(tag.Length);
        tag.CopyTo(outBuff);
        Outlet.Advance(tag.Length);
    }
}
