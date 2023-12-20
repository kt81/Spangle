using Spangle.Interop;

namespace Spangle.Containers.ADTS;

public unsafe partial struct ADTSHeader
{
    private fixed byte _data[6];

    public bool IsSyncValid() =>
        _data[0] == 0b1111_1111
        && ((_data[1] >>> 4) == 0b1111);

    public ADTS_MPEG_ID ID => (ADTS_MPEG_ID)((_data[1] >>> 3) & 0b1);
    public byte Layer => (byte)((_data[1] >>> 1) & 0b11); // always zero
    public ADTS_ProtectionAbsent ProtectionAbsent => (ADTS_ProtectionAbsent)(_data[2] & 0b1);
    public ADTS_Profile Profile => (ADTS_Profile)(_data[2] >>> 6);
    public ADTS_SamplingFrequencyIndex SamplingFrequencyIndex => (ADTS_SamplingFrequencyIndex)((_data[2] >>> 2) & 0b1111);
    public byte PrivateBit => (byte)((_data[2] >>> 1) & 0b1);
    public byte ChannelConfiguration => (byte)(((_data[2] & 0b1) << 2) + ((_data[3] >>> 6) & 0b11));
}

public enum ADTS_MPEG_ID : byte
{
    MPEG4 = 0,
    MPEG2 = 1,
}

public enum ADTS_ProtectionAbsent : byte
{
    None      = 0,
    Protected = 1,
}

public enum ADTS_Profile : byte
{
    Main     = 0b00,
    LC       = 0b01,
    SSR      = 0b10,

    Reserved = 0b11,
}

public enum ADTS_SamplingFrequencyIndex : byte
{
    Freq96000 = 0,
    Freq88200,
    Freq64000,
    Freq48000,
    Freq44100, // = 4
    Freq32000,
    Freq24000,
    Freq22050,
    Freq16000,
    Freq12000,
    Freq11025,
    Freq8000, // = 11
}


public class SomeFieldAttribute : Attribute
{

}
