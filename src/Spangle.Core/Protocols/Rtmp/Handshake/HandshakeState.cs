namespace Spangle.Protocols.Rtmp.Handshake;

internal enum HandshakeState
{
    Uninitialized = 0,
    VersionSent,
    AckSent,
    HandshakeDone,
}
