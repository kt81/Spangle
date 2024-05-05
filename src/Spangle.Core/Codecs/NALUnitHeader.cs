using Spangle.LusterBits;

namespace Spangle.Codecs;

[LusterCharm]
public unsafe partial struct NALUnitHeader
{
    [
        BitField(typeof(byte), "ForbiddenZeroBit", 1),
        BitField(typeof(byte), "NALRefIDC", 2),
        BitField(typeof(NALUnitType), "Type", 5)
    ]
    private fixed byte _val[1];
}

public enum NALUnitType : byte
{
    Unspecified                   = 0,
    CodedSliceNonIDR              = 1,
    CodedSliceDataPartitionA      = 2,
    CodedSliceDataPartitionB      = 3,
    CodedSliceDataPartitionC      = 4,
    CodedSliceIDR                 = 5,
    SEI                           = 6,
    SPS                           = 7,
    PPS                           = 8,
    AUD                           = 9,
    EndOfSequence                 = 10,
    EndOfStream                   = 11,
    FillerData                    = 12,
    SequenceParameterSetExtension = 13,
    PrefixNALUnit                 = 14,
    SubsetSPS                     = 15,
    Reserved16                    = 16,
    Reserved17                    = 17,
    Reserved18                    = 18,
    CodedSliceAux                 = 19,
    CodedSliceExtension           = 20,
    CodedSliceExtensionForDepth   = 21,
    Reserved22                    = 22,
    Reserved23                    = 23,
    // 24- Unspecified...
}
