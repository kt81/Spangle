using System.Buffers;
using System.Runtime.InteropServices;
using Spangle.Interop;
using Spangle.IO;
using Spangle.LusterBits;

namespace Spangle.Codecs.AVC;

/// <summary>
/// Map of AVCDecoderConfigurationRecord
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
internal unsafe struct AVCDecoderConfigurationRecord
{
    public const int Size = 6;

    /// <summary>
    /// configurationVersion; normally be 1
    /// </summary>
    public byte ConfigurationVersion;

    /// <summary>
    /// AVCProfileIndication
    /// </summary>
    public AVCProfile AVCProfile;

    /// <summary>
    /// profile_compatibility
    /// </summary>
    public byte ProfileCompatibility;

    /// <summary>
    /// AVCLevelIndication
    /// </summary>
    public AVCLevel AVCLevel;

    /// <summary>
    /// Length Size of NAL Units
    /// </summary>
    public int LengthSize => (_lengthSizeMinusOne & 0b11) + 1; // drop unused `reserved` bits

    private byte _lengthSizeMinusOne;

    /// <summary>
    /// numOfSequenceParameterSets
    /// </summary>
    public int NumOfSequenceParameterSets => _numOfSequenceParameterSets & 0x1F; // drop unused `reserved` bits

    private byte _numOfSequenceParameterSets;

    //
    // Variable parts cannot be mapped to struct and no need.
    //

    public Span<byte> AsSpan()
    {
        var span = MemoryMarshal.CreateSpan(ref this, 1);
        return MemoryMarshal.Cast<AVCDecoderConfigurationRecord, byte>(span);
    }
}

// [LusterCharm]
// [StructLayout(LayoutKind.Sequential, Pack = 1, Size = MaxSize)]
// public unsafe partial struct SequenceParameterSet
// {
//     public const int MaxSize = 0xFFFF;
//     public const int FixedSize = 4;
//
//     [
//         BitField(typeof(byte), "NALRefIdc", 3, description: "nal_ref_idc, containing forbidden_zero_bit"),
//         BitField(typeof(byte), "NalUnitType", 5, description: "nal_unit_type"),
//         BitField(typeof(byte), "ProfileIdc", 8, description: "profile_idc"),
//         BitField(typeof(byte), "ConstraintSet0Flag", 1, description: "constraint_set0_flag"),
//         BitField(typeof(byte), "ConstraintSet1Flag", 1, description: "constraint_set1_flag"),
//         BitField(typeof(byte), "ConstraintSet2Flag", 1, description: "constraint_set2_flag"),
//         BitField(typeof(byte), "ConstraintSet3Flag", 1, description: "constraint_set3_flag"),
//         BitField(typeof(byte), "ReservedZero4Bits", 4, description: "reserved_zero_4bits"),
//         BitField(typeof(byte), "LevelIdc", 8, description: "level_idc"),
//     ]
//     private fixed byte _fixed[FixedSize];
//     private fixed byte _nalu[MaxSize - FixedSize];
// }

internal enum AVCProfile : byte
{
    Baseline                     = 0x42,
    Main                         = 0x4D,
    Extended                     = 0x58,
    High                         = 0x64,
    High10                       = 0x6E,
    High422                      = 0x7A,
    High444                      = 0xF4,
    High444Predictive            = 0xF5,
    ScalableBaseline             = 0x50,
    ScalableHigh                 = 0x7C,
    ScalableHighIntra            = 0x7D,
    StereoHigh                   = 0xC0,
    MultiviewHigh                = 0xD0,
    MultiviewDepthHigh           = 0xD1,
    OldHigh                      = 0x32,
    OldHigh10                    = 0x33,
    OldHigh422                   = 0x34,
    OldHigh444                   = 0x35,
    OldHigh444Predictive         = 0x3C,
    OldHigh10Intra               = 0x3D,
    OldHigh422Intra              = 0x3E,
    OldHigh444Intra              = 0x3F,
    OldCavlc444Intra             = 0x40,
    OldScalableHigh              = 0x30,
    OldScalableHighIntra         = 0x31,
    OldStereoHigh                = 0x34,
    OldMultiviewHigh             = 0x35,
    OldStereoHigh422             = 0x36,
    OldMultiviewDepthHigh        = 0x37,
    OldStereoHigh444             = 0x38,
    OldStereoHigh444Predictive   = 0x39,
    OldExtended                  = 0x3A,
    OldCavlc444                  = 0x3B,
    OldScalableBaseline          = 0x40,
    OldScalableExtended          = 0x41,
    OldScalableHigh422           = 0x42,
    OldScalableHigh444           = 0x43,
    OldScalableHigh444Predictive = 0x44,
    OldScalableCavlc444          = 0x45,
    OldScalableCavlc444Intra     = 0x46,
    OldScalableNbit              = 0x47,
    OldScalableNbitIntra         = 0x48,
}

internal enum AVCLevel : byte
{
    Level1   = 0x0A,
    Level1b  = 0x16,
    Level11  = 0x1E,
    Level12  = 0x26,
    Level13  = 0x2E,
    Level2   = 0x36,
    Level21  = 0x3E,
    Level22  = 0x46,
    Level3   = 0x4E,
    Level31  = 0x56,
    Level32  = 0x5E,
    Level4   = 0x66,
    Level41  = 0x6E,
    Level42  = 0x76,
    Level5   = 0x7E,
    Level51  = 0x86,
    Level52  = 0x8E,
    Level6   = 0x96,
    Level61  = 0x9E,
    Level62  = 0xA6,
    Level6_1 = 0xAE,
    Level6_2 = 0xB6,
    Level8_5 = 0xCE,
}
