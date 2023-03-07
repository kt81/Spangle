using System.Buffers;
using System.IO.Pipelines;
using Spangle.Interop;

namespace Spangle.Containers.M2TS;

internal class TSPipeReader
{
    private readonly PipeReader _baseReader;

    public TSPipeReader(PipeReader baseReader)
    {
        _baseReader = baseReader;
    }

    public static ref readonly TSPacket ReadTSPacket(ref ReadOnlySequence<byte> buff)
    {
        if (buff.Length < TSPacket.Size)
        {
            throw new InvalidDataException("The buffer length is lower than fixed TS packet length " + TSPacket.Size);
        }

        return ref BufferMarshal.AsRefOrCopy<TSPacket>(buff);
    }

}
