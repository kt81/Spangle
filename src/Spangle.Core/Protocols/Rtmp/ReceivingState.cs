namespace Spangle.Protocols.Rtmp;

public enum ReceivingState
{
    HandShaking,
    WaitingConnect,
    WaitingFCPublish,
    WaitingPublish,
    Publishing,
    Terminated,
}
