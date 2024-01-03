using Spangle.LusterBits;

namespace Spangle.Containers.ADTS;

[LusterCharm]
public unsafe partial struct ADTSHeader
{
    public const ushort ValidSyncword = 0b1111_1111_1111;

    [
        BitField(typeof(ushort), "Syncword", 12),
        BitField(typeof(ADTS_MPEG_ID), "ID", 1, description: "MPEG identifier; 0 = MPEG-4, 1 = MPEG-2"),
        BitField(typeof(byte), "Layer", 2, description: "Always 0"),
        BitField(typeof(byte), "ProtectionAbsent", 1, description: "Protection; 0 = CRC present, 1 = no CRC"),
        BitField(typeof(ADTS_Profile), "Profile", 2, description: "Profile; 0 = Main, 1 = LC, 10 = SSR, 11 = reserved"),
        BitField(typeof(ADTS_SamplingFrequencyIndex), "SamplingFrequencyIndex", 4, description: "Sampling frequency index"),
        BitField(typeof(bool), "IsPrivate", 1, description: "Private bit; 0 = private bit not set, 1 = private bit set"),
        BitField(typeof(byte), "ChannelConfiguration", 3, description: "Number of channels"),
        BitField(typeof(byte), "OriginalCopy", 1, description: "Original or copy; 0 = original, 1 = copy"),
        BitField(typeof(bool), "IsHome", 1, description: "Home use or not"),
        BitField(typeof(byte), "CopyrightID", 1, description: "Circular buffer of copyright information"),
        BitField(typeof(bool), "IsFirstCopyrightID", 1, description: "CopyrightID is the first one"),
        BitField(typeof(ushort), "FrameLength", 13, description: "Length of frame including header"),
        BitField(typeof(ushort), "BufferFullness", 11, description: "Number of bits left in the bit reservoir"),
        BitField(typeof(byte), "NumberOfRawDataBlocksInFrame", 2, description: "Number of raw data blocks in frame"),
        BitField(typeof(byte), "CRC", 16, description: "CRC check word. Only if ProtectionAbsent is 0"),
    ]
    private fixed byte _data[9];

    public bool IsSyncValid() => Syncword == ValidSyncword;
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
    Freq44100,
    Freq32000, // = 0b0101
    Freq24000,
    Freq22050,
    Freq16000,
    Freq12000,
    Freq11025,
    Freq8000, // = 0b1011
}
