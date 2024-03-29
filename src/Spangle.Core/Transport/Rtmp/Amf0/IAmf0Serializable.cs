﻿using System.Buffers;

namespace Spangle.Transport.Rtmp.Amf0;

/// <summary>
/// AMF0 Serializable
/// </summary>
public interface IAmf0Serializable
{
    int WriteBytes(IBufferWriter<byte> writer);
}
