﻿//HintName: Spangle.Tests.GeneratesPartialSerializerCorrectlyTestStruct.g.cs
// <auto-generated/>
#nullable enable
#pragma warning disable CS8600
#pragma warning disable CS8601
#pragma warning disable CS8602
#pragma warning disable CS8603
#pragma warning disable CS8604

using System.Buffers;
using Spangle.Rtmp.Amf0;

namespace Spangle.Tests;

internal partial struct GeneratesPartialSerializerCorrectlyTestStruct : IAmf0Serializable
{
    /// <summary>
    /// Serialize this struct as an AMF0 byte sequence.
    /// </summary>
    public int WriteBytes(IBufferWriter<byte> writer)
    {
        var total = 0;
        total += Amf0Writer.WriteNumber(writer, Convert.ToDouble(IntField));
        total += Amf0Writer.WriteNumber(writer, Convert.ToDouble(FloatField));
        total += Amf0Writer.WriteString(writer, StringField);
        return total;
    }
}