namespace Sequin.Rtmp.Chunk;

internal enum MessageType: byte
{
    #region Protocol Control Messages
    
    SetChunkSize              = 1,
    Abort                     = 2,
    Acknowledgement           = 3,
    UserControl               = 4,
    WindowAcknowledgementSize = 5,
    SetPeerBandwidth          = 6,
    
    #endregion
    
    #region RTMP Command Messages
    
    Audio = 8,
    Video = 9,
    
    // AMF3
    DataAmf3         = 15,
    SharedObjectAmf3 = 16,
    CommandAmf3      = 17,
    
    // AMF0
    DataAmf0         = 18,
    SharedObjectAmf0 = 19,
    CommandAmf0      = 20,
    
    Aggregate = 22,
    
    #endregion
}
