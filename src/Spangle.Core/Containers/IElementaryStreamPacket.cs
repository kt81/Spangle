namespace Spangle.Containers;

public interface IElementaryStreamPacket
{
    public PacketType PacketType { get; }
    public Span<byte> Payload { get; }

}

public enum PacketType : byte
{
    Invalid = 0,
    Audio,
    Video,
    Data,
}
