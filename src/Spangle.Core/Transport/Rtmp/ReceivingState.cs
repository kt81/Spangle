namespace Spangle.Transport.Rtmp;

public enum ReceivingState
{
    HandShaking,
    WaitingConnect,
    WaitingFCPublish,
    WaitingPublish,
    Publishing,
    Terminated,
}
