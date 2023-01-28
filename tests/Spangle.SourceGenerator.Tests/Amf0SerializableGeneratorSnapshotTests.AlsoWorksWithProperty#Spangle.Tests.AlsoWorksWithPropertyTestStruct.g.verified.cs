﻿//HintName: Spangle.Tests.AlsoWorksWithPropertyTestStruct.g.cs
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

internal partial struct AlsoWorksWithPropertyTestStruct
{
    /// <summary>
    /// Write this structure itself to the buffer.
    /// </summary>
    public int WriteAsAmf0Command(IBufferWriter<byte> writer)
    {
        int total = 0;

        total += Amf0Writer.WriteNumber(writer, Convert.ToDouble(IntField));
        total += Amf0Writer.WriteNumber(writer, Convert.ToDouble(FloatField));
        total += Amf0Writer.WriteString(writer, StringField);

        return total;
    }

    /// <summary>
    /// Serialize this struct as an AMF0 byte sequence.
    /// </summary>
    public ReadOnlySpan<byte> ToBytes()
    {
        var writer = new ArrayBufferWriter<byte>(1024);
        WriteAsAmf0Command(writer);
        return writer.WrittenSpan;
    }
}
