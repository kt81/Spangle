namespace Spangle.Rtmp;

public enum ReceivingState
{
    HandShaking,
    WaitingConnect,
    WaitingFCPublish,
    WaitingPublish,
    Established,
    Terminated,
}
