﻿using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spangle.Containers.M2TS;

/// <summary>
/// Indicates
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = Size)]
internal unsafe struct TSPacket
{
    public const int MaxPayloadSize = 184;
    public const int Size           = TSHeader.Size + MaxPayloadSize;

    // Header
    [FieldOffset(0)]             public readonly  TSHeader                 Header;
    [FieldOffset(TSHeader.Size)] private readonly AdaptationFieldsBasic _adaptationFieldsBasic;

    // Optional fields and/or payload
    [FieldOffset(TSHeader.Size)] private fixed byte _others[MaxPayloadSize];

    public readonly ref readonly AdaptationFieldsBasic AdaptationFieldsBasic
    {
        get
        {
            // Must call HasAdaptationField() before accessing this property.
            Debug.Assert(Header.AdaptationFieldControl.HasAdaptationField(), "The packet has no adaptation fields.");
#pragma warning disable CS9084
            return ref _adaptationFieldsBasic;
#pragma warning restore CS9084
        }
    }

    #region Optional fields for AdaptationFields

    private const int PCROffset = TSHeader.Size + AdaptationFieldsBasic.Size;
    public ref readonly PCR PCR => ref MemoryMarshal.AsRef<PCR>(OthersAsSpan().Slice(PCROffset, PCR.Size));

    private readonly int OPCROffset => PCROffset + (AdaptationFieldsBasic.HasPCR ? PCR.Size : 0);
    public ref readonly PCR OPCR => ref MemoryMarshal.AsRef<PCR>(OthersAsSpan().Slice(OPCROffset, PCR.Size));

    private readonly int SpliceCountdownOffset => OPCROffset + (AdaptationFieldsBasic.HasOPCR ? PCR.Size : 0);
    public readonly byte SpliceCountdown => _others[SpliceCountdownOffset];

    private readonly int TransportPrivateDataOffset => SpliceCountdownOffset + (AdaptationFieldsBasic.HasSplicingPoint ? 1 : 0);
    public readonly byte TransportPrivateDataLength => _others[TransportPrivateDataOffset];

    private readonly int PrivateDataBytesOffset => TransportPrivateDataOffset + (AdaptationFieldsBasic.HasTransportPrivateData ? 1 : 0);

    public readonly ReadOnlySpan<byte> PrivateDataBytes =>
        OthersAsSpan().Slice(PrivateDataBytesOffset, TransportPrivateDataLength);

    #endregion

    #region Adaptation field extensions

    private readonly int AdaptationFieldExtensionOffset =>
        PrivateDataBytesOffset + (AdaptationFieldsBasic.HasTransportPrivateData ? TransportPrivateDataLength : 0);
    public readonly byte AdaptationFieldExtensionLength => _others[AdaptationFieldExtensionOffset];

    private readonly int AdaptationFieldExtensionFlagsOffset =>
        AdaptationFieldExtensionOffset + (AdaptationFieldsBasic.HasAdaptationFieldExtension ? AdaptationFieldExtensionLength : 0);
    public readonly bool HasLtw => _others[AdaptationFieldExtensionFlagsOffset] >>> 7 == 1;
    public readonly bool HasPiecewiseRate => ((_others[AdaptationFieldExtensionFlagsOffset] >>> 6) & 0x01) == 1;
    public readonly bool HasSeamlessSplice => ((_others[AdaptationFieldExtensionFlagsOffset] >>> 5) & 0x01) == 1;
    // -- Reserved 5 bit (rest of _others[AdaptationFieldExtensionFlagsOffset]) --

    private readonly int LtwDataOffset => AdaptationFieldExtensionFlagsOffset + 1;
    public readonly bool IsLtwValid => _others[LtwDataOffset] >>> 7 == 1;
    public readonly ushort LtwOffset => (ushort)(((_others[LtwDataOffset] & 0x7F) << 8) + _others[LtwDataOffset + 1]);

    private readonly int PiecewiseRateDataOffset => LtwDataOffset + (HasLtw ? 2 : 0);
    // -- Reserved 2 bit (top of _others[PiecewiseRateDataOffset]) --
    public readonly uint PiecewiseRate => (uint)(
        ((_others[PiecewiseRateDataOffset] & 0x3F) << 16)
        + (_others[PiecewiseRateDataOffset + 1] << 8)
        + _others[PiecewiseRateDataOffset + 2]
    );

    private readonly int SeamlessSpliceDataOffset => PiecewiseRateDataOffset + (HasPiecewiseRate ? 3 : 0);
    public readonly byte SpliceType => (byte)(_others[SeamlessSpliceDataOffset] >>> 4);
    // marker_bit included
    public readonly byte DTSNextAU32To30 => (byte)(_others[SeamlessSpliceDataOffset] & 0b1111);
    public readonly ReadOnlySpan<byte> DTSNextAU29To15 => OthersAsSpan().Slice(SeamlessSpliceDataOffset + 1, 2);
    public readonly ReadOnlySpan<byte> DTSNextAU14To0 => OthersAsSpan().Slice(SeamlessSpliceDataOffset + 2, 2);

    #endregion

    private readonly ReadOnlySpan<byte> OthersAsSpan()
    {
        fixed (void* p = _others)
        {
            return new ReadOnlySpan<byte>(p, Size);
        }
    }
}

