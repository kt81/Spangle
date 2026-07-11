using System.Buffers;

namespace Spangle.Codecs;

public static unsafe class NALAnnexB
{
    public const int NALUIndicatorSize = 3;

    public static int WriteNALU(IBufferWriter<byte> buff, ReadOnlySequence<byte> nalu)
    {
        int l = WriteNALUIndicator(buff);
        nalu.CopyTo(buff.GetSpan((int)nalu.Length));
        buff.Advance((int)nalu.Length);
        return l + (int)nalu.Length;
    }

    public static int WriteNALUIndicator(IBufferWriter<byte> buff)
    {
        // Always give 3 bytes type for NALU indicator
        var span = buff.GetSpan(NALUIndicatorSize);
        span[0] = 0x00;
        span[1] = 0x00;
        span[2] = 0x01;
        buff.Advance(NALUIndicatorSize);
        return NALUIndicatorSize;
    }

    /// <summary>
    /// Enumerates the NAL units of an Annex B byte stream (3- and 4-byte start codes)
    /// without allocating. Each element is the NALU body, start code excluded.
    /// </summary>
    public static NaluEnumerator EnumerateNALUs(ReadOnlySpan<byte> annexB) => new(annexB);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1034:Nested types should not be visible",
        Justification = "allocation-free foreach requires the ref-struct enumerator to be visible; nesting scopes it to its factory")]
    public ref struct NaluEnumerator(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _rest = data;

        public ReadOnlySpan<byte> Current { get; private set; }

        public readonly NaluEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (true)
            {
                int start = IndexOfStartCode(_rest);
                if (start < 0)
                {
                    return false;
                }
                ReadOnlySpan<byte> fromNalu = _rest[(start + NALUIndicatorSize)..];

                int next = IndexOfStartCode(fromNalu);
                if (next < 0)
                {
                    Current = fromNalu;
                    _rest = default;
                    return Current.Length > 0;
                }

                // a 4-byte start code owns the zero byte before its 00 00 01
                int end = next > 0 && fromNalu[next - 1] == 0x00 ? next - 1 : next;
                Current = fromNalu[..end];
                _rest = fromNalu[next..];
                if (Current.Length > 0)
                {
                    return true;
                }
                // zero-length NALU (e.g. duplicated start codes): keep scanning
            }
        }

        private static int IndexOfStartCode(ReadOnlySpan<byte> span)
        {
            for (var i = 0; i + NALUIndicatorSize <= span.Length; i++)
            {
                if (span[i] == 0x00 && span[i + 1] == 0x00 && span[i + 2] == 0x01)
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
