using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;
using Spangle.Rtmp.Chunk;

namespace Spangle.Rtmp.ReadState;

/// <summary>
///     ReadBasicHeader
///     <see cref="BasicHeader" />
/// </summary>
internal abstract class ReadBasicHeader : IReadStateAction
{
    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;

        // Check first byte
        (ReadOnlySequence<byte> firstBuff, _) = await reader.ReadExactlyAsync(1, ct);
        (byte fmt, int headerLength, byte checkBits) =
            BasicHeader.GetFormatAndLengthByFirstByte(firstBuff.FirstSpan[0]);
        reader.AdvanceTo(firstBuff.End);

        uint csId;
        // Read the remaining buffer if needed
        if (headerLength > 1)
        {
            (ReadOnlySequence<byte> exBuff, _) = await reader.ReadExactlyAsync(headerLength - 1, ct);
            csId = GetCsId(exBuff, headerLength);
            reader.AdvanceTo(exBuff.End);
        }
        else
        {
            // 1 byte version
            csId = checkBits;
        }

        context.BasicHeader.Renew(fmt, csId);
        context.SetNext<ReadMessageHeader>();
    }

    private static uint GetCsId(ReadOnlySequence<byte> buff, int headerLength)
    {
        ReadOnlySpan<byte> csIdBytes = buff.FirstSpan;
        return headerLength switch
        {
            // 3-bytes version
            3 => (uint)(csIdBytes[0] << 8) + csIdBytes[1] + 64,
            // 2-bytes version
            2 => (uint)(csIdBytes[0] + 64),
            // not reachable
            _ => throw new Exception()
        };
    }
}
