namespace Spangle.Transport.Rtmp.ProtocolControlMessage;

public enum MessageType : byte
{
    #region Protocol Control Messages

    SetChunkSize              = 0x01,
    Abort                     = 0x02,
    Acknowledgement           = 0x03,
    UserControl               = 0x04,
    WindowAcknowledgementSize = 0x05,
    SetPeerBandwidth          = 0x06,

    #endregion

    #region RTMP Command Messages

    Audio = 0x08,
    Video = 0x09,

    // AMF3
    DataAmf3         = 0x0A,
    SharedObjectAmf3 = 0x10,
    CommandAmf3      = 0x11,

    // AMF0
    DataAmf0         = 0x12,
    SharedObjectAmf0 = 0x13,
    CommandAmf0      = 0x14,

    Aggregate = 0x18,

    // Additional Feature(s)
    GoAway = 0x20,

    #endregion
}
